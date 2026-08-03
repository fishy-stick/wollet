CREATE TABLE pairing_tokens (
    token_hash BLOB PRIMARY KEY NOT NULL CHECK(length(token_hash) = 32),
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL
);

CREATE INDEX pairing_tokens_expires_at_idx ON pairing_tokens(expires_at);

CREATE TABLE devices (
    id TEXT PRIMARY KEY NOT NULL,
    credential_hash BLOB NOT NULL UNIQUE CHECK(length(credential_hash) = 32),
    name TEXT NOT NULL,
    mac_address TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    last_seen_at INTEGER
);
