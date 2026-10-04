package protocol

import (
	"encoding/json"
	"testing"
)

func TestOptionalStatusValidation(t *testing.T) {
	for _, test := range []struct {
		name, input string
		uptime      *int64
		invalid     bool
	}{
		{"zero uptime", `{"systemUptimeMs":0}`, intPointer(0), false},
		{"positive", `{"systemUptimeMs":123}`, intPointer(123), false},
		{"client IP ignored", `{"localIpAddress":"2001:db8::20","systemUptimeMs":5}`, intPointer(5), false},
		{"explicit unknown", `{"systemUptimeMs":null}`, nil, false},
		{"missing", `{}`, nil, false},
		{"negative", `{"systemUptimeMs":-1}`, nil, true},
		{"unsafe integer", `{"systemUptimeMs":9007199254740992}`, nil, true},
		{"safe integer", `{"systemUptimeMs":9007199254740991}`, intPointer(MaxSafeInteger), false},
		{"fraction", `{"systemUptimeMs":1.5}`, nil, true},
		{"wrong type", `{"systemUptimeMs":"100"}`, nil, true},
		{"nonobject", `42`, nil, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			var message ClientMessage
			if err := json.Unmarshal([]byte(`{"type":"heartbeat","deviceStatus":`+test.input+`}`), &message); err != nil {
				t.Fatalf("optional telemetry broke heartbeat: %v", err)
			}
			status := message.DeviceStatus
			if status == nil || status.Invalid != test.invalid {
				t.Fatalf("status: %+v", status)
			}
			if (status.SystemUptimeMs == nil) != (test.uptime == nil) || status.SystemUptimeMs != nil && *status.SystemUptimeMs != *test.uptime {
				t.Fatalf("uptime: %+v", status)
			}
		})
	}
	for _, input := range []string{`{"type":"heartbeat"}`, `{"type":"heartbeat","deviceStatus":null}`} {
		var message ClientMessage
		if err := json.Unmarshal([]byte(input), &message); err != nil || message.DeviceStatus != nil {
			t.Fatalf("legacy/null heartbeat: %+v %v", message, err)
		}
	}
}

func intPointer(value int64) *int64 { return &value }
