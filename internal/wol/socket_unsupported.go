//go:build !linux && !windows

package wol

import (
	"errors"
	"syscall"
)

func enableBroadcast(_, _ string, _ syscall.RawConn) error {
	return errors.New("Wake-on-LAN is supported only on Linux and Windows")
}
