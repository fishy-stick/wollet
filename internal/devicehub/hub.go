package devicehub

import (
	"context"
	"errors"
	"log/slog"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/fishy-stick/wakelet/internal/protocol"
)

var (
	ErrOffline           = errors.New("device is offline")
	ErrCommandInProgress = errors.New("a command is already in progress")
	ErrDeliveryFailed    = errors.New("command delivery failed")
)

type Activity string

const (
	ActivityOnline    Activity = "online"
	ActivityHeartbeat Activity = "heartbeat"
	ActivityOffline   Activity = "offline"
)

type ActivityHandler func(deviceID string, activity Activity, at time.Time)

type Connection struct {
	deviceID string
	conn     *websocket.Conn
	writeMu  sync.Mutex
	done     chan struct{}
	doneOnce sync.Once
}

func (c *Connection) DeviceID() string      { return c.deviceID }
func (c *Connection) Done() <-chan struct{} { return c.done }

func (c *Connection) write(ctx context.Context, message protocol.ServerMessage) error {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	return wsjson.Write(ctx, c.conn, message)
}

func (c *Connection) finish() {
	c.doneOnce.Do(func() { close(c.done) })
}

func (c *Connection) close(code websocket.StatusCode, reason string) {
	c.finish()
	go func() { _ = c.conn.Close(code, reason) }()
}

type pendingCommand struct {
	deviceID string
	result   chan bool
}

type Hub struct {
	mu              sync.Mutex
	connections     map[string]*Connection
	lastHeartbeat   map[string]time.Time
	pending         map[string]pendingCommand
	pendingByDevice map[string]string
	offlineAfter    time.Duration
	onActivity      ActivityHandler
	logger          *slog.Logger
}

func New(offlineAfter time.Duration, logger *slog.Logger, onActivity ActivityHandler) *Hub {
	return &Hub{
		connections:     make(map[string]*Connection),
		lastHeartbeat:   make(map[string]time.Time),
		pending:         make(map[string]pendingCommand),
		pendingByDevice: make(map[string]string),
		offlineAfter:    offlineAfter,
		onActivity:      onActivity,
		logger:          logger,
	}
}

func (h *Hub) Register(deviceID string, conn *websocket.Conn, now time.Time) *Connection {
	connection := &Connection{deviceID: deviceID, conn: conn, done: make(chan struct{})}
	h.mu.Lock()
	old := h.connections[deviceID]
	h.connections[deviceID] = connection
	h.lastHeartbeat[deviceID] = now
	if old != nil {
		h.failPendingLocked(deviceID)
	}
	h.mu.Unlock()

	if old != nil {
		old.close(websocket.StatusPolicyViolation, "superseded by a newer connection")
	}
	h.notify(deviceID, ActivityOnline, now)
	return connection
}

func (h *Hub) Unregister(connection *Connection, now time.Time) {
	h.mu.Lock()
	current, ok := h.connections[connection.deviceID]
	if !ok || current != connection {
		h.mu.Unlock()
		connection.finish()
		return
	}
	delete(h.connections, connection.deviceID)
	delete(h.lastHeartbeat, connection.deviceID)
	h.failPendingLocked(connection.deviceID)
	h.mu.Unlock()
	connection.finish()
	h.notify(connection.deviceID, ActivityOffline, now)
}

func (h *Hub) Heartbeat(connection *Connection, now time.Time) bool {
	h.mu.Lock()
	current, ok := h.connections[connection.deviceID]
	if ok && current == connection {
		h.lastHeartbeat[connection.deviceID] = now
	}
	h.mu.Unlock()
	if ok && current == connection {
		h.notify(connection.deviceID, ActivityHeartbeat, now)
		return true
	}
	return false
}

func (h *Hub) Acknowledge(connection *Connection, commandID string) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	pending, ok := h.pending[commandID]
	if !ok || pending.deviceID != connection.deviceID || h.connections[connection.deviceID] != connection {
		return false
	}
	delete(h.pending, commandID)
	delete(h.pendingByDevice, pending.deviceID)
	pending.result <- true
	return true
}

