package store

import (
	"context"
	"database/sql"
	"errors"
)

func (s *Store) SaveCompatibility(ctx context.Context, id string, payload []byte) error {
	_, err := s.db.ExecContext(ctx, `INSERT INTO device_compatibility(device_id,payload) VALUES(?,?) ON CONFLICT(device_id) DO UPDATE SET payload=excluded.payload`, id, string(payload))
	return err
}
func (s *Store) LoadCompatibility(ctx context.Context, id string) ([]byte, error) {
	var payload string
	err := s.db.QueryRowContext(ctx, `SELECT payload FROM device_compatibility WHERE device_id=?`, id).Scan(&payload)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil
	}
	return []byte(payload), err
}
