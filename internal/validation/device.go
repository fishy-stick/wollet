package validation

import (
	"errors"
	"net"
	"strings"
	"unicode/utf8"
)

func DeviceName(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return "", errors.New("device name is required")
	}
	if !utf8.ValidString(value) || utf8.RuneCountInString(value) > 128 {
		return "", errors.New("device name must be valid UTF-8 and at most 128 characters")
	}
	return value, nil
}

func MACAddress(value string) (string, error) {
	hardware, err := net.ParseMAC(strings.TrimSpace(value))
	if err != nil || len(hardware) != 6 {
		return "", errors.New("MAC address must contain six octets")
	}
	allZero := true
	for _, octet := range hardware {
		if octet != 0 {
			allZero = false
			break
		}
	}
	if allZero || hardware[0]&1 != 0 {
		return "", errors.New("MAC address must be a non-zero unicast address")
	}
	return strings.ToUpper(hardware.String()), nil
}
