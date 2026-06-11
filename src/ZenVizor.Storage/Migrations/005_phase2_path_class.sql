-- Phase 2 follow-up. Adds a three-state path classification to the apps table.
-- Previously, basename-only ETW attributions (no full path resolvable from
-- ImageFileName / CommandLine) collapsed to is_user_writable_path=0, which
-- the Phase-6 unsigned-binary alert reads as "safe". The 'Unknown' state
-- makes that distinct.
--
-- Values: 'System' | 'UserWritable' | 'Unknown'. The default 'System' for
-- legacy rows preserves current Phase-6 semantics; new sessions write the
-- enrichment's explicit verdict via SqliteFlushSink, and the existing
-- EnrichmentBackfill recomputes it for historical 'Unchecked' rows.

ALTER TABLE apps ADD COLUMN path_class TEXT NOT NULL DEFAULT 'System';
