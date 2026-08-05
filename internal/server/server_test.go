package server

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/fishy-stick/wollet/internal/config"
	"github.com/fishy-stick/wollet/internal/protocol"
	"github.com/fishy-stick/wollet/internal/store"
)

type fakeWOLSender struct {
	mu   sync.Mutex
	macs []net.HardwareAddr
}

func TestEmbeddedWebUI(t *testing.T) {
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "wollet.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()
	cfg := config.Config{AdminUsername: "admin", AdminPassword: "test-password-123", SessionTTL: time.Hour, OfflineAfter: time.Minute}
	app := New(cfg, dataStore, &fakeWOLSender{}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer app.Close()

	for _, test := range []struct {
		path        string
		contentType string
		contains    string
	}{
		{path: "/", contentType: "text/html", contains: "管理员登录"},
		{path: "/assets/styles.css", contentType: "text/css", contains: "--blue: #0a58f5"},
		{path: "/assets/wollet.svg", contentType: "image/svg+xml", contains: "<title id=\"title\">Wollet</title>"},
		{path: "/assets/app.js", contentType: "text/javascript", contains: "EventSource"},
	} {
		request := httptest.NewRequest(http.MethodGet, test.path, nil)
		response := httptest.NewRecorder()
		app.ServeHTTP(response, request)
		if response.Code != http.StatusOK {
			t.Errorf("GET %s returned HTTP %d", test.path, response.Code)
			continue
		}
		if !strings.Contains(response.Header().Get("Content-Type"), test.contentType) {
			t.Errorf("GET %s Content-Type = %q", test.path, response.Header().Get("Content-Type"))
		}
		if !strings.Contains(response.Body.String(), test.contains) {
			t.Errorf("GET %s did not contain %q", test.path, test.contains)
		}
		if response.Header().Get("Content-Security-Policy") == "" {
			t.Errorf("GET %s did not include CSP", test.path)
		}
	}
}

func (s *fakeWOLSender) Send(_ context.Context, mac net.HardwareAddr) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.macs = append(s.macs, append(net.HardwareAddr(nil), mac...))
	return nil
}

