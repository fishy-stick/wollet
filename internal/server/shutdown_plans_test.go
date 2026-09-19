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
	"strings"
	"testing"
	"time"
)

func TestShutdownPlanRoundTripAndRestart(t *testing.T) {
	db, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "plans.db"))
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
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(host.URL, "http")+"/api/v1/client/connect", &websocket.DialOptions{HTTPHeader: http.Header{"X-Wollet-Device-ID": {id}, "Authorization": {"Bearer " + secret}}})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.CloseNow()
	write := func(m protocol.ClientMessage) {
		t.Helper()
		if err := wsjson.Write(ctx, conn, m); err != nil {
			t.Fatal(err)
		}
	}
	read := func(kind string) protocol.ServerMessage {
		t.Helper()
		for {
			var m protocol.ServerMessage
			if err := wsjson.Read(ctx, conn, &m); err != nil {
				t.Fatal(err)
			}
			if m.Type == kind {
				return m
			}
			if m.Type != "shutdown_plan_recorded" {
				t.Fatalf("unexpected message: %#v", m)
			}
		}
	}
	write(protocol.ClientMessage{Type: "hello", ProtocolVersion: 1, DeviceName: "Plan test", MACAddress: "A4:83:E7:19:2C:5A", Capabilities: []string{protocol.ShutdownPlanCapability}})
	ready := read("ready")
	if ready.SessionID == "" {
		t.Fatal("no session")
	}
	operation := "b5aee3fb-b063-4620-a410-2cd305538159"
	path := "/api/v1/devices/" + id + "/shutdown-plans"
	body := []byte(`{"operationId":"` + operation + `"}`)
	waitFor(t, time.Second, func() bool { return app.hub.IsOnline(id) })
	response := adminRequestForTest(t, host.URL, nil, "POST", path, body)
	if response.StatusCode != 409 {
		t.Fatalf("unsynced create %d", response.StatusCode)
	}
	response.Body.Close()
	write(protocol.ClientMessage{Type: "shutdown_plan_sync", SessionID: ready.SessionID, Sequence: 1, Complete: true})
	read("shutdown_plan_synced")
	legacy := adminRequestForTest(t, host.URL, nil, "POST", "/api/v1/devices/"+id+"/shutdown", nil)
	if legacy.StatusCode != 409 {
		t.Fatalf("legacy bypass %d", legacy.StatusCode)
	}
	legacy.Body.Close()
	done := make(chan *http.Response, 1)
	go func() { done <- adminRequestForTest(t, host.URL, nil, "POST", path, body) }()
	command := read("shutdown_plan_create")
	plan := &protocol.ShutdownPlan{OperationID: operation, Revision: 1, State: "scheduled", RemainingMilliseconds: 9000}
	write(protocol.ClientMessage{Type: "shutdown_plan_result", SessionID: ready.SessionID, Sequence: 2, CommandID: command.CommandID, OperationID: operation, Accepted: true, Plan: plan})
	response = <-done
	if response.StatusCode != 201 {
		t.Fatalf("create %d: %s", response.StatusCode, readBody(response))
	}
	response.Body.Close()
	repeat := adminRequestForTest(t, host.URL, nil, "POST", path, body)
	if repeat.StatusCode != 201 {
		t.Fatalf("repeat %d", repeat.StatusCode)
	}
	repeat.Body.Close()
	// A stale snapshot must not resurrect a cancelled plan, even with a newer sequence.
	plan = &protocol.ShutdownPlan{OperationID: operation, Revision: 2, State: "cancelled", Reason: "local_user"}
	write(protocol.ClientMessage{Type: "shutdown_plan_state", SessionID: ready.SessionID, Sequence: 3, Plan: plan})
	waitFor(t, time.Second, func() bool { _, p, _ := app.planDeviceView(id); return p != nil && p.State == "cancelled" })
	write(protocol.ClientMessage{Type: "shutdown_plan_state", SessionID: ready.SessionID, Sequence: 4, Plan: &protocol.ShutdownPlan{OperationID: operation, Revision: 1, State: "scheduled", RemainingMilliseconds: 10000}})
	conn.CloseNow()
	waitFor(t, time.Second, func() bool { return !app.hub.IsOnline(id) })
	// A new coordinator reads the stored result but cannot claim a synchronized snapshot.
	restored := New(cfg, db, &fakeWOLSender{}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer restored.Close()
	_, p, r := restored.planDeviceView(id)
	if p == nil || p.State != "cancelled" || p.Synchronized || r.Status != "confirmed" {
		t.Fatalf("restored plan=%#v request=%#v", p, r)
	}
	query := adminRequestForTest(t, host.URL, nil, "GET", path+"/"+operation, nil)
	defer query.Body.Close()
	var payload struct {
		Plan *planView `json:"plan"`
	}
	if err := json.NewDecoder(query.Body).Decode(&payload); err != nil {
		t.Fatal(err)
	}
	if payload.Plan == nil || payload.Plan.Synchronized {
		t.Fatal("disconnected plan appears synchronized")
	}
}
