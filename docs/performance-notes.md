# Performance notes — awareness items

This doc collects performance-related observations that aren't currently
actionable but should be on the table if conditions change. These are
**not follow-up items** — they're "if you ever see X, here's where to start
looking" notes.

---

## Denormalizing `app_id` into `traffic_samples`

> Identified during the Phase-4 freeze debugging (2026-06-02), listed as **Q3**
> in the PR-4 optimization queue. Explicitly deferred — not a backlog item.

### What it is

`traffic_samples` currently links to apps via `process_sessions`:

```
traffic_samples.session_id → process_sessions.session_id → process_sessions.app_id
```

Every per-app query has to JOIN through `process_sessions`:

```sql
FROM traffic_samples s
JOIN process_sessions ps ON ps.session_id = s.session_id
WHERE ps.app_id = $appId
  AND s.bucket_start < $to
  AND s.bucket_start > $from - $bucketMs
GROUP BY s.bucket_start, s.remote_class
```

Adding `app_id` directly to `traffic_samples` (with a composite
`(app_id, bucket_start)` index) would let per-app queries filter without the
join. The flush sink would need to write `app_id` alongside `session_id`.

### When it would start mattering

At current data scale (~27 k samples; chrome over 24 h returns ~1100 rows in
~12 ms server-side) the JOIN cost is a few ms — noise compared to the
~50–100 ms IPC handshake. The cost scales with
`apps × sessions × samples_per_app_window`. Trigger conditions to revisit:

- `GetAppDetail` server-side time creeps past ~200 ms on a typical machine
  (roughly 10× current scale).
- `traffic_samples` row count exceeds ~500 k (roughly 18× current; plausible
  after months of heavy use on a busy machine).
- `EXPLAIN QUERY PLAN` on `SqlAppSeriesFromSamples` shows the `process_sessions`
  scan as the dominant cost rather than the indexed `(session_id, bucket_start)`
  range lookup.
- A user-reported "per-app drill-down feels slow" — measured server-side, not UI.

### Why we haven't done it now

- Risk-to-benefit is bad at current scale: ~2 % of perceptible cost, not 10 %.
  Doesn't clear the bar the project's "as light and fast as possible" principle
  set for follow-up optimization (~10 % wins, per the explicit user direction
  in the optimization-aggressively memory).
- Schema migration requires a backfill from `process_sessions` — touches the
  write hot-path (`SqliteFlushSink`), which CLAUDE.md invariant #4 (no
  per-event DB writes, aggregate in memory) flags as high-blast-radius.
- A standalone migration spends a whole migration's risk budget on a small
  current win. The clean version of this lands as a ride-along on a future
  schema bump (most likely Phase 6 alerts work, which is expected to add at
  least one new column).

### Where to start if it does become relevant

| File | Change |
|---|---|
| `src/ZenVizor.Storage/Migrations/NNN_denorm_app_id_into_samples.sql` | New: `ALTER TABLE traffic_samples ADD COLUMN app_id INTEGER`; backfill from `process_sessions` via `UPDATE traffic_samples SET app_id = (SELECT app_id FROM process_sessions WHERE session_id = traffic_samples.session_id)`; `CREATE INDEX ix_traffic_samples_app_bucket ON traffic_samples (app_id, bucket_start)` |
| `src/ZenVizor.Storage/Repositories/SqliteFlushSink.InsertSamples` | Add `$appId` parameter, resolve via `pidToSessionId` → `sessionIdToAppId` (the same lookup already done in `UpsertRollups`) |
| `src/ZenVizor.Storage/Repositories/AppHistoryQueryRepository.SqlAppSeriesFromSamples` | Drop the JOIN; filter directly by `app_id` and `bucket_start` |
| `src/ZenVizor.Storage/Repositories/AppHistoryQueryRepository.SqlAppListFromSamples` | Same simplification — direct `app_id` filter |
| `tests/ZenVizor.Storage.Tests/AppHistoryQueryRepositoryTests` | Existing tests should pass unchanged (same public contract). Add a migration test verifying the backfill matches the JOIN-derived totals. |

The query simplifications are the easy bit. The migration backfill on a live
DB is the careful bit — must run inside one transaction to avoid leaving rows
with `app_id IS NULL` if the service is force-killed mid-migration.
