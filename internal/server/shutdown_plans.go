package server

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"regexp"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/fishy-stick/wollet/internal/events"
	"github.com/fishy-stick/wollet/internal/identity"
	"github.com/fishy-stick/wollet/internal/protocol"
)

var uuidPattern = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)

type planRequest struct {
	RequestID        string    `json:"requestId,omitempty"`
	OperationID      string    `json:"operationId"`
	CommandID        string    `json:"commandId"`
	Action           string    `json:"action"`
	ExpectedRevision int64     `json:"expectedRevision,omitempty"`
	Status           string    `json:"status"`
	Code             string    `json:"code,omitempty"`
	CreatedAt        time.Time `json:"createdAt"`
}
type planView struct {
	protocol.ShutdownPlan
	ObservedAt         time.Time `json:"observedAt"`
	EstimatedExecuteAt time.Time `json:"estimatedExecuteAt"`
	Synchronized       bool      `json:"synchronized"`
}
type planRecord struct {
	Plans       map[string]*planView    `json:"plans"`
	Current     string                  `json:"current"`
	Requests    map[string]*planRequest `json:"requests"`
	LastRequest string                  `json:"lastRequest"`
}
type planConnection struct {
	socket   *websocket.Conn
	session  string
	synced   bool
	sequence int64
	writeMu  sync.Mutex
}

func (c *planConnection) send(message protocol.ServerMessage) error {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	return wsjson.Write(ctx, c.socket, message)
}

type shutdownPlans struct {
	mu          sync.Mutex
	records     map[string]*planRecord
	connections map[string]*planConnection
}

