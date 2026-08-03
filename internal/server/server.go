package server

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/url"
	"runtime/debug"
	"strings"
	"time"

	"github.com/fishy-stick/wakelet/internal/auth"
	"github.com/fishy-stick/wakelet/internal/config"
	"github.com/fishy-stick/wakelet/internal/devicehub"
	"github.com/fishy-stick/wakelet/internal/events"
	"github.com/fishy-stick/wakelet/internal/store"
	"github.com/fishy-stick/wakelet/internal/webui"
)

const sessionCookieName = "wakelet_session"

type WOLSender interface {
	Send(context.Context, net.HardwareAddr) error
}

type Server struct {
	cfg           config.Config
	store         *store.Store
	logger        *slog.Logger
	mux           *http.ServeMux
	sessions      *auth.SessionManager
	loginLimiter  *auth.Limiter
	bindLimiter   *auth.Limiter
	adminUserHash [sha256.Size]byte
	adminPassHash [sha256.Size]byte
	hub           *devicehub.Hub
	broker        *events.Broker
	wol           WOLSender
}

func New(cfg config.Config, dataStore *store.Store, sender WOLSender, logger *slog.Logger) *Server {
	adminPasswordHash := sha256.Sum256([]byte(cfg.AdminPassword))
	cfg.AdminPassword = ""
	server := &Server{
		cfg:           cfg,
		store:         dataStore,
		logger:        logger,
		mux:           http.NewServeMux(),
		sessions:      auth.NewSessionManager(cfg.SessionTTL),
		loginLimiter:  auth.NewLimiter(5, time.Minute),
		bindLimiter:   auth.NewLimiter(30, time.Minute),
		adminUserHash: sha256.Sum256([]byte(cfg.AdminUsername)),
		adminPassHash: adminPasswordHash,
		broker:        events.NewBroker(),
		wol:           sender,
	}
	server.hub = devicehub.New(cfg.OfflineAfter, logger, server.onDeviceActivity)
	server.routes()
	return server
}

func (s *Server) routes() {
	s.mux.HandleFunc("GET /healthz", s.handleHealth)
	s.mux.HandleFunc("GET /readyz", s.handleReady)

	s.mux.HandleFunc("POST /api/v1/auth/login", s.requireSameOrigin(s.handleLogin))
	s.mux.HandleFunc("POST /api/v1/auth/logout", s.requireSameOrigin(s.requireAdmin(s.handleLogout)))
	s.mux.HandleFunc("GET /api/v1/auth/session", s.requireAdmin(s.handleSession))

	s.mux.HandleFunc("POST /api/v1/pairing-tokens", s.requireSameOrigin(s.requireAdmin(s.handleCreatePairingToken)))
	s.mux.HandleFunc("GET /api/v1/devices", s.requireAdmin(s.handleListDevices))
	s.mux.HandleFunc("POST /api/v1/devices/{id}/wake", s.requireSameOrigin(s.requireAdmin(s.handleWakeDevice)))
	s.mux.HandleFunc("POST /api/v1/devices/{id}/shutdown", s.requireSameOrigin(s.requireAdmin(s.handleShutdownDevice)))
	s.mux.HandleFunc("DELETE /api/v1/devices/{id}", s.requireSameOrigin(s.requireAdmin(s.handleDeleteDevice)))
	s.mux.HandleFunc("GET /api/v1/events", s.requireAdmin(s.handleEvents))

	s.mux.HandleFunc("POST /api/v1/client/bind", s.handleBindClient)
	s.mux.HandleFunc("GET /api/v1/client/me", s.handleClientMe)
	s.mux.HandleFunc("GET /api/v1/client/connect", s.handleClientConnect)

	assets := http.FileServer(http.FS(webui.Files))
	s.mux.Handle("GET /assets/", assets)
	s.mux.HandleFunc("GET /{$}", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Header().Set("Cache-Control", "no-cache")
		_, _ = w.Write(webui.Index)
	})
}

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	started := time.Now()
	writer := &statusWriter{ResponseWriter: w, status: http.StatusOK}
	s.setSecurityHeaders(writer.Header())
	defer func() {
		if value := recover(); value != nil {
			s.logger.Error("HTTP handler panic", "method", r.Method, "path", r.URL.Path, "panic", value, "stack", string(debug.Stack()))
			writeError(writer, http.StatusInternalServerError, "internal_error", "服务暂时不可用")
		}
		s.logger.Info("HTTP request", "method", r.Method, "path", r.URL.Path, "status", writer.status, "duration_ms", time.Since(started).Milliseconds(), "remote_ip", remoteIP(r))
	}()
	s.mux.ServeHTTP(writer, r)
}

func (s *Server) StartBackground(ctx context.Context) {
	go s.runHeartbeatSweeper(ctx)
	go s.runCleanup(ctx)
}

