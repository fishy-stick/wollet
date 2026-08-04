package config

import (
	"net"
	"testing"
)

func TestLoadUsesDefaultWOLBroadcast(t *testing.T) {
	t.Setenv("WOLLET_ADMIN_USERNAME", "admin")
	t.Setenv("WOLLET_ADMIN_PASSWORD", "development-password")
	t.Setenv("WOLLET_WOL_BROADCAST", "")

	cfg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	if !cfg.WOLBroadcast.Equal(net.IPv4bcast) {
		t.Fatalf("WOLBroadcast = %v, want %v", cfg.WOLBroadcast, net.IPv4bcast)
	}
}

func TestLoadUsesConfiguredWOLBroadcast(t *testing.T) {
	t.Setenv("WOLLET_ADMIN_USERNAME", "admin")
	t.Setenv("WOLLET_ADMIN_PASSWORD", "development-password")
	t.Setenv("WOLLET_WOL_BROADCAST", "192.168.1.255")

	cfg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	want := net.ParseIP("192.168.1.255")
	if !cfg.WOLBroadcast.Equal(want) {
		t.Fatalf("WOLBroadcast = %v, want %v", cfg.WOLBroadcast, want)
	}
}
