package server

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/fishy-stick/wollet/internal/config"
	"github.com/fishy-stick/wollet/internal/protocol"
	"github.com/fishy-stick/wollet/internal/store"
)

func TestDeviceStatusNegotiationViewsReconnectAndLegacy(t *testing.T) {
	db, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "status.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	cfg := config.Config{SessionTTL: time.Hour, TokenTTL: time.Minute, HelloTimeout: time.Second, HeartbeatEvery: time.Second, OfflineAfter: 45 * time.Second, CleanupInterval: time.Minute, CommandTimeout: time.Second}
	app := New(cfg, db, &fakeWOLSender{}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer app.Close()
	host := httptest.NewServer(app)
	defer host.Close()
	id, secret := bindForTest(t, host.URL, createTokenForTest(t, host.URL, nil))
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	headers := http.Header{"X-Wollet-Device-ID": {id}, "Authorization": {"Bearer " + secret},
		"X-Forwarded-For": {"192.168.1.99"}, "X-Real-IP": {"2001:db8::99"}}
	dial := func(caps []string) (*websocket.Conn, protocol.ServerMessage) {
		t.Helper()
		conn, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(host.URL, "http")+"/api/v1/client/connect", &websocket.DialOptions{HTTPHeader: headers})
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { conn.CloseNow() })
		if err = wsjson.Write(ctx, conn, protocol.ClientMessage{Type: "hello", ProtocolVersion: 1, DeviceName: "Status test", MACAddress: "A4:83:E7:19:2C:5A", Capabilities: caps}); err != nil {
			t.Fatal(err)
		}
		var ready protocol.ServerMessage
		if err = wsjson.Read(ctx, conn, &ready); err != nil {
			t.Fatal(err)
		}
		return conn, ready
	}
	device, err := db.GetDevice(ctx, id)
	if err != nil {
		t.Fatal(err)
	}
	conn, ready := dial([]string{protocol.DeviceStatusCapability})
	if ready.SessionID != "" || !slices.Equal(ready.Capabilities, []string{protocol.DeviceStatusCapability}) {
		t.Fatalf("telemetry tied to shutdown plans: %+v", ready)
	}
	waitFor(t, time.Second, func() bool { return app.hub.IsOnline(id) })
	first := app.view(device)
	if first.Connection == nil || first.Connection.RemoteIPAddress == nil || *first.Connection.RemoteIPAddress != "127.0.0.1" || first.ClientStatus != nil {
		t.Fatalf("before sample: %+v", first)
	}
	uptime := int64(86400000)
	// Neither forwarded headers nor a client-reported address may override the connection peer.
	if err = conn.Write(ctx, websocket.MessageText, []byte(`{"type":"heartbeat","deviceStatus":{"localIpAddress":"2001:db8::20","systemUptimeMs":86400000}}`)); err != nil {
		t.Fatal(err)
	}
	waitFor(t, time.Second, func() bool { return app.view(device).ClientStatus != nil })
	check := func(view deviceView) {
		t.Helper()
		if view.Status != "online" || view.Connection == nil || view.Connection.ID != first.Connection.ID || view.Connection.RemoteIPAddress == nil || *view.Connection.RemoteIPAddress != "127.0.0.1" || view.ClientStatus == nil || !view.ClientStatus.Fresh || view.ClientStatus.SystemUptimeMs == nil || *view.ClientStatus.SystemUptimeMs != uptime {
			t.Fatalf("inconsistent device view: %+v", view)
		}
		if !slices.Contains(view.Capabilities, protocol.DeviceStatusCapability) {
			t.Fatal("negotiated status capability missing from view")
		}
	}
	for range 2 {
		response := adminRequestForTest(t, host.URL, nil, "GET", "/api/v1/devices", nil)
		var payload struct {
			Devices []deviceView `json:"devices"`
		}
		if err = json.NewDecoder(response.Body).Decode(&payload); err != nil {
			t.Fatal(err)
		}
		response.Body.Close()
		if len(payload.Devices) != 1 {
			t.Fatal("unexpected list")
		}
		check(payload.Devices[0])
	}
	request, _ := http.NewRequestWithContext(ctx, "GET", host.URL+"/api/v1/client/me", nil)
	request.Header = headers
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	var me deviceView
	if err = json.NewDecoder(response.Body).Decode(&me); err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	check(me)
	request, _ = http.NewRequestWithContext(ctx, "GET", host.URL+"/api/v1/events", nil)
	response, err = http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	reader := bufio.NewReader(response.Body)
	for {
		line, err := reader.ReadString('\n')
		if err != nil {
			t.Fatal(err)
		}
		if strings.HasPrefix(line, "data: ") {
			var payload struct {
				Devices []deviceView `json:"devices"`
			}
			if err = json.Unmarshal([]byte(strings.TrimPrefix(line, "data: ")), &payload); err != nil {
				t.Fatal(err)
			}
			check(payload.Devices[0])
			break
		}
	}
	response.Body.Close()
	// Invalid uptime becomes unknown without losing either the connection or its peer IP.
	if err = conn.Write(ctx, websocket.MessageText, []byte(`{"type":"heartbeat","deviceStatus":{"localIpAddress":false,"systemUptimeMs":false}}`)); err != nil {
		t.Fatal(err)
	}
	waitFor(t, time.Second, func() bool {
		status := app.view(device).ClientStatus
		return status != nil && status.SystemUptimeMs == nil
	})
	if got := app.view(device); got.Status != "online" || *got.Connection.RemoteIPAddress != "127.0.0.1" {
		t.Fatalf("bad status disconnected client: %+v", got)
	}
	conn.CloseNow()
	waitFor(t, time.Second, func() bool { return !app.hub.IsOnline(id) })
	if got := app.view(device); got.Connection != nil || got.ClientStatus != nil {
		t.Fatal("offline retained metadata")
	}
	legacy, ready := dial(nil)
	if len(ready.Capabilities) != 0 {
		t.Fatalf("legacy negotiated telemetry: %+v", ready)
	}
	waitFor(t, time.Second, func() bool { return app.hub.IsOnline(id) })
	if got := app.view(device); got.Connection.ID == first.Connection.ID || *got.Connection.RemoteIPAddress != "127.0.0.1" || got.ClientStatus != nil {
		t.Fatal("reconnect retained old session/sample")
	}
	if err = wsjson.Write(ctx, legacy, protocol.ClientMessage{Type: "heartbeat", DeviceStatus: &protocol.DeviceStatus{SystemUptimeMs: &uptime}}); err != nil {
		t.Fatal(err)
	}
	// The ordinary legacy command still works after an unnegotiated status heartbeat.
	delivered := make(chan error, 1)
	go func() { delivered <- app.hub.SendShutdown(ctx, id, "legacy-command") }()
	var command protocol.ServerMessage
	if err = wsjson.Read(ctx, legacy, &command); err != nil {
		t.Fatal(err)
	}
	if err = wsjson.Write(ctx, legacy, protocol.ClientMessage{Type: "shutdown_ack", CommandID: command.CommandID}); err != nil {
		t.Fatal(err)
	}
	if err = <-delivered; err != nil {
		t.Fatal(err)
	}
	if app.view(device).ClientStatus != nil {
		t.Fatal("unnegotiated telemetry was accepted")
	}
	legacy.CloseNow()
}

func TestConnectionIPAddress(t *testing.T) {
	for _, test := range []struct{ remote, want string }{
		{"192.168.1.20:50000", "192.168.1.20"},
		{"[2001:0db8::20]:50000", "2001:db8::20"},
		{"[::ffff:192.168.1.20]:50000", "192.168.1.20"},
		{"[fe80::1%12]:50000", "fe80::1%12"},
		{"127.0.0.1:50000", "127.0.0.1"},
		{"[::1]:50000", "::1"},
		{"", ""}, {"example.com:50000", ""}, {"0.0.0.0:50000", ""}, {"[::ffff:0.0.0.0]:50000", ""},
		{"[ff02::1]:50000", ""},
	} {
		t.Run(test.remote, func(t *testing.T) {
			request := &http.Request{RemoteAddr: test.remote, Header: http.Header{
				"X-Forwarded-For": {"192.168.1.99"}, "X-Real-IP": {"192.168.1.99"},
			}}
			got := connectionIPAddress(request)
			if (got == nil) != (test.want == "") || got != nil && *got != test.want {
				t.Fatalf("RemoteAddr %q: %v, want %q", test.remote, got, test.want)
			}
		})
	}
}
