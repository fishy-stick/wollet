package identity

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"strings"
)

const crockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

func NewPairingToken() (display string, normalized string, err error) {
	raw := make([]byte, 20)
	if _, err = rand.Read(raw); err != nil {
		return "", "", err
	}
	chars := make([]byte, 20)
	for i, value := range raw {
		chars[i] = crockfordAlphabet[int(value)&31]
	}
	normalized = string(chars)
	groups := []string{
		normalized[0:5], normalized[5:10], normalized[10:15], normalized[15:20],
	}
	return strings.Join(groups, "-"), normalized, nil
}

func NormalizePairingToken(value string) (string, error) {
	value = strings.ToUpper(strings.TrimSpace(value))
	value = strings.NewReplacer("-", "", " ", "").Replace(value)
	if len(value) != 20 {
		return "", errors.New("pairing token must contain 20 characters")
	}
	for _, char := range value {
		if !strings.ContainsRune(crockfordAlphabet, char) {
			return "", errors.New("pairing token contains invalid characters")
		}
	}
	return value, nil
}

func NewSecret() (string, error) {
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(raw), nil
}

func NewUUID() (string, error) {
	raw := make([]byte, 16)
	if _, err := rand.Read(raw); err != nil {
		return "", err
	}
	raw[6] = (raw[6] & 0x0f) | 0x40
	raw[8] = (raw[8] & 0x3f) | 0x80
	encoded := make([]byte, 36)
	hex.Encode(encoded[0:8], raw[0:4])
	encoded[8] = '-'
	hex.Encode(encoded[9:13], raw[4:6])
	encoded[13] = '-'
	hex.Encode(encoded[14:18], raw[6:8])
	encoded[18] = '-'
	hex.Encode(encoded[19:23], raw[8:10])
	encoded[23] = '-'
	hex.Encode(encoded[24:36], raw[10:16])
	return string(encoded), nil
}

func Hash(value string) [sha256.Size]byte {
	return sha256.Sum256([]byte(value))
}
