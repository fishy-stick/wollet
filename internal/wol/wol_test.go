package wol

import (
	"bytes"
	"net"
	"testing"
)

func TestMagicPacket(t *testing.T) {
	mac, _ := net.ParseMAC("A4:83:E7:19:2C:5A")
	packet, err := MagicPacket(mac)
	if err != nil {
		t.Fatal(err)
	}
	if len(packet) != 102 {
		t.Fatalf("got packet length %d", len(packet))
	}
	if !bytes.Equal(packet[:6], bytes.Repeat([]byte{0xff}, 6)) {
		t.Fatal("missing magic packet prefix")
	}
	for i := 0; i < 16; i++ {
		if !bytes.Equal(packet[6+i*6:12+i*6], mac) {
			t.Fatalf("MAC repetition %d is wrong", i)
		}
	}
}
