-- Phase 6.7 — alert producer thresholds become user-settable. Three new
-- keys, all integers (UnusualDailyVolume k factor stored × 10 to keep the
-- settings table integer-typed; UI shows decimal).
--
--   alert.large_download_mb           — LargeDownload byte threshold (MB).
--                                       Default 50.
--   alert.outbound_heavy_floor_mb     — OutboundHeavy minimum outbound MB
--                                       over the rolling 15 min for an
--                                       app to qualify. Default 10.
--   alert.unusual_daily_volume_k_x10  — UnusualDailyVolume sensitivity ×
--                                       10. Default 25 (k = 2.5). Formula:
--                                       alert when day total ≥ k × median
--                                       (last 14 days) AND day delta over
--                                       median ≥ 50 MB hard-coded floor.
--                                       Divergence from the original
--                                       median + k × MAD formula documented
--                                       in IpcSchemaVersion.Settings remarks;
--                                       revert there if low-variance apps
--                                       generate noise.
--
-- Bumps IpcSchemaVersion.Settings from 2 → 3. Older UI clients with the
-- v2 floor reject as expected; the migration is service-side and lands
-- with the same install that ships the v3-aware UI.

INSERT OR IGNORE INTO settings (key, value) VALUES
    ('alert.large_download_mb',           '50'),
    ('alert.outbound_heavy_floor_mb',     '10'),
    ('alert.unusual_daily_volume_k_x10',  '25');
