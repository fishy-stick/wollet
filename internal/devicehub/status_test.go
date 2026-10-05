package devicehub

import (
	"io"
	"log/slog"
	"testing"
	"time"

	"github.com/fishy-stick/wollet/internal/protocol"
)

func TestConnectionStatusSnapshotAndExpiry(t *testing.T) {
	hub := New(45*time.Second, slog.New(slog.NewTextHandler(io.Discard, nil)), nil)
	start := time.Now()
	ip, uptime := "192.168.1.20", int64(86400000)
	c := &Connection{id: "session-1", deviceID: "device", connectedAt: start, remoteIPAddress: &ip, done: make(chan struct{})}
	hub.connections[c.deviceID] = c
	connection, status := hub.Snapshot(c.deviceID, start.Add(3*time.Second))
	if connection.DurationMs != 3000 || connection.ID != c.id || *connection.RemoteIPAddress != ip || status != nil {
		t.Fatalf("initial snapshot: %+v %+v", connection, status)
	}
	if !hub.HeartbeatStatus(c, &protocol.DeviceStatus{SystemUptimeMs: &uptime}, start.Add(5*time.Second)) {
		t.Fatal("current heartbeat rejected")
	}
	hub.Heartbeat(c, start.Add(20*time.Second)) // Plain heartbeat must not refresh the sample age.
	connection, status = hub.Snapshot(c.deviceID, start.Add(25*time.Second))
	if connection.DurationMs != 25000 || status.AgeMs != 20000 || !status.Fresh || status.ValidForMs != 25000 {
		t.Fatalf("snapshot: %+v %+v", connection, status)
	}
	connection, status = hub.Snapshot(c.deviceID, start.Add(50*time.Second))
	if status.Fresh || status.ValidForMs != 0 || status.AgeMs != 45000 || *connection.RemoteIPAddress != ip {
		t.Fatalf("sample did not expire: %+v", status)
	}
	hub.HeartbeatStatus(c, &protocol.DeviceStatus{}, start.Add(51*time.Second))
	connection, status = hub.Snapshot(c.deviceID, start.Add(52*time.Second))
	if status.SystemUptimeMs != nil || !status.Fresh || *connection.RemoteIPAddress != ip {
		t.Fatalf("unknown sample did not clear fields: %+v", status)
	}
	hub.Unregister(c, start.Add(53*time.Second))
	connection, status = hub.Snapshot(c.deviceID, start.Add(54*time.Second))
	if connection != nil || status != nil {
		t.Fatal("offline retained current metadata")
	}
}

func TestSupersededConnectionCannotRefreshTelemetryOrDisconnectNewSession(t *testing.T) {
	hub := New(time.Minute, slog.New(slog.NewTextHandler(io.Discard, nil)), nil)
	start := time.Now()
	old := &Connection{id: "old", deviceID: "device", connectedAt: start, done: make(chan struct{})}
	ip := "192.168.1.21"
	current := &Connection{id: "new", deviceID: "device", connectedAt: start.Add(10 * time.Second), remoteIPAddress: &ip, done: make(chan struct{})}
	hub.connections[current.deviceID] = current
	if hub.HeartbeatStatus(old, &protocol.DeviceStatus{}, start.Add(11*time.Second)) {
		t.Fatal("old heartbeat accepted")
	}
	hub.Unregister(old, start.Add(12*time.Second))
	connection, status := hub.Snapshot(current.deviceID, start.Add(13*time.Second))
	if connection == nil || connection.ID != "new" || connection.DurationMs != 3000 || *connection.RemoteIPAddress != ip || status != nil {
		t.Fatalf("old connection altered new session: %+v %+v", connection, status)
	}
}
