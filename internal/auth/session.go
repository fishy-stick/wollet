package auth

import (
	"crypto/sha256"
	"sync"
	"time"

	"github.com/fishy-stick/wakelet/internal/identity"
)

type SessionManager struct {
	mu       sync.Mutex
	ttl      time.Duration
	sessions map[[sha256.Size]byte]time.Time
}

func NewSessionManager(ttl time.Duration) *SessionManager {
	return &SessionManager{ttl: ttl, sessions: make(map[[sha256.Size]byte]time.Time)}
}

func (m *SessionManager) Create(now time.Time) (string, time.Time, error) {
	token, err := identity.NewSecret()
	if err != nil {
		return "", time.Time{}, err
	}
	expiresAt := now.Add(m.ttl)
	m.mu.Lock()
	m.sessions[identity.Hash(token)] = expiresAt
	m.mu.Unlock()
	return token, expiresAt, nil
}

func (m *SessionManager) Valid(token string, now time.Time) bool {
	if token == "" {
		return false
	}
	hash := identity.Hash(token)
	m.mu.Lock()
	defer m.mu.Unlock()
	expiresAt, ok := m.sessions[hash]
	if !ok {
		return false
	}
	if !expiresAt.After(now) {
		delete(m.sessions, hash)
		return false
	}
	return true
}

func (m *SessionManager) Delete(token string) {
	if token == "" {
		return
	}
	m.mu.Lock()
	delete(m.sessions, identity.Hash(token))
	m.mu.Unlock()
}

func (m *SessionManager) Cleanup(now time.Time) int {
	m.mu.Lock()
	defer m.mu.Unlock()
	removed := 0
	for hash, expiresAt := range m.sessions {
		if !expiresAt.After(now) {
			delete(m.sessions, hash)
			removed++
		}
	}
	return removed
}
