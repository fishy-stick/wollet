package store

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"database/sql"
	"embed"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

var ErrNotFound = errors.New("not found")

//go:embed migrations/*.sql
var migrationFiles embed.FS

type Store struct {
	db *sql.DB
}

type Device struct {
	ID             string
	CredentialHash []byte
	Name           string
	MACAddress     string
	CreatedAt      time.Time
	UpdatedAt      time.Time
	LastSeenAt     *time.Time
}

func Open(ctx context.Context, path string) (*Store, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, fmt.Errorf("create database directory: %w", err)
	}
	absolutePath, err := filepath.Abs(path)
	if err != nil {
		return nil, fmt.Errorf("resolve database path: %w", err)
	}

	databasePath := filepath.ToSlash(absolutePath)
	if filepath.VolumeName(absolutePath) != "" && !strings.HasPrefix(databasePath, "/") {
		databasePath = "/" + databasePath
	}
	dsnURL := &url.URL{Scheme: "file", Path: databasePath}
	query := dsnURL.Query()
	query.Add("_pragma", "busy_timeout(5000)")
	query.Add("_pragma", "journal_mode(WAL)")
	query.Add("_pragma", "foreign_keys(1)")
	dsnURL.RawQuery = query.Encode()

	db, err := sql.Open("sqlite", dsnURL.String())
	if err != nil {
		return nil, fmt.Errorf("open database: %w", err)
	}
	db.SetMaxOpenConns(4)
	db.SetMaxIdleConns(4)
	if err := db.PingContext(ctx); err != nil {
		db.Close()
		return nil, fmt.Errorf("ping database: %w", err)
	}
	_ = os.Chmod(path, 0o600)

	store := &Store{db: db}
	if err := store.migrate(ctx); err != nil {
		db.Close()
		return nil, err
	}
	return store, nil
}

func (s *Store) Close() error {
	return s.db.Close()
}

func (s *Store) Ping(ctx context.Context) error {
	return s.db.PingContext(ctx)
}

func (s *Store) migrate(ctx context.Context) error {
	if _, err := s.db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS schema_migrations (
        version INTEGER PRIMARY KEY,
        applied_at INTEGER NOT NULL
    )`); err != nil {
		return fmt.Errorf("create migrations table: %w", err)
	}

	entries, err := migrationFiles.ReadDir("migrations")
	if err != nil {
		return fmt.Errorf("read migrations: %w", err)
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name() < entries[j].Name() })
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".sql") {
			continue
		}
		var version int
		if _, err := fmt.Sscanf(entry.Name(), "%03d_", &version); err != nil {
			return fmt.Errorf("invalid migration name %q", entry.Name())
		}
		var exists int
		err := s.db.QueryRowContext(ctx, "SELECT 1 FROM schema_migrations WHERE version = ?", version).Scan(&exists)
		if err == nil {
			continue
		}
		if !errors.Is(err, sql.ErrNoRows) {
			return fmt.Errorf("check migration %d: %w", version, err)
		}
		contents, err := migrationFiles.ReadFile("migrations/" + entry.Name())
		if err != nil {
			return fmt.Errorf("read migration %d: %w", version, err)
		}
		tx, err := s.db.BeginTx(ctx, nil)
		if err != nil {
			return fmt.Errorf("begin migration %d: %w", version, err)
		}
		if _, err := tx.ExecContext(ctx, string(contents)); err != nil {
			tx.Rollback()
			return fmt.Errorf("apply migration %d: %w", version, err)
		}
		if _, err := tx.ExecContext(ctx, "INSERT INTO schema_migrations(version, applied_at) VALUES (?, ?)", version, time.Now().UTC().UnixMilli()); err != nil {
			tx.Rollback()
			return fmt.Errorf("record migration %d: %w", version, err)
		}
		if err := tx.Commit(); err != nil {
			return fmt.Errorf("commit migration %d: %w", version, err)
		}
	}
	return nil
}

func (s *Store) CreatePairingToken(ctx context.Context, hash [32]byte, createdAt, expiresAt time.Time) error {
	_, err := s.db.ExecContext(ctx,
		"INSERT INTO pairing_tokens(token_hash, created_at, expires_at) VALUES (?, ?, ?)",
		hash[:], createdAt.UTC().UnixMilli(), expiresAt.UTC().UnixMilli())
	return err
}

func (s *Store) ConsumePairingToken(ctx context.Context, hash [32]byte, now time.Time, device Device) (bool, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return false, err
	}
	defer tx.Rollback()

	result, err := tx.ExecContext(ctx, "DELETE FROM pairing_tokens WHERE token_hash = ? AND expires_at > ?", hash[:], now.UTC().UnixMilli())
	if err != nil {
		return false, err
	}
	consumed, err := result.RowsAffected()
	if err != nil {
		return false, err
	}
	if consumed != 1 {
		return false, nil
	}

	_, err = tx.ExecContext(ctx, `INSERT INTO devices(
        id, credential_hash, name, mac_address, created_at, updated_at, last_seen_at
    ) VALUES (?, ?, ?, ?, ?, ?, NULL)`,
		device.ID, device.CredentialHash, device.Name, device.MACAddress,
		device.CreatedAt.UTC().UnixMilli(), device.UpdatedAt.UTC().UnixMilli())
	if err != nil {
		return false, err
	}
	if err := tx.Commit(); err != nil {
		return false, err
	}
	return true, nil
}

func (s *Store) CleanupExpiredTokens(ctx context.Context, now time.Time) (int64, error) {
	result, err := s.db.ExecContext(ctx, "DELETE FROM pairing_tokens WHERE expires_at <= ?", now.UTC().UnixMilli())
	if err != nil {
		return 0, err
	}
	return result.RowsAffected()
}

func (s *Store) ListDevices(ctx context.Context) ([]Device, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT id, credential_hash, name, mac_address,
        created_at, updated_at, last_seen_at FROM devices ORDER BY created_at, id`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var devices []Device
	for rows.Next() {
		device, err := scanDevice(rows)
		if err != nil {
			return nil, err
		}
		devices = append(devices, device)
	}
	return devices, rows.Err()
}

