-- TitaniRun initial schema. Covers PRD §7.1–7.8.
-- All timestamps are Unix-ms unless noted. Bytes are 64-bit.
-- (journal_mode = WAL is set on the connection by Migrator before this runs.)

-- ============================================================================
-- 7.1  apps  — deduplicated process identity
-- ============================================================================
CREATE TABLE IF NOT EXISTS apps (
    app_id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    image_path              TEXT    NOT NULL,
    image_name              TEXT    NOT NULL,
    publisher               TEXT    NULL,
    signature_status        TEXT    NOT NULL DEFAULT 'Unchecked',
        -- Signed | Unsigned | Invalid | Unchecked
    is_user_writable_path   INTEGER NOT NULL DEFAULT 0,
    first_seen              INTEGER NOT NULL,
    last_seen               INTEGER NOT NULL
);

-- Dedup key: a publisher of NULL is distinct, which matches PRD intent
-- (unsigned-from-temp vs. signed-from-system are different identities).
CREATE UNIQUE INDEX IF NOT EXISTS ux_apps_path_publisher
    ON apps (image_path, IFNULL(publisher, ''));

-- ============================================================================
-- 7.2  process_sessions  — a running PID instance
-- ============================================================================
CREATE TABLE IF NOT EXISTS process_sessions (
    session_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    app_id              INTEGER NOT NULL REFERENCES apps(app_id),
    pid                 INTEGER NOT NULL,
    start_time          INTEGER NOT NULL,
    end_time            INTEGER NULL,
    hosted_services     TEXT    NULL
        -- comma-separated service names for svchost hosts; honest list, no byte split
);

CREATE INDEX IF NOT EXISTS ix_sessions_pid_alive
    ON process_sessions (pid, end_time);

CREATE INDEX IF NOT EXISTS ix_sessions_app
    ON process_sessions (app_id);

-- ============================================================================
-- 7.3  traffic_samples  — high-resolution tier (hot path)
-- ============================================================================
CREATE TABLE IF NOT EXISTS traffic_samples (
    sample_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id      INTEGER NOT NULL REFERENCES process_sessions(session_id),
    bucket_start    INTEGER NOT NULL,   -- aligned bucket (default 60s)
    bytes_up        INTEGER NOT NULL DEFAULT 0,
    bytes_down      INTEGER NOT NULL DEFAULT 0,
    remote_class    TEXT    NOT NULL    -- Local | Wan
);

CREATE INDEX IF NOT EXISTS ix_traffic_samples_bucket
    ON traffic_samples (bucket_start);

CREATE INDEX IF NOT EXISTS ix_traffic_samples_session_bucket
    ON traffic_samples (session_id, bucket_start);

-- ============================================================================
-- 7.4  connections  — drill-down detail aggregated per endpoint
-- ============================================================================
CREATE TABLE IF NOT EXISTS connections (
    connection_id   INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id      INTEGER NOT NULL REFERENCES process_sessions(session_id),
    protocol        TEXT    NOT NULL,    -- TCP | UDP
    remote_addr     TEXT    NOT NULL,
    remote_port     INTEGER NOT NULL,
    remote_class    TEXT    NOT NULL,    -- Local | Wan
    resolved_host   TEXT    NULL,        -- reserved for passive-DNS module (post-MVP)
    bytes_up        INTEGER NOT NULL DEFAULT 0,
    bytes_down      INTEGER NOT NULL DEFAULT 0,
    first_seen      INTEGER NOT NULL,
    last_seen       INTEGER NOT NULL
);

-- Supports the per-endpoint upsert key the aggregator uses.
CREATE UNIQUE INDEX IF NOT EXISTS ux_connections_endpoint
    ON connections (session_id, protocol, remote_addr, remote_port);

-- ============================================================================
-- 7.5  traffic_hourly / traffic_daily  — rollup tiers
-- ============================================================================
CREATE TABLE IF NOT EXISTS traffic_hourly (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    app_id          INTEGER NOT NULL REFERENCES apps(app_id),
    bucket_start    INTEGER NOT NULL,    -- hour-aligned
    bytes_up        INTEGER NOT NULL DEFAULT 0,
    bytes_down      INTEGER NOT NULL DEFAULT 0,
    remote_class    TEXT    NOT NULL     -- Local | Wan
);

CREATE INDEX IF NOT EXISTS ix_traffic_hourly_app_bucket
    ON traffic_hourly (app_id, bucket_start);

CREATE INDEX IF NOT EXISTS ix_traffic_hourly_bucket
    ON traffic_hourly (bucket_start);

CREATE TABLE IF NOT EXISTS traffic_daily (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    app_id          INTEGER NOT NULL REFERENCES apps(app_id),
    bucket_start    INTEGER NOT NULL,    -- day-aligned
    bytes_up        INTEGER NOT NULL DEFAULT 0,
    bytes_down      INTEGER NOT NULL DEFAULT 0,
    remote_class    TEXT    NOT NULL     -- Local | Wan
);

CREATE INDEX IF NOT EXISTS ix_traffic_daily_app_bucket
    ON traffic_daily (app_id, bucket_start);

CREATE INDEX IF NOT EXISTS ix_traffic_daily_bucket
    ON traffic_daily (bucket_start);

-- ============================================================================
-- 7.6  alerts  — generic raise/acknowledge feed (seam #2)
-- ============================================================================
CREATE TABLE IF NOT EXISTS alerts (
    alert_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    type                TEXT    NOT NULL,    -- e.g., UnsignedFromUserPath
    severity            TEXT    NOT NULL,    -- Info | Warning | Critical
    created_at          INTEGER NOT NULL,
    source_monitor      TEXT    NOT NULL,
    entity_kind         TEXT    NOT NULL,    -- App | Session | Device | File ...
    entity_ref          TEXT    NOT NULL,
    title               TEXT    NOT NULL,
    detail              TEXT    NOT NULL,
    acknowledged_at     INTEGER NULL
);

CREATE INDEX IF NOT EXISTS ix_alerts_created
    ON alerts (created_at);

CREATE INDEX IF NOT EXISTS ix_alerts_unacknowledged
    ON alerts (acknowledged_at)
    WHERE acknowledged_at IS NULL;

-- ============================================================================
-- 7.7  devices  — RESERVED. Defined, not populated in MVP.
-- ============================================================================
CREATE TABLE IF NOT EXISTS devices (
    device_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    mac             TEXT    NOT NULL,
    ip              TEXT    NOT NULL,
    interface       TEXT    NOT NULL,
    hostname        TEXT    NULL,
    first_seen      INTEGER NOT NULL,
    last_seen       INTEGER NOT NULL,
    is_known        INTEGER NOT NULL DEFAULT 0
);

-- ============================================================================
-- 7.8  settings  — key/value config
-- ============================================================================
CREATE TABLE IF NOT EXISTS settings (
    key     TEXT PRIMARY KEY,
    value   TEXT NOT NULL
);

-- Seed §7.9 retention defaults and operating intervals.
INSERT OR IGNORE INTO settings (key, value) VALUES
    ('retention.traffic_samples_days',   '30'),
    ('retention.connections_days',       '30'),
    ('retention.traffic_hourly_days',    '90'),
    ('retention.traffic_daily_days',     '365'),
    ('retention.alerts_days_after_ack',  '90'),
    ('flush.interval_ms',                '5000'),
    ('flush.bucket_seconds',             '60'),
    ('toast.on_alert',                   '1'),
    ('autostart.mirror',                 '1');
