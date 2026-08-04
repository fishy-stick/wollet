package config

import (
	"errors"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"time"
)

const (
	defaultListenAddr   = "0.0.0.0:8080"
	defaultDBPath       = "/data/wollet.db"
	defaultWOLBroadcast = "255.255.255.255"
)

type Config struct {
	ListenAddr      string
	DBPath          string
	AdminUsername   string
	AdminPassword   string
	WOLBroadcast    net.IP
	WOLPort         int
	LogLevel        string
	SessionTTL      time.Duration
	HeartbeatEvery  time.Duration
	OfflineAfter    time.Duration
	HelloTimeout    time.Duration
	CommandTimeout  time.Duration
	TokenTTL        time.Duration
	CleanupInterval time.Duration
}

func Load() (Config, error) {
	cfg := Config{
		ListenAddr:      envOrDefault("WOLLET_LISTEN_ADDR", defaultListenAddr),
		DBPath:          envOrDefault("WOLLET_DB_PATH", defaultDBPath),
		AdminUsername:   envOrDefault("WOLLET_ADMIN_USERNAME", "admin"),
		AdminPassword:   os.Getenv("WOLLET_ADMIN_PASSWORD"),
		LogLevel:        strings.ToLower(envOrDefault("WOLLET_LOG_LEVEL", "info")),
		SessionTTL:      12 * time.Hour,
		HeartbeatEvery:  15 * time.Second,
		OfflineAfter:    45 * time.Second,
		HelloTimeout:    10 * time.Second,
		CommandTimeout:  5 * time.Second,
		TokenTTL:        5 * time.Minute,
		CleanupInterval: time.Minute,
	}

	if len(cfg.AdminUsername) > 128 {
		return Config{}, errors.New("WOLLET_ADMIN_USERNAME must be at most 128 characters")
	}
	if cfg.AdminPassword != "" && len(cfg.AdminPassword) < 12 {
		return Config{}, errors.New("WOLLET_ADMIN_PASSWORD must be empty or at least 12 characters")
	}
	if cfg.DBPath == "" {
		return Config{}, errors.New("WOLLET_DB_PATH must not be empty")
	}
	if _, _, err := net.SplitHostPort(cfg.ListenAddr); err != nil {
		return Config{}, fmt.Errorf("invalid WOLLET_LISTEN_ADDR: %w", err)
	}

	broadcast := net.ParseIP(envOrDefault("WOLLET_WOL_BROADCAST", defaultWOLBroadcast))
	if broadcast == nil || broadcast.To4() == nil {
		return Config{}, errors.New("WOLLET_WOL_BROADCAST must be a valid IPv4 address")
	}
	if broadcast.IsUnspecified() || broadcast.IsMulticast() {
		return Config{}, errors.New("WOLLET_WOL_BROADCAST must be an IPv4 broadcast address")
	}
	cfg.WOLBroadcast = broadcast.To4()

	port, err := strconv.Atoi(envOrDefault("WOLLET_WOL_PORT", "9"))
	if err != nil || port < 1 || port > 65535 {
		return Config{}, errors.New("WOLLET_WOL_PORT must be between 1 and 65535")
	}
	cfg.WOLPort = port

	switch cfg.LogLevel {
	case "debug", "info", "warn", "error":
	default:
		return Config{}, errors.New("WOLLET_LOG_LEVEL must be debug, info, warn, or error")
	}

	return cfg, nil
}

func envOrDefault(name, fallback string) string {
	if value := strings.TrimSpace(os.Getenv(name)); value != "" {
		return value
	}
	return fallback
}