func (s *Store) GetDevice(ctx context.Context, id string) (Device, error) {
	row := s.db.QueryRowContext(ctx, `SELECT id, credential_hash, name, mac_address,
        created_at, updated_at, last_seen_at FROM devices WHERE id = ?`, id)
	device, err := scanDevice(row)
	if errors.Is(err, sql.ErrNoRows) {
		return Device{}, ErrNotFound
	}
	return device, err
}

func (s *Store) AuthenticateDevice(ctx context.Context, id, secret string) (Device, error) {
	device, err := s.GetDevice(ctx, id)
	if err != nil {
		return Device{}, err
	}
	hash := sha256String(secret)
	if len(device.CredentialHash) != len(hash) || subtle.ConstantTimeCompare(device.CredentialHash, hash) != 1 {
		return Device{}, ErrNotFound
	}
	return device, nil
}

func (s *Store) UpdateDeviceConnection(ctx context.Context, id, name, mac string, seenAt time.Time) error {
	result, err := s.db.ExecContext(ctx, `UPDATE devices SET name = ?, mac_address = ?,
        updated_at = ?, last_seen_at = ? WHERE id = ?`,
		name, mac, seenAt.UTC().UnixMilli(), seenAt.UTC().UnixMilli(), id)
	if err != nil {
		return err
	}
	return requireAffected(result)
}

func (s *Store) TouchDevice(ctx context.Context, id string, seenAt time.Time) error {
	result, err := s.db.ExecContext(ctx, "UPDATE devices SET last_seen_at = ?, updated_at = ? WHERE id = ?",
		seenAt.UTC().UnixMilli(), seenAt.UTC().UnixMilli(), id)
	if err != nil {
		return err
	}
	return requireAffected(result)
}

func (s *Store) DeleteDevice(ctx context.Context, id string) error {
	result, err := s.db.ExecContext(ctx, "DELETE FROM devices WHERE id = ?", id)
	if err != nil {
		return err
	}
	return requireAffected(result)
}

type scanner interface {
	Scan(dest ...any) error
}

func scanDevice(row scanner) (Device, error) {
	var device Device
	var createdAt, updatedAt int64
	var lastSeenAt sql.NullInt64
	err := row.Scan(&device.ID, &device.CredentialHash, &device.Name, &device.MACAddress,
		&createdAt, &updatedAt, &lastSeenAt)
	if err != nil {
		return Device{}, err
	}
	device.CreatedAt = time.UnixMilli(createdAt).UTC()
	device.UpdatedAt = time.UnixMilli(updatedAt).UTC()
	if lastSeenAt.Valid {
		value := time.UnixMilli(lastSeenAt.Int64).UTC()
		device.LastSeenAt = &value
	}
	return device, nil
}

func requireAffected(result sql.Result) error {
	count, err := result.RowsAffected()
	if err != nil {
		return err
	}
	if count != 1 {
		return ErrNotFound
	}
	return nil
}

func sha256String(value string) []byte {
	hash := sha256.Sum256([]byte(value))
	return hash[:]
}
