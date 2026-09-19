CREATE TABLE shutdown_plan_records (
    device_id TEXT PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
    payload TEXT NOT NULL
);
