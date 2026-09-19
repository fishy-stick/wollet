package protocol

const ShutdownPlanCapability = "shutdown-plan.v1"

type ShutdownPlan struct {
	OperationID           string `json:"operationId"`
	Revision              int64  `json:"revision"`
	State                 string `json:"state"`
	RemainingMilliseconds int64  `json:"remainingMilliseconds"`
	Reason                string `json:"reason,omitempty"`
}

type ShutdownResult struct {
	CommandID   string        `json:"commandId"`
	OperationID string        `json:"operationId"`
	Accepted    bool          `json:"accepted"`
	Plan        *ShutdownPlan `json:"plan"`
	Code        string        `json:"code,omitempty"`
}