func TestEndToEndDeviceLifecycle(t *testing.T) {
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "wollet.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()
	cfg := config.Config{
		AdminUsername: "admin", AdminPassword: "test-password-123", SessionTTL: time.Hour,
		TokenTTL: 5 * time.Minute, HelloTimeout: time.Second, HeartbeatEvery: 15 * time.Second,
		OfflineAfter: 45 * time.Second, CommandTimeout: time.Second, CleanupInterval: time.Minute,
	}
	app := New(cfg, dataStore, &fakeWOLSender{}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer app.Close()
	testServer := httptest.NewServer(app)
	defer testServer.Close()

	cookie := loginForTest(t, testServer.URL, "admin", "test-password-123")
	token := createTokenForTest(t, testServer.URL, cookie)
	deviceID, secret := bindForTest(t, testServer.URL, token)

	wsURL := "ws" + strings.TrimPrefix(testServer.URL, "http") + "/api/v1/client/connect"
	headers := http.Header{"X-Wollet-Device-ID": {deviceID}, "Authorization": {"Bearer " + secret}}
	conn, response, err := websocket.Dial(context.Background(), wsURL, &websocket.DialOptions{HTTPHeader: headers})
	if err != nil {
		if response != nil {
			t.Fatalf("WebSocket dial: HTTP %d", response.StatusCode)
		}
		t.Fatal(err)
	}
	defer conn.CloseNow()
	if err := wsjson.Write(context.Background(), conn, protocol.ClientMessage{
		Type: "hello", ProtocolVersion: protocol.Version, DeviceName: "工作站", MACAddress: "A4:83:E7:19:2C:5A",
	}); err != nil {
		t.Fatal(err)
	}
	var ready protocol.ServerMessage
	if err := wsjson.Read(context.Background(), conn, &ready); err != nil {
		t.Fatal(err)
	}
	if ready.Type != "ready" {
		t.Fatalf("got message %q, want ready", ready.Type)
	}
	waitFor(t, time.Second, func() bool { return app.hub.IsOnline(deviceID) })

	shutdownDone := make(chan *http.Response, 1)
	go func() {
		shutdownDone <- adminRequestForTest(t, testServer.URL, cookie, http.MethodPost, "/api/v1/devices/"+deviceID+"/shutdown", nil)
	}()
	var command protocol.ServerMessage
	if err := wsjson.Read(context.Background(), conn, &command); err != nil {
		t.Fatal(err)
	}
	if command.Type != "shutdown" || command.CommandID == "" {
		t.Fatalf("unexpected command %#v", command)
	}
	if err := wsjson.Write(context.Background(), conn, protocol.ClientMessage{Type: "shutdown_ack", CommandID: command.CommandID}); err != nil {
		t.Fatal(err)
	}
	shutdownResponse := <-shutdownDone
	defer shutdownResponse.Body.Close()
	if shutdownResponse.StatusCode != http.StatusOK {
		t.Fatalf("shutdown returned HTTP %d: %s", shutdownResponse.StatusCode, readBody(shutdownResponse))
	}
	if got := app.operations.get(deviceID); got != deviceOperationShuttingDown {
		t.Fatalf("operation = %q after shutdown, want %q", got, deviceOperationShuttingDown)
	}

	repeatedShutdownDone := make(chan *http.Response, 1)
	go func() {
		repeatedShutdownDone <- adminRequestForTest(t, testServer.URL, cookie, http.MethodPost, "/api/v1/devices/"+deviceID+"/shutdown", nil)
	}()
	command = protocol.ServerMessage{}
	if err := wsjson.Read(context.Background(), conn, &command); err != nil {
		t.Fatal(err)
	}
	if command.Type != "shutdown" || command.CommandID == "" {
		t.Fatalf("unexpected repeated command %#v", command)
	}
	if err := wsjson.Write(context.Background(), conn, protocol.ClientMessage{Type: "shutdown_ack", CommandID: command.CommandID}); err != nil {
		t.Fatal(err)
	}
	repeatedShutdownResponse := <-repeatedShutdownDone
	defer repeatedShutdownResponse.Body.Close()
	if repeatedShutdownResponse.StatusCode != http.StatusOK {
		t.Fatalf("repeated shutdown returned HTTP %d: %s", repeatedShutdownResponse.StatusCode, readBody(repeatedShutdownResponse))
	}

	if err := conn.Close(websocket.StatusNormalClosure, "test complete"); err != nil {
		t.Fatal(err)
	}
	waitFor(t, time.Second, func() bool { return !app.hub.IsOnline(deviceID) })
	waitFor(t, time.Second, func() bool { return app.operations.get(deviceID) == "" })

	deleteResponse := adminRequestForTest(t, testServer.URL, cookie, http.MethodDelete, "/api/v1/devices/"+deviceID, nil)
	defer deleteResponse.Body.Close()
	if deleteResponse.StatusCode != http.StatusNoContent {
		t.Fatalf("delete returned HTTP %d: %s", deleteResponse.StatusCode, readBody(deleteResponse))
	}
	waitFor(t, time.Second, func() bool { return !app.hub.IsOnline(deviceID) })

	request, _ := http.NewRequest(http.MethodGet, testServer.URL+"/api/v1/client/me", nil)
	request.Header.Set("X-Wollet-Device-ID", deviceID)
	request.Header.Set("Authorization", "Bearer "+secret)
	invalidResponse, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer invalidResponse.Body.Close()
	if invalidResponse.StatusCode != http.StatusUnauthorized {
		t.Fatalf("revoked credentials returned HTTP %d", invalidResponse.StatusCode)
	}
}

func TestLoginRejectsMissingOrigin(t *testing.T) {
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "wollet.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()
	cfg := config.Config{AdminUsername: "admin", AdminPassword: "test-password-123", SessionTTL: time.Hour, OfflineAfter: time.Minute}
	app := New(cfg, dataStore, &fakeWOLSender{}, slog.New(slog.NewTextHandler(os.Stderr, nil)))
	defer app.Close()
	server := httptest.NewServer(app)
	defer server.Close()
	payload, _ := json.Marshal(map[string]string{"username": "admin", "password": "test-password-123"})
	response, err := http.Post(server.URL+"/api/v1/auth/login", "application/json", bytes.NewReader(payload))
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusForbidden {
		t.Fatalf("got HTTP %d, want 403", response.StatusCode)
	}
}

func loginForTest(t *testing.T, baseURL, username, password string) *http.Cookie {
	t.Helper()
	payload, _ := json.Marshal(map[string]string{"username": username, "password": password})
	response := adminRequestForTest(t, baseURL, nil, http.MethodPost, "/api/v1/auth/login", payload)
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("login returned HTTP %d: %s", response.StatusCode, readBody(response))
	}
	for _, cookie := range response.Cookies() {
		if cookie.Name == sessionCookieName {
			return cookie
		}
	}
	t.Fatal("login did not set session cookie")
	return nil
}

func createTokenForTest(t *testing.T, baseURL string, cookie *http.Cookie) string {
	t.Helper()
	response := adminRequestForTest(t, baseURL, cookie, http.MethodPost, "/api/v1/pairing-tokens", nil)
	defer response.Body.Close()
	if response.StatusCode != http.StatusCreated {
		t.Fatalf("token returned HTTP %d: %s", response.StatusCode, readBody(response))
	}
	var payload struct {
		Token string `json:"token"`
	}
	if err := json.NewDecoder(response.Body).Decode(&payload); err != nil {
		t.Fatal(err)
	}
	return payload.Token
}