func (s *Server) Close() {
	s.hub.Close()
}

func (s *Server) runHeartbeatSweeper(ctx context.Context) {
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-ticker.C:
			s.hub.Sweep(now.UTC())
		}
	}
}

func (s *Server) runCleanup(ctx context.Context) {
	ticker := time.NewTicker(s.cfg.CleanupInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-ticker.C:
			removed, err := s.store.CleanupExpiredTokens(ctx, now.UTC())
			if err != nil && !errors.Is(err, context.Canceled) {
				s.logger.Error("cleanup expired pairing tokens", "error", err)
			} else if removed > 0 {
				s.logger.Debug("expired pairing tokens removed", "count", removed)
			}
			s.sessions.Cleanup(now)
			s.loginLimiter.Cleanup(now)
			s.bindLimiter.Cleanup(now)
		}
	}
}

func (s *Server) onDeviceActivity(deviceID string, activity devicehub.Activity, at time.Time) {
	if activity == devicehub.ActivityOnline || activity == devicehub.ActivityHeartbeat {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		err := s.store.TouchDevice(ctx, deviceID, at)
		cancel()
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			s.logger.Error("update device last seen", "device_id", deviceID, "error", err)
		}
	}
	s.broker.Publish(events.Event{Type: "device.updated", DeviceID: deviceID})
}

func (s *Server) handleHealth(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) handleReady(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
	defer cancel()
	if err := s.store.Ping(ctx); err != nil {
		writeError(w, http.StatusServiceUnavailable, "not_ready", "数据库尚未就绪")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "ready"})
}

func (s *Server) setSecurityHeaders(header http.Header) {
	header.Set("Content-Security-Policy", "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'")
	header.Set("Referrer-Policy", "no-referrer")
	header.Set("X-Content-Type-Options", "nosniff")
	header.Set("X-Frame-Options", "DENY")
	header.Set("Permissions-Policy", "camera=(), microphone=(), geolocation=()")
}

func (s *Server) requireAdmin(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		cookie, err := r.Cookie(sessionCookieName)
		if err != nil || !s.sessions.Valid(cookie.Value, time.Now().UTC()) {
			writeError(w, http.StatusUnauthorized, "authentication_required", "请先登录")
			return
		}
		next(w, r)
	}
}

func (s *Server) requireSameOrigin(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !sameOrigin(r) {
			writeError(w, http.StatusForbidden, "invalid_origin", "请求来源无效")
			return
		}
		next(w, r)
	}
}

func sameOrigin(r *http.Request) bool {
	source := r.Header.Get("Origin")
	if source == "" {
		source = r.Header.Get("Referer")
	}
	if source == "" {
		return false
	}
	parsed, err := url.Parse(source)
	if err != nil || parsed.Host == "" {
		return false
	}
	return strings.EqualFold(parsed.Host, r.Host) && (parsed.Scheme == "http" || parsed.Scheme == "https")
}

func decodeJSON(w http.ResponseWriter, r *http.Request, target any) error {
	if mediaType := r.Header.Get("Content-Type"); !strings.HasPrefix(strings.ToLower(mediaType), "application/json") {
		return errors.New("Content-Type must be application/json")
	}
	r.Body = http.MaxBytesReader(w, r.Body, 16<<10)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		return err
	}
	if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
		return errors.New("request body must contain one JSON object")
	}
	return nil
}

func remoteIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err == nil {
		return host
	}
	return r.RemoteAddr
}

type statusWriter struct {
	http.ResponseWriter
	status      int
	wroteHeader bool
}

func (w *statusWriter) WriteHeader(status int) {
	if w.wroteHeader {
		return
	}
	w.wroteHeader = true
	w.status = status
	w.ResponseWriter.WriteHeader(status)
}

func (w *statusWriter) Write(contents []byte) (int, error) {
	if !w.wroteHeader {
		w.WriteHeader(http.StatusOK)
	}
	return w.ResponseWriter.Write(contents)
}

func (w *statusWriter) Flush() {
	if flusher, ok := w.ResponseWriter.(http.Flusher); ok {
		flusher.Flush()
	}
}

func (w *statusWriter) Unwrap() http.ResponseWriter { return w.ResponseWriter }

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(value); err != nil {
		// The response has already started; the server logger records transport failures elsewhere.
		return
	}
}

func writeError(w http.ResponseWriter, status int, code, message string) {
	writeJSON(w, status, map[string]any{"error": map[string]string{"code": code, "message": message}})
}

func mustJSON(value any) []byte {
	contents, err := json.Marshal(value)
	if err != nil {
		panic(fmt.Sprintf("marshal SSE payload: %v", err))
	}
	return contents
}
