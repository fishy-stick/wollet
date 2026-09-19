package server

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"errors"
	"fmt"
	"net/http"
	"time"

	"github.com/fishy-stick/wollet/internal/compatibility"
	"github.com/fishy-stick/wollet/internal/events"
	"github.com/fishy-stick/wollet/internal/identity"
	"github.com/fishy-stick/wollet/internal/store"
)

type loginRequest struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	if !s.authEnabled {
		writeError(w, http.StatusConflict, "authentication_disabled", "管理员认证未启用")
		return
	}
	key := remoteIP(r)
	if !s.loginLimiter.Allow(key, time.Now().UTC()) {
		writeError(w, http.StatusTooManyRequests, "too_many_attempts", "登录尝试过于频繁，请稍后再试")
		return
	}
	var request loginRequest
	if err := decodeJSON(w, r, &request); err != nil {
		writeError(w, http.StatusBadRequest, "invalid_request", "登录信息格式无效")
		return
	}
	usernameHash := sha256.Sum256([]byte(request.Username))
	passwordHash := sha256.Sum256([]byte(request.Password))
	if subtle.ConstantTimeCompare(usernameHash[:], s.adminUserHash[:]) != 1 ||
		subtle.ConstantTimeCompare(passwordHash[:], s.adminPassHash[:]) != 1 {
		writeError(w, http.StatusUnauthorized, "invalid_credentials", "用户名或密码错误")
		return
	}

	token, expiresAt, err := s.sessions.Create(time.Now().UTC())
	if err != nil {
		s.logger.Error("create admin session", "error", err)
		writeError(w, http.StatusInternalServerError, "session_error", "无法创建登录会话")
		return
	}
	s.loginLimiter.Reset(key)
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookieName,
		Value:    token,
		Path:     "/",
		Expires:  expiresAt,
		MaxAge:   int(time.Until(expiresAt).Seconds()),
		HttpOnly: true,
		SameSite: http.SameSiteStrictMode,
		Secure:   false,
	})
	writeJSON(w, http.StatusOK, map[string]any{
		"username": s.cfg.AdminUsername, "authenticationEnabled": true,
	})
}

func (s *Server) handleLogout(w http.ResponseWriter, r *http.Request) {
	if cookie, err := r.Cookie(sessionCookieName); err == nil {
		s.sessions.Delete(cookie.Value)
	}
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookieName,
		Value:    "",
		Path:     "/",
		MaxAge:   -1,
		HttpOnly: true,
		SameSite: http.SameSiteStrictMode,
	})
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleSession(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"username": s.cfg.AdminUsername, "authenticationEnabled": s.authEnabled,
	})
}

func (s *Server) handleCreatePairingToken(w http.ResponseWriter, r *http.Request) {
	display, normalized, err := identity.NewPairingToken()
	if err != nil {
		s.logger.Error("generate pairing token", "error", err)
		writeError(w, http.StatusInternalServerError, "token_generation_failed", "无法生成绑定 Token")
		return
	}
	now := time.Now().UTC()
	expiresAt := now.Add(s.cfg.TokenTTL)
	if err := s.store.CreatePairingToken(r.Context(), identity.Hash(normalized), now, expiresAt); err != nil {
		s.logger.Error("store pairing token", "error", err)
		writeError(w, http.StatusInternalServerError, "token_generation_failed", "无法生成绑定 Token")
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{
		"token": display, "expiresAt": expiresAt, "expiresInSeconds": int(s.cfg.TokenTTL / time.Second),
	})
}

type deviceView struct {
	Compatibility      compatibility.Result `json:"compatibility"`
	ServerVersion      string               `json:"serverVersion"`
	ServerCapabilities []string             `json:"serverCapabilities"`
	Capabilities       []string             `json:"capabilities,omitempty"`
	ShutdownPlan       *planView            `json:"shutdownPlan,omitempty"`
	ShutdownRequest    *planRequest         `json:"shutdownRequest,omitempty"`
	ServerTime         time.Time            `json:"serverTime"`
	ID                 string               `json:"id"`
	Name               string               `json:"name"`
	MACAddress         string               `json:"macAddress"`
	Status             string               `json:"status"`
	Operation          string               `json:"operation,omitempty"`
	LastSeenAt         *time.Time           `json:"lastSeenAt"`
	CreatedAt          time.Time            `json:"createdAt"`
}

func (s *Server) view(device store.Device) deviceView {
	caps, plan, request := s.planDeviceView(device.ID)
	status := "offline"
	if s.hub.IsOnline(device.ID) {
		status = "online"
	}
	return deviceView{
		Compatibility: s.compatibilityView(device.ID), ServerVersion: compatibility.Version, ServerCapabilities: compatibility.ServerCapabilities(),
		Capabilities: caps, ShutdownPlan: plan, ShutdownRequest: request, ServerTime: time.Now().UTC(),
		ID: device.ID, Name: device.Name, MACAddress: device.MACAddress,
		Status: status, Operation: s.operations.get(device.ID),
		LastSeenAt: device.LastSeenAt, CreatedAt: device.CreatedAt,
	}
}

func (s *Server) listDeviceViews(ctx context.Context) ([]deviceView, error) {
	devices, err := s.store.ListDevices(ctx)
	if err != nil {
		return nil, err
	}
	views := make([]deviceView, 0, len(devices))
	for _, device := range devices {
		views = append(views, s.view(device))
	}
	return views, nil
}

func (s *Server) handleListDevices(w http.ResponseWriter, r *http.Request) {
	devices, err := s.listDeviceViews(r.Context())
	if err != nil {
		s.logger.Error("list devices", "error", err)
		writeError(w, http.StatusInternalServerError, "database_error", "无法读取设备列表")
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"devices": devices})
}

func (s *Server) handleEvents(w http.ResponseWriter, r *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		writeError(w, http.StatusInternalServerError, "stream_unsupported", "服务器不支持状态流")
		return
	}
	stream, unsubscribe := s.broker.Subscribe()
	defer unsubscribe()

	w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache, no-transform")
	w.Header().Set("Connection", "keep-alive")
	w.WriteHeader(http.StatusOK)

	devices, err := s.listDeviceViews(r.Context())
	if err != nil {
		return
	}
	writeSSE(w, "snapshot", map[string]any{"devices": devices})
	flusher.Flush()

	keepAlive := time.NewTicker(20 * time.Second)
	defer keepAlive.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case <-keepAlive.C:
			_, _ = fmt.Fprint(w, ": keepalive\n\n")
			flusher.Flush()
		case event, open := <-stream:
			if !open {
				return
			}
			if event.Type == "device.removed" {
				writeSSE(w, event.Type, map[string]string{"id": event.DeviceID})
				flusher.Flush()
				continue
			}
			device, err := s.store.GetDevice(r.Context(), event.DeviceID)
			if errors.Is(err, store.ErrNotFound) {
				continue
			}
			if err != nil {
				s.logger.Error("load SSE device", "device_id", event.DeviceID, "error", err)
				continue
			}
			writeSSE(w, event.Type, s.view(device))
			flusher.Flush()
		}
	}
}

func writeSSE(w http.ResponseWriter, event string, payload any) {
	_, _ = fmt.Fprintf(w, "event: %s\ndata: %s\n\n", event, mustJSON(payload))
}

func (s *Server) publishRemoved(deviceID string) {
	s.broker.Publish(events.Event{Type: "device.removed", DeviceID: deviceID})
}
