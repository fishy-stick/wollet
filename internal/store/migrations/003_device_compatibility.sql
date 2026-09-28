CREATE TABLE device_compatibility (
    device_id TEXT PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
    payload TEXT NOT NULL
);
