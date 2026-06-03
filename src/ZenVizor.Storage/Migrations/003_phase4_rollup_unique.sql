-- Phase 4: turn the non-unique app/bucket indexes on the rollup tables into
-- unique ones so the flush sink can use ON CONFLICT (...) DO UPDATE for
-- incremental UPSERT into traffic_hourly / traffic_daily inside the same
-- transaction that writes traffic_samples (Q1 decision: incremental at flush).
--
-- The previous indexes were non-unique because the rollup writer didn't
-- exist yet; uniqueness lets us key on (app_id, bucket_start, remote_class)
-- so that two flushes into the same hour for the same app+remote-class
-- accumulate into one row instead of inserting duplicates.

DROP INDEX IF EXISTS ix_traffic_hourly_app_bucket;
DROP INDEX IF EXISTS ix_traffic_daily_app_bucket;

CREATE UNIQUE INDEX IF NOT EXISTS ux_traffic_hourly_app_bucket_class
    ON traffic_hourly (app_id, bucket_start, remote_class);

CREATE UNIQUE INDEX IF NOT EXISTS ux_traffic_daily_app_bucket_class
    ON traffic_daily (app_id, bucket_start, remote_class);
