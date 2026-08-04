package server

import (
	"sync"
	"time"
)

const (
	deviceOperationWaking       = "waking"
	deviceOperationShuttingDown = "shutting_down"
	wakeOperationTimeout        = 90 * time.Second
	shutdownOperationTimeout    = 60 * time.Second
)

type deviceOperation struct {
	kind      string
	expiresAt time.Time
}

type expiredDeviceOperation struct {
	deviceID string
	kind     string
}

type deviceOperations struct {
	mu     sync.Mutex
	states map[string]deviceOperation
}

func newDeviceOperations() *deviceOperations {
	return &deviceOperations{states: make(map[string]deviceOperation)}
}

func (o *deviceOperations) set(deviceID, kind string, expiresAt time.Time) {
	o.mu.Lock()
	o.states[deviceID] = deviceOperation{kind: kind, expiresAt: expiresAt}
	o.mu.Unlock()
}

func (o *deviceOperations) get(deviceID string) string {
	o.mu.Lock()
	defer o.mu.Unlock()
	return o.states[deviceID].kind
}

func (o *deviceOperations) clear(deviceID string) bool {
	o.mu.Lock()
	defer o.mu.Unlock()
	if _, ok := o.states[deviceID]; !ok {
		return false
	}
	delete(o.states, deviceID)
	return true
}

func (o *deviceOperations) clearIf(deviceID, kind string) bool {
	o.mu.Lock()
	defer o.mu.Unlock()
	state, ok := o.states[deviceID]
	if !ok || state.kind != kind {
		return false
	}
	delete(o.states, deviceID)
	return true
}

func (o *deviceOperations) expire(now time.Time) []expiredDeviceOperation {
	o.mu.Lock()
	defer o.mu.Unlock()
	var expired []expiredDeviceOperation
	for deviceID, state := range o.states {
		if now.Before(state.expiresAt) {
			continue
		}
		expired = append(expired, expiredDeviceOperation{deviceID: deviceID, kind: state.kind})
		delete(o.states, deviceID)
	}
	return expired
}
