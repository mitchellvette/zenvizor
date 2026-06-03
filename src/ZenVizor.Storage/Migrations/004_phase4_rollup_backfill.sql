-- Phase 4 one-time backfill: populate traffic_hourly + traffic_daily from
-- any traffic_samples that exist at the moment this migration runs.
--
-- Background: migration 003 added unique indexes to enable rollup UPSERTs,
-- but those UPSERTs only fire on NEW flushes after Phase 4 ships. Existing
-- traffic_samples rows (up to 30 days of Phase-1/2/3 data) never got rolled
-- up. Without this backfill, the Per-App view on >6h windows (which
-- auto-selects the Hourly tier) returns nothing until enough new flushes
-- have happened to populate it from scratch -- a fresh-Phase-4 deployment
-- looks empty for the first day, which is misleading.
--
-- Safety: pre-Phase-4 nothing wrote to traffic_hourly / traffic_daily, so a
-- straight INSERT without ON CONFLICT is correct. If for some reason rows
-- existed (e.g. someone re-applied this migration manually), INSERT OR
-- IGNORE on the unique index would skip duplicates; we use ON CONFLICT DO
-- UPDATE to be safe and accumulate, which is mathematically wrong only if
-- the same samples were rolled up twice -- but the migration runner ensures
-- this only runs once per database.

INSERT INTO traffic_hourly (app_id, bucket_start, remote_class, bytes_up, bytes_down)
SELECT ps.app_id,
       (s.bucket_start / 3600000) * 3600000 AS hour_bucket,
       s.remote_class,
       SUM(s.bytes_up),
       SUM(s.bytes_down)
FROM traffic_samples s
JOIN process_sessions ps ON ps.session_id = s.session_id
GROUP BY ps.app_id, hour_bucket, s.remote_class
ON CONFLICT (app_id, bucket_start, remote_class) DO NOTHING;

INSERT INTO traffic_daily (app_id, bucket_start, remote_class, bytes_up, bytes_down)
SELECT ps.app_id,
       (s.bucket_start / 86400000) * 86400000 AS day_bucket,
       s.remote_class,
       SUM(s.bytes_up),
       SUM(s.bytes_down)
FROM traffic_samples s
JOIN process_sessions ps ON ps.session_id = s.session_id
GROUP BY ps.app_id, day_bucket, s.remote_class
ON CONFLICT (app_id, bucket_start, remote_class) DO NOTHING;
