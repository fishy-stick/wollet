package protocol

const Version = 1

type ClientMessage struct {
	Type            string `json:"type"`
	ProtocolVersion int    `json:"protocolVersion,omitempty"`
	DeviceName      string `json:"deviceName,omitempty"`
	MACAddress      string `json:"macAddress,omitempty"`
	CommandID       string `json:"commandId,omitempty"`
}

type ServerMessage struct {
	Type                     string `json:"type"`
	ProtocolVersion          int    `json:"protocolVersion,omitempty"`
	HeartbeatIntervalSeconds int    `json:"heartbeatIntervalSeconds,omitempty"`
	OfflineAfterSeconds      int    `json:"offlineAfterSeconds,omitempty"`
	CommandID                string `json:"commandId,omitempty"`
	Code                     string `json:"code,omitempty"`
	Message                  string `json:"message,omitempty"`
}
