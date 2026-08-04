package server

import (
	"testing"
	"time"
)

func TestDeviceOperationsAreReplaceableAndExpire(t *testing.T) {
	operations := newDeviceOperations()
	now := time.Now().UTC()

	operations.set("device-1", deviceOperationWaking, now.Add(time.Minute))
	if got := operations.get("device-1"); got != deviceOperationWaking {
		t.Fatalf("operation = %q, want %q", got, deviceOperationWaking)
	}

	operations.set("device-1", deviceOperationShuttingDown, now.Add(2*time.Minute))
	if operations.clearIf("device-1", deviceOperationWaking) {
		t.Fatal("clearIf removed a different operation")
	}
	if got := operations.get("device-1"); got != deviceOperationShuttingDown {
		t.Fatalf("replacement operation = %q, want %q", got, deviceOperationShuttingDown)
	}

	if expired := operations.expire(now.Add(time.Minute)); len(expired) != 0 {
		t.Fatalf("expired %d operations too early", len(expired))
	}
	expired := operations.expire(now.Add(3 * time.Minute))
	if len(expired) != 1 || expired[0].deviceID != "device-1" || expired[0].kind != deviceOperationShuttingDown {
		t.Fatalf("unexpected expired operations: %#v", expired)
	}
	if got := operations.get("device-1"); got != "" {
		t.Fatalf("operation after expiry = %q, want empty", got)
	}
}
