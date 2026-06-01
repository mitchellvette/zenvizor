-- Phase 1 introduces the IP Helper poll cadence and the session-reap grace.
-- Seeded only; if a user has tuned these already a prior INSERT OR IGNORE will keep their value.

INSERT OR IGNORE INTO settings (key, value) VALUES
    ('pid_table.poll_ms',    '1000'),
    ('session.end_grace_ms', '30000');
