package wol

import (
	"context"
	"fmt"
	"net"
)

type Sender struct {
	Broadcast net.IP
	Port      int
}

func MagicPacket(mac net.HardwareAddr) ([]byte, error) {
	if len(mac) != 6 {
		return nil, fmt.Errorf("MAC address must contain six octets")
	}
	packet := make([]byte, 6+16*len(mac))
	for i := 0; i < 6; i++ {
		packet[i] = 0xff
	}
	for i := 0; i < 16; i++ {
		copy(packet[6+i*len(mac):], mac)
	}
	return packet, nil
}

func (s Sender) Send(ctx context.Context, mac net.HardwareAddr) error {
	packet, err := MagicPacket(mac)
	if err != nil {
		return err
	}
	if s.Broadcast == nil || s.Broadcast.To4() == nil {
		return fmt.Errorf("invalid IPv4 broadcast address")
	}

	listenConfig := net.ListenConfig{Control: enableBroadcast}
	packetConn, err := listenConfig.ListenPacket(ctx, "udp4", "0.0.0.0:0")
	if err != nil {
		return fmt.Errorf("open UDP socket: %w", err)
	}
	defer packetConn.Close()
	if deadline, ok := ctx.Deadline(); ok {
		_ = packetConn.SetWriteDeadline(deadline)
	}
	_, err = packetConn.WriteTo(packet, &net.UDPAddr{IP: s.Broadcast.To4(), Port: s.Port})
	if err != nil {
		return fmt.Errorf("send magic packet: %w", err)
	}
	return nil
}
