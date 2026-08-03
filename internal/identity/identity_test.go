package identity

import (
	"strings"
	"testing"
)

func TestPairingTokenRoundTrip(t *testing.T) {
	display, normalized, err := NewPairingToken()
	if err != nil {
		t.Fatal(err)
	}
	if len(display) != 23 || strings.Count(display, "-") != 3 {
		t.Fatalf("unexpected display token %q", display)
	}
	if len(normalized) != 20 {
		t.Fatalf("unexpected normalized token length %d", len(normalized))
	}
	parsed, err := NormalizePairingToken(strings.ToLower(display))
	if err != nil {
		t.Fatal(err)
	}
	if parsed != normalized {
		t.Fatalf("normalized token mismatch: got %q want %q", parsed, normalized)
	}
}

func TestNormalizePairingTokenRejectsAmbiguousCharacters(t *testing.T) {
	if _, err := NormalizePairingToken("OOOOO-OOOOO-OOOOO-OOOOO"); err == nil {
		t.Fatal("expected invalid token error")
	}
}

func TestNewUUIDShape(t *testing.T) {
	id, err := NewUUID()
	if err != nil {
		t.Fatal(err)
	}
	if len(id) != 36 || id[14] != '4' || (id[19] != '8' && id[19] != '9' && id[19] != 'a' && id[19] != 'b') {
		t.Fatalf("invalid UUID v4 %q", id)
	}
}
