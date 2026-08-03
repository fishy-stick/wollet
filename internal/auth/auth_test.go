package auth

import (
	"testing"
	"time"
)

func TestSessionLifecycle(t *testing.T) {
	now := time.Date(2026, 8, 4, 0, 0, 0, 0, time.UTC)
	manager := NewSessionManager(time.Hour)
	token, _, err := manager.Create(now)
	if err != nil {
		t.Fatal(err)
	}
	if !manager.Valid(token, now.Add(59*time.Minute)) {
		t.Fatal("session should still be valid")
	}
	if manager.Valid(token, now.Add(time.Hour)) {
		t.Fatal("session should expire at its deadline")
	}
}

func TestLimiter(t *testing.T) {
	now := time.Date(2026, 8, 4, 0, 0, 0, 0, time.UTC)
	limiter := NewLimiter(2, time.Minute)
	if !limiter.Allow("client", now) || !limiter.Allow("client", now) {
		t.Fatal("first two attempts should be accepted")
	}
	if limiter.Allow("client", now) {
		t.Fatal("third attempt should be rejected")
	}
	if !limiter.Allow("client", now.Add(time.Minute)) {
		t.Fatal("new window should accept attempts")
	}
}
