package store

import (
	"context"
	"fmt"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/fishy-stick/wakelet/internal/identity"
)

func TestPairingTokenConcurrentConsumption(t *testing.T) {
	ctx := context.Background()
	dataStore, err := Open(ctx, filepath.Join(t.TempDir(), "wakelet.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()

	now := time.Date(2026, 8, 4, 0, 0, 0, 0, time.UTC)
	tokenHash := identity.Hash("0123456789ABCDEFGHJK")
	if err := dataStore.CreatePairingToken(ctx, tokenHash, now, now.Add(5*time.Minute)); err != nil {
		t.Fatal(err)
	}

	var successes atomic.Int32
	var failures atomic.Int32
	var wait sync.WaitGroup
	for i := 0; i < 16; i++ {
		wait.Add(1)
		go func(index int) {
			defer wait.Done()
			secretHash := identity.Hash(fmt.Sprintf("secret-%d", index))
			consumed, err := dataStore.ConsumePairingToken(ctx, tokenHash, now, Device{
				ID: fmt.Sprintf("device-%d", index), CredentialHash: secretHash[:], Name: "测试设备",
				MACAddress: "A4:83:E7:19:2C:5A", CreatedAt: now, UpdatedAt: now,
			})
			if err != nil {
				failures.Add(1)
				return
			}
			if consumed {
				successes.Add(1)
			}
		}(i)
	}
	wait.Wait()
	if failures.Load() != 0 {
		t.Fatalf("unexpected database failures: %d", failures.Load())
	}
	if successes.Load() != 1 {
		t.Fatalf("got %d successful consumers, want 1", successes.Load())
	}
	devices, err := dataStore.ListDevices(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(devices) != 1 {
		t.Fatalf("got %d devices, want 1", len(devices))
	}
}

func TestExpiredTokenCleanup(t *testing.T) {
	ctx := context.Background()
	dataStore, err := Open(ctx, filepath.Join(t.TempDir(), "wakelet.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()
	now := time.Now().UTC()
	hash := identity.Hash("0123456789ABCDEFGHJK")
	if err := dataStore.CreatePairingToken(ctx, hash, now.Add(-time.Minute), now); err != nil {
		t.Fatal(err)
	}
	secretHash := identity.Hash("expired-secret")
	consumed, err := dataStore.ConsumePairingToken(ctx, hash, now, Device{
		ID: "expired-device", CredentialHash: secretHash[:], Name: "过期设备",
		MACAddress: "A4:83:E7:19:2C:5A", CreatedAt: now, UpdatedAt: now,
	})
	if err != nil {
		t.Fatal(err)
	}
	if consumed {
		t.Fatal("expired token must not be consumed")
	}
	removed, err := dataStore.CleanupExpiredTokens(ctx, now)
	if err != nil {
		t.Fatal(err)
	}
	if removed != 1 {
		t.Fatalf("removed %d tokens, want 1", removed)
	}
}
