package store

import (
	"context"
	"database/sql"
	"errors"
)

func (s *Store) LoadShutdownRecord(ctx context.Context, deviceID string) ([]byte, error) {
	var payload []byte
	err := s.db.QueryRowContext(ctx, "SELECT payload FROM shutdown_plan_records WHERE device_id = ?", deviceID).Scan(&payload)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil
	}
	return payload, err
}
func (s *Store) SaveShutdownRecord(ctx context.Context, deviceID string, payload []byte) error {
	_, err := s.db.ExecContext(ctx, `INSERT INTO shutdown_plan_records(device_id,payload) VALUES(?,?)
 ON CONFLICT(device_id) DO UPDATE SET payload=excluded.payload`, deviceID, payload)
	return err
}