func bindForTest(t *testing.T, baseURL, token string) (string, string) {
	t.Helper()
	payload, _ := json.Marshal(map[string]string{"token": token, "deviceName": "工作站", "macAddress": "A4:83:E7:19:2C:5A"})
	request, _ := http.NewRequest(http.MethodPost, baseURL+"/api/v1/client/bind", bytes.NewReader(payload))
	request.Header.Set("Content-Type", "application/json")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusCreated {
		t.Fatalf("bind returned HTTP %d: %s", response.StatusCode, readBody(response))
	}
	var result struct {
		DeviceID string `json:"deviceId"`
		Secret   string `json:"deviceSecret"`
	}
	if err := json.NewDecoder(response.Body).Decode(&result); err != nil {
		t.Fatal(err)
	}
	return result.DeviceID, result.Secret
}

func adminRequestForTest(t *testing.T, baseURL string, cookie *http.Cookie, method, path string, body []byte) *http.Response {
	t.Helper()
	request, err := http.NewRequest(method, baseURL+path, bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Origin", baseURL)
	if len(body) > 0 {
		request.Header.Set("Content-Type", "application/json")
	}
	if cookie != nil {
		request.AddCookie(cookie)
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	return response
}

func waitFor(t *testing.T, timeout time.Duration, condition func() bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for !condition() {
		if time.Now().After(deadline) {
			t.Fatal("condition was not met before timeout")
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func readBody(response *http.Response) string {
	contents, _ := io.ReadAll(response.Body)
	return string(contents)
}
func TestAuthenticationCanBeDisabled(t *testing.T) {
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "wollet.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()
	cfg := config.Config{AdminUsername: "admin", SessionTTL: time.Hour, OfflineAfter: time.Minute, TokenTTL: 5 * time.Minute}
	app := New(cfg, dataStore, &fakeWOLSender{}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer app.Close()
	testServer := httptest.NewServer(app)
	defer testServer.Close()

	response, err := http.Get(testServer.URL + "/api/v1/auth/session")
	if err != nil {
		t.Fatal(err)
	}
	var session struct {
		AuthenticationEnabled bool `json:"authenticationEnabled"`
	}
	if err := json.NewDecoder(response.Body).Decode(&session); err != nil {
		response.Body.Close()
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != http.StatusOK || session.AuthenticationEnabled {
		t.Fatalf("session returned HTTP %d with authenticationEnabled=%v", response.StatusCode, session.AuthenticationEnabled)
	}

	tokenResponse := adminRequestForTest(t, testServer.URL, nil, http.MethodPost, "/api/v1/pairing-tokens", nil)
	if tokenResponse.StatusCode != http.StatusCreated {
		tokenResponse.Body.Close()
		t.Fatalf("passwordless token creation returned HTTP %d", tokenResponse.StatusCode)
	}
	var tokenPayload struct {
		ExpiresInSeconds int `json:"expiresInSeconds"`
	}
	if err := json.NewDecoder(tokenResponse.Body).Decode(&tokenPayload); err != nil {
		tokenResponse.Body.Close()
		t.Fatal(err)
	}
	tokenResponse.Body.Close()
	if tokenPayload.ExpiresInSeconds != 300 {
		t.Fatalf("expiresInSeconds = %d, want 300", tokenPayload.ExpiresInSeconds)
	}

	payload, _ := json.Marshal(map[string]string{"username": "admin", "password": "unused"})
	loginResponse := adminRequestForTest(t, testServer.URL, nil, http.MethodPost, "/api/v1/auth/login", payload)
	loginResponse.Body.Close()
	if loginResponse.StatusCode != http.StatusConflict {
		t.Fatalf("login while authentication is disabled returned HTTP %d, want 409", loginResponse.StatusCode)
	}
}

func TestWakeOperationIsAdvisory(t *testing.T) {
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "wollet.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()
	sender := &fakeWOLSender{}
	cfg := config.Config{AdminUsername: "admin", SessionTTL: time.Hour, OfflineAfter: time.Minute, TokenTTL: 5 * time.Minute}
	app := New(cfg, dataStore, sender, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer app.Close()
	testServer := httptest.NewServer(app)
	defer testServer.Close()

	token := createTokenForTest(t, testServer.URL, nil)
	deviceID, _ := bindForTest(t, testServer.URL, token)
	for attempt := 0; attempt < 2; attempt++ {
		response := adminRequestForTest(t, testServer.URL, nil, http.MethodPost, "/api/v1/devices/"+deviceID+"/wake", nil)
		var payload map[string]string
		if err := json.NewDecoder(response.Body).Decode(&payload); err != nil {
			response.Body.Close()
			t.Fatal(err)
		}
		response.Body.Close()
		if response.StatusCode != http.StatusOK || payload["operation"] != deviceOperationWaking {
			t.Fatalf("wake attempt %d returned HTTP %d with %#v", attempt+1, response.StatusCode, payload)
		}
	}

	if got := app.operations.get(deviceID); got != deviceOperationWaking {
		t.Fatalf("operation = %q, want %q", got, deviceOperationWaking)
	}
	sender.mu.Lock()
	sent := len(sender.macs)
	sender.mu.Unlock()
	if sent != 2 {
		t.Fatalf("sent %d Wake-on-LAN packets, want 2", sent)
	}
}
