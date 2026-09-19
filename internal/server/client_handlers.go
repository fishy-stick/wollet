package server

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/fishy-stick/wollet/internal/events"
	"github.com/fishy-stick/wollet/internal/identity"
	"github.com/fishy-stick/wollet/internal/protocol"
	"github.com/fishy-stick/wollet/internal/store"
	"github.com/fishy-stick/wollet/internal/validation"
)

type bindRequest struct {
	Token      string `json:"token"`
	DeviceName string `json:"deviceName"`
	MACAddress string `json:"macAddress"`
}

func (s *Server) handleBindClient(w http.ResponseWriter, r *http.Request) {
	if !s.bindLimiter.Allow(remoteIP(r), time.Now().UTC()) {
		writeError(w, http.StatusTooManyRequests, "too_many_attempts", "绑定尝试过于频繁，请稍后再试")
		return
	}
	var request bindRequest
	if err := decodeJSON(w, r, &request); err != nil {
		writeError(w, http.StatusBadRequest, "invalid_request", "绑定信息格式无效")
		return
	}
	token, err := identity.NormalizePairingToken(request.Token)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "invalid_or_expired_token", "Token 无效或已过期")
		return
	}
	name, err := validation.DeviceName(request.DeviceName)
	if err != nil {
		writeError(w, http.StatusBadRequest, "invalid_device_name", "设备名称无效")
		return
	}
	mac, err := validation.MACAddress(request.MACAddress)
	if err != nil {
		writeError(w, http.StatusBadRequest, "invalid_mac_address", "MAC 地址无效")
		return
	}
	deviceID, err := identity.NewUUID()
	if err != nil {
		writeError(w, http.StatusInternalServerError, "identity_generation_failed", "无法创建设备身份")
		return
	}
	secret, err := identity.NewSecret()
	if err != nil {
		writeError(w, http.StatusInternalServerError, "identity_generation_failed", "无法创建设备身份")
		return
	}
	credentialHash := identity.Hash(secret)
	now := time.Now().UTC()
	consumed, err := s.store.ConsumePairingToken(r.Context(), identity.Hash(token), now, store.Device{
		ID: deviceID, CredentialHash: credentialHash[:], Name: name, MACAddress: mac,
		CreatedAt: now, UpdatedAt: now,
	})
	if err != nil {
		s.logger.Error("bind client", "error", err)
		writeError(w, http.StatusInternalServerError, "binding_failed", "设备绑定失败")
		return
	}
	if !consumed {
		writeError(w, http.StatusUnauthorized, "invalid_or_expired_token", "Token 无效或已过期")
		return
	}
	s.broker.Publish(events.Event{Type: "device.updated", DeviceID: deviceID})
	writeJSON(w, http.StatusCreated, map[string]string{"deviceId": deviceID, "deviceSecret": secret})
}

func (s *Server) handleClientMe(w http.ResponseWriter, r *http.Request) {
	device, ok := s.authenticateClient(w, r)
	if !ok {
		return
	}
	writeJSON(w, http.StatusOK, s.view(device))
}

