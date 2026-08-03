package auth

import (
	"sync"
	"time"
)

type rateWindow struct {
	Count   int
	ResetAt time.Time
}

type Limiter struct {
	mu      sync.Mutex
	limit   int
	window  time.Duration
	entries map[string]rateWindow
}

func NewLimiter(limit int, window time.Duration) *Limiter {
	return &Limiter{limit: limit, window: window, entries: make(map[string]rateWindow)}
}

func (l *Limiter) Allow(key string, now time.Time) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	entry := l.entries[key]
	if entry.ResetAt.IsZero() || !entry.ResetAt.After(now) {
		l.entries[key] = rateWindow{Count: 1, ResetAt: now.Add(l.window)}
		return true
	}
	if entry.Count >= l.limit {
		return false
	}
	entry.Count++
	l.entries[key] = entry
	return true
}

func (l *Limiter) Reset(key string) {
	l.mu.Lock()
	delete(l.entries, key)
	l.mu.Unlock()
}

func (l *Limiter) Cleanup(now time.Time) {
	l.mu.Lock()
	defer l.mu.Unlock()
	for key, entry := range l.entries {
		if !entry.ResetAt.After(now) {
			delete(l.entries, key)
		}
	}
}