func newShutdownPlans() *shutdownPlans {
	return &shutdownPlans{records: map[string]*planRecord{}, connections: map[string]*planConnection{}}
}
func (s *Server) planRecordLocked(id string) (*planRecord, error) {
	if record := s.plans.records[id]; record != nil {
		return record, nil
	}
	data, err := s.store.LoadShutdownRecord(context.Background(), id)
	if err != nil {
		return nil, err
	}
	record := &planRecord{Plans: map[string]*planView{}, Requests: map[string]*planRequest{}}
	if len(data) > 0 {
		if err = json.Unmarshal(data, record); err != nil {
			return nil, err
		}
	}
	for _, p := range record.Plans {
		p.Synchronized = false
	}
	for _, r := range record.Requests {
		if r.Status == "pending" {
			r.Status = "outcome_unknown"
		}
	}
	s.plans.records[id] = record
	return record, nil
}
func (s *Server) savePlanRecordLocked(id string, record *planRecord) error {
	data, err := json.Marshal(record)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	return s.store.SaveShutdownRecord(ctx, id, data)
}
func (s *Server) planDeviceView(id string) ([]string, *planView, *planRequest) {
	s.plans.mu.Lock()
	defer s.plans.mu.Unlock()
	record, err := s.planRecordLocked(id)
	if err != nil {
		return nil, nil, nil
	}
	connection := s.plans.connections[id]
	var caps []string
	if connection != nil {
		caps = []string{protocol.ShutdownPlanCapability}
	}
	var plan *planView
	if original := record.Plans[record.Current]; original != nil {
		copy := *original
		copy.Synchronized = connection != nil && connection.synced && time.Since(copy.ObservedAt) < 3*time.Second
		plan = &copy
	}
	var request *planRequest
	if original := record.Requests[record.LastRequest]; original != nil {
		copy := *original
		request = &copy
	}
	return caps, plan, request
}
func (s *Server) receivePlanMessage(id string, c *planConnection, m protocol.ClientMessage) error {
	s.plans.mu.Lock()
	if s.plans.connections[id] != c || m.SessionID != c.session {
		s.plans.mu.Unlock()
		return errors.New("invalid plan session")
	}
	record, err := s.planRecordLocked(id)
	if err != nil {
		s.plans.mu.Unlock()
		return err
	}
	if m.Sequence < 1 {
		s.plans.mu.Unlock()
		return errors.New("invalid sequence")
	}
	if m.Type != "shutdown_plan_sync" && m.Type != "shutdown_plan_state" && m.Type != "shutdown_plan_result" {
		s.plans.mu.Unlock()
		return errors.New("unknown plan message")
	}
	changed := false
	before, err := json.Marshal(record)
	if err != nil {
		s.plans.mu.Unlock()
		return err
	}
	if m.Plan != nil {
		p := m.Plan
		if !uuidPattern.MatchString(p.OperationID) || p.Revision < 1 || p.Revision > 9007199254740991 || p.RemainingMilliseconds < 0 || p.RemainingMilliseconds > 10000 {
			s.plans.mu.Unlock()
			return errors.New("invalid plan")
		}
		switch p.State {
		case "scheduled", "executing", "submitted", "cancelled", "failed", "indeterminate":
		default:
			s.plans.mu.Unlock()
			return errors.New("invalid plan state")
		}
		old := record.Plans[p.OperationID]
		if old != nil && p.Revision > old.Revision && (old.State == "cancelled" || old.State == "failed" || old.State == "submitted" || old.State == "indeterminate") && p.State != old.State {
			s.plans.mu.Unlock()
			return errors.New("terminal plan cannot transition")
		}
		if m.Sequence > c.sequence && (old == nil || p.Revision >= old.Revision) {
			if old != nil && p.Revision == old.Revision && p.State != old.State {
				s.plans.mu.Unlock()
				return errors.New("revision changed state")
			}
			now := time.Now().UTC()
			if old != nil && old.Revision == p.Revision && p.State == "scheduled" && c.synced {
				remaining := max(int64(0), time.Until(old.EstimatedExecuteAt).Milliseconds())
				p.RemainingMilliseconds = min(p.RemainingMilliseconds, remaining)
			}
			record.Plans[p.OperationID] = &planView{ShutdownPlan: *p, ObservedAt: now, EstimatedExecuteAt: now.Add(time.Duration(p.RemainingMilliseconds) * time.Millisecond), Synchronized: true}
			if record.Current == "" || p.State == "scheduled" || record.Current == p.OperationID {
				record.Current = p.OperationID
			}
			changed = old == nil || p.Revision != old.Revision
		}
	}
	var result *protocol.ShutdownResult
	if m.Type == "shutdown_plan_result" {
		result = &protocol.ShutdownResult{CommandID: m.CommandID, OperationID: m.OperationID, Accepted: m.Accepted, Code: m.Code, Plan: m.Plan}
	}
	if m.Type == "shutdown_plan_sync" {
		result = m.Result
	}
	if result != nil {
		for _, request := range record.Requests {
			if request.CommandID == result.CommandID && request.OperationID == result.OperationID {
				if result.Accepted {
					request.Status = "confirmed"
				} else {
					request.Status = "rejected"
					request.Code = result.Code
				}
				changed = true
				break
			}
		}
	}
	if m.Sequence > c.sequence {
		c.sequence = m.Sequence
	}
	if m.Type == "shutdown_plan_sync" && m.Complete && m.Plan == nil && record.Current != "" {
		record.Current = ""
		changed = true
	}
	if changed {
		err = s.savePlanRecordLocked(id, record)
		if err != nil {
			var restored planRecord
			_ = json.Unmarshal(before, &restored)
			*record = restored
		}
	}
	s.plans.mu.Unlock()
	if err != nil {
		return err
	}
	if changed {
		ack := protocol.ServerMessage{Type: "shutdown_plan_recorded", SessionID: c.session, CommandID: m.CommandID}
		if m.Result != nil {
			ack.CommandID = m.Result.CommandID
			ack.OperationID = m.Result.OperationID
		}
		if m.Plan != nil {
			ack.OperationID = m.Plan.OperationID
			ack.Revision = m.Plan.Revision
		}
		if err := c.send(ack); err != nil {
			return err
		}
	}
	if m.Type == "shutdown_plan_sync" && m.Complete {
		if err := c.send(protocol.ServerMessage{Type: "shutdown_plan_synced", SessionID: c.session}); err != nil {
			return err
		}
		s.plans.mu.Lock()
		c.synced = true
		s.plans.mu.Unlock()
	}
	s.broker.Publish(events.Event{Type: "device.updated", DeviceID: id})
	return nil
}
func (s *Server) handleShutdownPlan(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	operation := r.PathValue("operationId")
	action := r.PathValue("action")
	if _, err := s.store.GetDevice(r.Context(), id); err != nil {
		writeError(w, 404, "device_not_found", "设备不存在")
		return
	}
	var body struct {
		OperationID      string `json:"operationId"`
		RequestID        string `json:"requestId"`
		ExpectedRevision int64  `json:"expectedRevision"`
	}
	if r.Method == http.MethodPost {
		if err := decodeJSON(w, r, &body); err != nil {
			writeError(w, 400, "invalid_request", "请求格式无效")
			return
		}
		if operation == "" {
			operation = body.OperationID
			action = "create"
		}
	}
	if !uuidPattern.MatchString(operation) {
		writeError(w, 400, "invalid_request", "计划 ID 无效")
		return
	}
	key := operation
	if r.Method == http.MethodGet {
		if q := r.URL.Query().Get("requestId"); q != "" {
			key = q
		}
	} else if action != "create" {
		key = body.RequestID
		if !uuidPattern.MatchString(key) || body.ExpectedRevision < 1 {
			writeError(w, 400, "invalid_request", "请求 ID 或修订号无效")
			return
		}
	}
	s.plans.mu.Lock()
	record, err := s.planRecordLocked(id)
	if err != nil {
		s.plans.mu.Unlock()
		writeError(w, 503, "storage_unavailable", "计划存储不可用")
		return
	}
	existing := record.Requests[key]
	if r.Method == http.MethodGet {
		if existing == nil || existing.OperationID != operation {
			s.plans.mu.Unlock()
			writeError(w, 404, "request_not_found", "请求记录不存在")
			return
		}
		response := s.planResponseLocked(id, record, key)
		s.plans.mu.Unlock()
		writeJSON(w, 200, response)
		return
	}
	if action != "create" && action != "cancel" && action != "execute" {
		s.plans.mu.Unlock()
		writeError(w, 400, "invalid_request", "操作无效")
		return
	}
	if existing != nil {
		if existing.OperationID != operation || existing.Action != action || existing.ExpectedRevision != body.ExpectedRevision {
			s.plans.mu.Unlock()
			writeError(w, 409, "idempotency_conflict", "请求 ID 已用于其他操作")
			return
		}
		response := s.planResponseLocked(id, record, key)
		status := planHTTPStatus(existing)
		s.plans.mu.Unlock()
		writeJSON(w, status, response)
		return
	}
	c := s.plans.connections[id]
	if !s.hub.IsOnline(id) {
		s.plans.mu.Unlock()
		writeError(w, 409, "device_offline", "设备离线，无法发送操作")
		return
	}
	if c == nil {
		s.plans.mu.Unlock()
		writeError(w, 409, "capability_required", "设备离线或不支持关机计划")
		return
	}
	if !c.synced {
		s.plans.mu.Unlock()
		writeError(w, 409, "client_sync_pending", "设备状态正在同步")
		return
	}
	if action == "create" {
		for _, req := range record.Requests {
			if req.Action == "create" && (req.Status == "pending" || req.Status == "outcome_unknown") {
				s.plans.mu.Unlock()
				writeError(w, 409, "active_plan_exists", "之前的关机请求尚未确认")
				return
			}
		}
		if p := record.Plans[record.Current]; p != nil && (p.State == "scheduled" || p.State == "executing") {
			s.plans.mu.Unlock()
			writeError(w, 409, "active_plan_exists", "已有活动关机计划")
			return
		}
	}
	commandID, err := identity.NewUUID()
	if err != nil {
		s.plans.mu.Unlock()
		writeError(w, 500, "identity_error", "无法创建指令")
		return
	}
	request := &planRequest{RequestID: body.RequestID, OperationID: operation, CommandID: commandID, Action: action, ExpectedRevision: body.ExpectedRevision, Status: "pending", CreatedAt: time.Now().UTC()}
	record.Requests[key] = request
	previousLast := record.LastRequest
	record.LastRequest = key
	if err = s.savePlanRecordLocked(id, record); err != nil {
		delete(record.Requests, key)
		record.LastRequest = previousLast
		s.plans.mu.Unlock()
		writeError(w, 503, "storage_unavailable", "无法保存请求")
		return
	}
	s.plans.mu.Unlock()
	message := protocol.ServerMessage{Type: "shutdown_plan_" + action, SessionID: c.session, CommandID: commandID, OperationID: operation, ExpectedRevision: body.ExpectedRevision, DelaySeconds: 10}
	deadline := time.Now().Add(5 * time.Second)
	sendErr := c.send(message)
	for sendErr == nil && time.Now().Before(deadline) {
		s.plans.mu.Lock()
		done := record.Requests[key].Status != "pending"
		s.plans.mu.Unlock()
		if done {
			break
		}
		select {
		case <-r.Context().Done():
			deadline = time.Now()
		case <-time.After(25 * time.Millisecond):
		}
	}
	s.plans.mu.Lock()
	request = record.Requests[key]
	if request.Status == "pending" {
		request.Status = "outcome_unknown"
		_ = s.savePlanRecordLocked(id, record)
	}
	response := s.planResponseLocked(id, record, key)
	status := planHTTPStatus(request)
	s.plans.mu.Unlock()
	s.broker.Publish(events.Event{Type: "device.updated", DeviceID: id})
	writeJSON(w, status, response)
}
func planHTTPStatus(r *planRequest) int {
	if r.Status == "confirmed" {
		if r.Action == "create" {
			return 201
		}
		return 200
	}
	if r.Status == "rejected" {
		if r.Code == "storage_unavailable" {
			return 503
		}
		if r.Code == "invalid_request" || r.Code == "invalid_delay" {
			return 400
		}
		return 409
	}
	return 202
}
func (s *Server) planResponseLocked(id string, record *planRecord, key string) map[string]any {
	request := *record.Requests[key]
	var plan *planView
	if p := record.Plans[request.OperationID]; p != nil {
		copy := *p
		connection := s.plans.connections[id]
		copy.Synchronized = connection != nil && connection.synced && time.Since(copy.ObservedAt) < 3*time.Second
		plan = &copy
	}
	return map[string]any{"serverTime": time.Now().UTC(), "request": request, "plan": plan}
}
