package validation

import "testing"

func TestMACAddressNormalization(t *testing.T) {
	got, err := MACAddress("a4-83-e7-19-2c-5a")
	if err != nil {
		t.Fatal(err)
	}
	if got != "A4:83:E7:19:2C:5A" {
		t.Fatalf("got %q", got)
	}
}

func TestMACAddressRejectsInvalidAddresses(t *testing.T) {
	for _, value := range []string{"", "00:00:00:00:00:00", "01:00:5E:00:00:01", "AA:BB:CC"} {
		if _, err := MACAddress(value); err == nil {
			t.Errorf("expected %q to be rejected", value)
		}
	}
}

func TestDeviceName(t *testing.T) {
	got, err := DeviceName("  工作站  ")
	if err != nil {
		t.Fatal(err)
	}
	if got != "工作站" {
		t.Fatalf("got %q", got)
	}
}
