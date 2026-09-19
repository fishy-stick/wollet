package main

import (
	"github.com/fishy-stick/wollet/internal/protocol"
	"sync"
	"time"
)

// A harmless in-memory client: it never calls an operating-system power API.
// The same instance survives WebSocket reconnects; expiry is based on a monotonic deadline.
type simulatedPlans struct {
	mu               sync.Mutex
	plan             *protocol.ShutdownPlan
	deadline         time.Time
	results          map[string]*protocol.ShutdownResult
	localCancelAfter time.Duration
	created          time.Time
}

func (p *simulatedPlans) snapshotLocked() *protocol.ShutdownPlan {
	if p.plan == nil {
		return nil
	}
	if p.plan.State == "scheduled" {
		if p.localCancelAfter > 0 && time.Since(p.created) >= p.localCancelAfter {
			p.plan.State = "cancelled"
			p.plan.Revision++
			p.plan.Reason = "local_user"
			p.plan.RemainingMilliseconds = 0
		} else if time.Until(p.deadline) <= 0 {
			p.plan.State = "submitted"
			p.plan.Revision += 2
			p.plan.RemainingMilliseconds = 0
		} else {
			p.plan.RemainingMilliseconds = time.Until(p.deadline).Milliseconds()
		}
	}
	copy := *p.plan
	return &copy
}
func (p *simulatedPlans) snapshot() *protocol.ShutdownPlan {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.snapshotLocked()
}
func (p *simulatedPlans) apply(m protocol.ServerMessage) *protocol.ShutdownResult {
	p.mu.Lock()
	defer p.mu.Unlock()
	if old := p.results[m.CommandID]; old != nil {
		return old
	}
	current := p.snapshotLocked()
	result := &protocol.ShutdownResult{CommandID: m.CommandID, OperationID: m.OperationID, Accepted: false, Plan: current}
	switch m.Type {
	case "shutdown_plan_create":
		if current != nil && (current.State == "scheduled" || current.State == "submitted") {
			result.Code = "active_plan_exists"
			break
		}
		p.plan = &protocol.ShutdownPlan{OperationID: m.OperationID, Revision: 1, State: "scheduled", RemainingMilliseconds: 10000}
		p.created = time.Now()
		p.deadline = p.created.Add(10 * time.Second)
		result.Accepted = true
	case "shutdown_plan_cancel", "shutdown_plan_execute":
		if current == nil || current.OperationID != m.OperationID {
			result.Code = "unknown_operation"
			break
		}
		if current.State != "scheduled" {
			result.Code = "too_late"
			break
		}
		if current.Revision != m.ExpectedRevision {
			result.Code = "revision_conflict"
			break
		}
		p.plan.Revision++
		p.plan.RemainingMilliseconds = 0
		result.Accepted = true
		if m.Type == "shutdown_plan_cancel" {
			p.plan.State = "cancelled"
			p.plan.Reason = "remote_user"
		} else {
			p.plan.State = "submitted"
			p.plan.Revision++
		}
	default:
		result.Code = "invalid_request"
	}
	result.Plan = p.snapshotLocked()
	if p.results == nil {
		p.results = map[string]*protocol.ShutdownResult{}
	}
	p.results[m.CommandID] = result
	return result
}
func (p *simulatedPlans) history() []*protocol.ShutdownResult {
	p.mu.Lock()
	defer p.mu.Unlock()
	var all []*protocol.ShutdownResult
	for _, r := range p.results {
		all = append(all, r)
	}
	return all
}