func (s *Server) handleClientConnect(w http.ResponseWriter, r *http.Request) {
	device, ok := s.authenticateClient(w, r)
	if !ok {
		return
	}
	conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{CompressionMode: websocket.CompressionDisabled})
	if err != nil {
		s.logger.Debug("accept device WebSocket", "device_id", device.ID, "error", err)
		return
	}
	defer conn.CloseNow()
	conn.SetReadLimit(4 << 10)

	helloCtx, cancelHello := context.WithTimeout(r.Context(), s.cfg.HelloTimeout)
	var hello protocol.ClientMessage
	err = wsjson.Read(helloCtx, conn, &hello)
	cancelHello()
	if err != nil {
		_ = conn.Close(websocket.StatusPolicyViolation, "hello timeout or invalid message")
		return
	}
	if hello.Type != "hello" || hello.ProtocolVersion != protocol.Version {
		_ = conn.Close(websocket.StatusPolicyViolation, "unsupported protocol")
		return
	}
	name, err := validation.DeviceName(hello.DeviceName)
	if err != nil {
		_ = conn.Close(websocket.StatusPolicyViolation, "invalid device name")
		return
	}
	mac, err := validation.MACAddress(hello.MACAddress)
	if err != nil {
		_ = conn.Close(websocket.StatusPolicyViolation, "invalid MAC address")
		return
	}
	now := time.Now().UTC()
	if err := s.store.UpdateDeviceConnection(r.Context(), device.ID, name, mac, now); err != nil {
		_ = conn.Close(websocket.StatusInternalError, "cannot update device")
		return
	}
	var planConn *planConnection
	for _, capability := range hello.Capabilities {
		if capability == protocol.ShutdownPlanCapability {
			session, err := identity.NewUUID()
			if err != nil {
				return
			}
			planConn = &planConnection{socket: conn, session: session}
			break
		}
	}
	var capabilities []string
	var sessionID string
	if planConn != nil {
		capabilities = []string{protocol.ShutdownPlanCapability}
		sessionID = planConn.session
		s.plans.mu.Lock()
		s.plans.connections[device.ID] = planConn
		s.plans.mu.Unlock()
		defer func() {
			s.plans.mu.Lock()
			if s.plans.connections[device.ID] == planConn {
				delete(s.plans.connections, device.ID)
			}
			s.plans.mu.Unlock()
		}()
	}
	readyCtx, cancelReady := context.WithTimeout(r.Context(), 5*time.Second)
	err = wsjson.Write(readyCtx, conn, protocol.ServerMessage{
		Type: "ready", ProtocolVersion: protocol.Version, Capabilities: capabilities, SessionID: sessionID,
		HeartbeatIntervalSeconds: int(s.cfg.HeartbeatEvery.Seconds()),
		OfflineAfterSeconds:      int(s.cfg.OfflineAfter.Seconds()),
	})
	cancelReady()
	if err != nil {
		return
	}

	connection := s.hub.Register(device.ID, conn, now)
	defer s.hub.Unregister(connection, time.Now().UTC())
	for {
		var message protocol.ClientMessage
		if err := wsjson.Read(r.Context(), conn, &message); err != nil {
			if status := websocket.CloseStatus(err); status == -1 {
				s.logger.Debug("device WebSocket read failed", "device_id", device.ID, "error", err)
			}
			return
		}
		if strings.HasPrefix(message.Type, "shutdown_plan_") && planConn != nil {
			if err := s.receivePlanMessage(device.ID, planConn, message); err != nil {
				s.logger.Warn("plan message rejected", "error", err)
				_ = conn.Close(websocket.StatusPolicyViolation, "invalid plan message")
				return
			}
			continue
		}
		switch message.Type {
		case "heartbeat":
			if !s.hub.Heartbeat(connection, time.Now().UTC()) {
				return
			}
		case "shutdown_ack":
			if message.CommandID == "" || !s.hub.Acknowledge(connection, message.CommandID) {
				_ = conn.Close(websocket.StatusPolicyViolation, "unexpected command acknowledgement")
				return
			}
		default:
			_ = conn.Close(websocket.StatusPolicyViolation, "unsupported message type")
			return
		}
	}
}

func (s *Server) authenticateClient(w http.ResponseWriter, r *http.Request) (store.Device, bool) {
	deviceID := strings.TrimSpace(r.Header.Get("X-Wollet-Device-ID"))
	authorization := strings.TrimSpace(r.Header.Get("Authorization"))
	if deviceID == "" || !strings.HasPrefix(authorization, "Bearer ") {
		writeError(w, http.StatusUnauthorized, "invalid_device_credentials", "设备凭据无效")
		return store.Device{}, false
	}
	secret := strings.TrimSpace(strings.TrimPrefix(authorization, "Bearer "))
	device, err := s.store.AuthenticateDevice(r.Context(), deviceID, secret)
	if errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusUnauthorized, "invalid_device_credentials", "设备凭据无效")
		return store.Device{}, false
	}
	if err != nil {
		s.logger.Error("authenticate device", "device_id", deviceID, "error", err)
		writeError(w, http.StatusInternalServerError, "authentication_error", "设备认证失败")
		return store.Device{}, false
	}
	return device, true
}
