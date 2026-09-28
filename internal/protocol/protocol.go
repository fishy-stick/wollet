package protocol

const Version = 1

type ClientMessage struct {
	ClientVersion    string          `json:"clientVersion,omitempty"`
	Capabilities     []string        `json:"capabilities,omitempty"`
	SessionID        string          `json:"sessionId,omitempty"`
	OperationID      string          `json:"operationId,omitempty"`
	ExpectedRevision int64           `json:"expectedRevision,omitempty"`
	DelaySeconds     int             `json:"delaySeconds,omitempty"`
	Sequence         int64           `json:"sequence,omitempty"`
	Complete         bool            `json:"complete,omitempty"`
	Accepted         bool            `json:"accepted,omitempty"`
	Plan             *ShutdownPlan   `json:"plan,omitempty"`
	Result           *ShutdownResult `json:"result,omitempty"`
	Code             string          `json:"code,omitempty"`
	Type             string          `json:"type"`
	ProtocolVersion  int             `json:"protocolVersion,omitempty"`
	DeviceName       string          `json:"deviceName,omitempty"`
	MACAddress       string          `json:"macAddress,omitempty"`
	CommandID        string          `json:"commandId,omitempty"`
}

type ServerMessage struct {
	ServerVersion            string          `json:"serverVersion,omitempty"`
	SupportedCapabilities    []string        `json:"supportedCapabilities,omitempty"`
	Revision                 int64           `json:"revision,omitempty"`
	Capabilities             []string        `json:"capabilities,omitempty"`
	SessionID                string          `json:"sessionId,omitempty"`
	OperationID              string          `json:"operationId,omitempty"`
	ExpectedRevision         int64           `json:"expectedRevision,omitempty"`
	DelaySeconds             int             `json:"delaySeconds,omitempty"`
	Sequence                 int64           `json:"sequence,omitempty"`
	Complete                 bool            `json:"complete,omitempty"`
	Accepted                 bool            `json:"accepted,omitempty"`
	Plan                     *ShutdownPlan   `json:"plan,omitempty"`
	Result                   *ShutdownResult `json:"result,omitempty"`
	Type                     string          `json:"type"`
	ProtocolVersion          int             `json:"protocolVersion,omitempty"`
	HeartbeatIntervalSeconds int             `json:"heartbeatIntervalSeconds,omitempty"`
	OfflineAfterSeconds      int             `json:"offlineAfterSeconds,omitempty"`
	CommandID                string          `json:"commandId,omitempty"`
	Code                     string          `json:"code,omitempty"`
	Message                  string          `json:"message,omitempty"`
}
