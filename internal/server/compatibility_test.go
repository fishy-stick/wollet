package server

import (
	"context"
	"encoding/json"
	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/fishy-stick/wollet/internal/config"
	"github.com/fishy-stick/wollet/internal/protocol"
	"github.com/fishy-stick/wollet/internal/store"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"
)

func TestCompatibilityHandshakeAndHistoricalState(t *testing.T) {
	db, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "test.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	cfg := config.Config{SessionTTL: time.Hour, TokenTTL: time.Minute, HelloTimeout: time.Second, HeartbeatEvery: time.Second, OfflineAfter: time.Minute, CleanupInterval: time.Minute}
	app := New(cfg, db, &fakeWOLSender{}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer app.Close()
	host := httptest.NewServer(app)
	defer host.Close()
	id, secret := bindForTest(t, host.URL, createTokenForTest(t, host.URL, nil))
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(host.URL, "http")+"/api/v1/client/connect", &websocket.DialOptions{HTTPHeader: http.Header{"X-Wollet-Device-ID": {id}, "Authorization": {"Bearer " + secret}}})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.CloseNow()
	if err = wsjson.Write(ctx, conn, protocol.ClientMessage{Type: "hello", ProtocolVersion: 1, DeviceName: "Legacy", MACAddress: "A4:83:E7:19:2C:5A", ClientVersion: "1.0.4"}); err != nil {
		t.Fatal(err)
	}
	var ready protocol.ServerMessage
	if err = wsjson.Read(ctx, conn, &ready); err != nil {
		t.Fatal(err)
	}
	if len(ready.Capabilities) != 0 || !slices.Contains(ready.SupportedCapabilities, protocol.ShutdownPlanCapability) {
		t.Fatalf("negotiated and supported confused: %+v", ready)
	}
	waitFor(t, time.Second, func() bool { return app.hub.IsOnline(id) })
	result := app.compatibilityView(id)
	if result.Kind != "limited" || result.Historical {
		t.Fatalf("online: %+v", result)
	}
	response := adminRequestForTest(t, host.URL, nil, "GET", "/api/v1/devices", nil)
	var payload struct {
		Devices []deviceView `json:"devices"`
	}
	if err = json.NewDecoder(response.Body).Decode(&payload); err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if len(payload.Devices) != 1 || payload.Devices[0].Compatibility.Kind != "limited" {
		t.Fatalf("REST: %+v", payload)
	}
	conn.CloseNow()
	waitFor(t, time.Second, func() bool { return !app.hub.IsOnline(id) })
	result = app.compatibilityView(id)
	if result.Label != "" || !result.Historical || result.ClientVersion != "1.0.4" || len(result.Missing) != 0 {
		t.Fatalf("offline: %+v", result)
	}
	restarted := New(cfg, db, &fakeWOLSender{}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer restarted.Close()
	result = restarted.compatibilityView(id)
	if !result.Historical || result.ClientVersion != "1.0.4" {
		t.Fatalf("restart: %+v", result)
	}
}
