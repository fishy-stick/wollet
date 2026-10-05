package protocol

import (
	"encoding/json"
)

const DeviceStatusCapability = "device-status.v1"
const MaxSafeInteger int64 = 1<<53 - 1

// Invalid optional telemetry must never reject an otherwise valid heartbeat.
type DeviceStatus struct {
	SystemUptimeMs *int64 `json:"systemUptimeMs"`
	Invalid        bool   `json:"-"`
}

func (s *DeviceStatus) UnmarshalJSON(data []byte) error {
	*s = DeviceStatus{}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil || fields == nil {
		s.Invalid = true
		return nil
	}
	if raw, ok := fields["systemUptimeMs"]; ok && string(raw) != "null" {
		var value int64
		if err := json.Unmarshal(raw, &value); err != nil || value < 0 || value > MaxSafeInteger {
			s.Invalid = true
		} else {
			s.SystemUptimeMs = &value
		}
	}
	return nil
}