func (h *Hub) IsOnline(deviceID string) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	_, ok := h.connections[deviceID]
	return ok
}

func (h *Hub) SendShutdown(ctx context.Context, deviceID, commandID string) error {
	h.mu.Lock()
	connection := h.connections[deviceID]
	if connection == nil {
		h.mu.Unlock()
		return ErrOffline
	}
	if _, exists := h.pendingByDevice[deviceID]; exists {
		h.mu.Unlock()
		return ErrCommandInProgress
	}
	result := make(chan bool, 1)
	h.pending[commandID] = pendingCommand{deviceID: deviceID, result: result}
	h.pendingByDevice[deviceID] = commandID
	h.mu.Unlock()

	if err := connection.write(ctx, protocol.ServerMessage{Type: "shutdown", CommandID: commandID}); err != nil {
		h.cancelPending(commandID)
		return ErrDeliveryFailed
	}

	select {
	case delivered := <-result:
		if !delivered {
			return ErrOffline
		}
		return nil
	case <-ctx.Done():
		h.cancelPending(commandID)
		return ctx.Err()
	case <-connection.Done():
		h.cancelPending(commandID)
		return ErrOffline
	}
}

func (h *Hub) Disconnect(deviceID string, reason string) {
	h.mu.Lock()
	connection := h.connections[deviceID]
	if connection != nil {
		delete(h.connections, deviceID)
		delete(h.lastHeartbeat, deviceID)
		h.failPendingLocked(deviceID)
	}
	h.mu.Unlock()
	if connection != nil {
		connection.close(websocket.StatusPolicyViolation, reason)
	}
}

func (h *Hub) Sweep(now time.Time) {
	var stale []*Connection
	h.mu.Lock()
	for deviceID, last := range h.lastHeartbeat {
		if now.Sub(last) < h.offlineAfter {
			continue
		}
		connection := h.connections[deviceID]
		delete(h.connections, deviceID)
		delete(h.lastHeartbeat, deviceID)
		h.failPendingLocked(deviceID)
		if connection != nil {
			stale = append(stale, connection)
		}
	}
	h.mu.Unlock()
	for _, connection := range stale {
		connection.close(websocket.StatusPolicyViolation, "heartbeat timeout")
		h.notify(connection.deviceID, ActivityOffline, now)
	}
}

func (h *Hub) Close() {
	h.mu.Lock()
	connections := make([]*Connection, 0, len(h.connections))
	for deviceID, connection := range h.connections {
		connections = append(connections, connection)
		h.failPendingLocked(deviceID)
	}
	h.connections = make(map[string]*Connection)
	h.lastHeartbeat = make(map[string]time.Time)
	h.mu.Unlock()
	for _, connection := range connections {
		connection.close(websocket.StatusGoingAway, "server shutting down")
	}
}

func (h *Hub) cancelPending(commandID string) {
	h.mu.Lock()
	pending, ok := h.pending[commandID]
	if ok {
		delete(h.pending, commandID)
		delete(h.pendingByDevice, pending.deviceID)
	}
	h.mu.Unlock()
}

func (h *Hub) failPendingLocked(deviceID string) {
	commandID, ok := h.pendingByDevice[deviceID]
	if !ok {
		return
	}
	pending := h.pending[commandID]
	delete(h.pendingByDevice, deviceID)
	delete(h.pending, commandID)
	pending.result <- false
}

func (h *Hub) notify(deviceID string, activity Activity, at time.Time) {
	if h.onActivity == nil {
		return
	}
	if err := safeNotify(h.onActivity, deviceID, activity, at); err != nil {
		h.logger.Error("device activity callback panicked", "device_id", deviceID, "error", err)
	}
}

func safeNotify(handler ActivityHandler, deviceID string, activity Activity, at time.Time) (err error) {
	defer func() {
		if value := recover(); value != nil {
			err = errors.New("activity callback panic")
		}
	}()
	handler(deviceID, activity, at)
	return nil
}
