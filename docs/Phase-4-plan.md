# Phase 4 — Plan

**Status:** Open questions resolved 2026-06-02 — proceeding with implementation
**Last updated:** 2026-06-02
**Prerequisites:** Phase 3 complete (commit `82eb9f8`, CI green, all three manual gates passed)

## Resolved open questions (2026-06-02)

| # | Decision |
|---|---|
| Q1 | **Incremental UPSERT at flush.** Rollups land in the same transaction as `traffic_samples` writes; no separate scheduler, no rollup-mark state. |
| Q2 | **Acceptable.** `traffic_hourly`/`traffic_daily` drop `hosted_services`; per CLAUDE.md invariant #5 bytes stay with the PID, and high-res svchost-service breakdown is retained in `traffic_samples` for its 30-day window. |
| Q3 | **Auto-derive with optional override.** `≤6h → Samples`, `>6h && ≤30d → Hourly`, `>30d → Daily`. Caller can force any tier. |
| Q4 | **Once at startup + daily at a fixed local hour.** PeriodicTimer in the hosted service; Settings → Purge button (Phase 6) kicks the same job immediately. |
| Q5 | **UTC bucket alignment.** Storage in UTC unix-ms; UI renders local-time labels. |
| Q6 | **Same auto-rule as Q3** for App-detail history grain. |
| Q7 | **Defer filters to UI polish phase.** Phase 4 ships `GetAppList(window)` with no filter args. See `docs/phase-4-filter-recommendations.md` for lay-friendly filter design. |
| Q8 | **Server-aggregated** `GetConnections`. Aggregation only drops *temporal/session attribution*, not bytes. Spike-shape signal lives in `traffic_samples` (by app, time-bucketed) and is detected via Phase 6's alert pipeline. Per-endpoint time series would need a new `connection_samples` table — out of scope, flagged as post-MVP. |
| Q9 | **Window picker presets: Last 1h / 24h / 7d / 30d / 90d / Custom range.** "This calendar day"/"yesterday"/etc. can land in the UI polish phase. |

---

## 1. Cold-start context

### 1.1 Where we are now

Repo at commit `82eb9f8`. CI green; 161 headless tests pass:

- `ZenVizor.Core.Tests` (71)
- `ZenVizor.Storage.Tests` (24)
- `ZenVizor.Attribution.Tests` (48)
- `ZenVizor.Ipc.Tests` (13)
- `ZenVizor.Integration.Tests` (5)

Phase 3 delivered: in-memory `RollingActivityWindow`, IPC envelope, `GetCurrentActivitySnapshot` / `GetCaptureStats`, `zvctl snapshot` / `zvctl stats`, LiveCharts2 Dashboard, and attribution reliability via `ProcessLifecycleResolver` + `ConnectionLifecycleResolver`. Reliability gate `scripts\verify-attribution.ps1` shows 5/5 deterministic passes.

### 1.2 What's already in place that Phase 4 builds on

| Concern | Where it lives | Phase 4 hooks here? |
|---|---|---|
| Schema (`traffic_samples`, `traffic_hourly`, `traffic_daily`, `apps`, `process_sessions`, `connections`, `settings`) | `src/ZenVizor.Storage/Migrations/001_initial.sql` | **Yes** — hourly/daily tables exist but are empty; settings has retention defaults |
| Hot-path persistence | `SqliteFlushSink` | **No** — Phase 4 reads, not writes-hot-path |
| IPC contract surface | `src/ZenVizor.Ipc.Contracts/IZenVizorIpc.cs` | **Yes** — four new methods land here |
| IPC envelope versioning | `IpcEnvelope<T>` (Phase 3) | **Yes** — every Phase 4 method returns one |
| UI placeholders | `Views/PerAppPage.cs`, `HistoryPage.cs` (still `PlaceholderPage`) | **Yes** — replaced with real pages |
| `zvctl` CLI | `src/ZenVizor.Cli/Program.cs` | **Yes** — gains `apps`, `connections`, `history` subcommands |
| Connection cache (live, in-mem) | `ConnectionLifecycleResolver` (Phase 3) | **No** — historical queries hit SQLite |

### 1.3 What's intentionally NOT in scope here

- **Daily report payload + CSV/HTML export** — Phase 5
- **Alert pipeline / first alert** — Phase 6
- **Settings UI / autostart toggle / purge button** — Phase 6 (we'll *seed* and *consume* `retention.*` settings here, but the user-facing knob lands later)
- **WiX installer** — Phase 6
- **UI visual polish** — explicit interlude after Phase 4

---

## 2. Open questions (need answers before implementation)

The plan can't lock in cleanly without these. Recommended defaults are in **bold**; each has reasoning so you can override with confidence.

### Q1. Rollup trigger model — scheduled batch vs incremental at flush?

Two options for keeping `traffic_hourly` / `traffic_daily` populated:

- **A — Incremental at flush.** Every flush also `UPSERT`s into `traffic_hourly` and `traffic_daily` from the rows it's about to write to `traffic_samples`. Same transaction; same write tick.
- **B — Scheduled batch.** A background job (e.g., every 5 min) scans `traffic_samples` since the last rollup mark, rebuilds the affected hour/day rows, advances the mark.

**Recommend: A (incremental).** Simpler operationally (no extra scheduler, no "last rollup mark" state, no race with retention purges), and the per-flush cost is tiny: a handful of upserts per `(app_id, hour, remote_class)` and `(app_id, day, remote_class)`. The tradeoff is that backfill / re-derivation is harder if rollup math ever changes — but for the schemas defined in PRD §7.5 the math is `SUM(bytes_up), SUM(bytes_down)`, which doesn't change.

### Q2. Hourly/daily rollups drop `hosted_services` — confirm acceptable?

Schema for `traffic_hourly` / `traffic_daily` is keyed on `(app_id, bucket_start, remote_class)`. **`session_id` and `hosted_services` are NOT in the key.** So after 30 days (when the underlying `traffic_samples` and `process_sessions` rows are purged), historical svchost rows collapse into one bucket per `svchost.exe` regardless of which services that PID was hosting.

**Recommend: keep schema as-is.** Per CLAUDE.md invariant #5, byte totals stay with the PID; the *display* of hosted services is informational. Long-window history losing the service breakdown is acceptable — that data is in the `traffic_samples` tier for the 30-day high-res window. If we ever need long-term svchost-service breakdown we add a separate `app_service_attribution` rollup table later.

### Q3. Query grain — auto-derive from window, or caller-explicit?

`GetTrafficHistory(window, grain)`. Options:

- **A — Auto-derive.** Server picks: window ≤ 6h → samples (60s buckets); ≤ 30d → hourly; otherwise daily. Caller can override.
- **B — Required from caller.** UI/CLI must pass grain explicitly.

**Recommend: A with optional override.** UI default is auto; CLI defaults to auto; both can pass `--grain hourly` etc. Auto rules: `≤ 6h → Samples`, `> 6h && ≤ 30d → Hourly`, `> 30d → Daily`. The 6h boundary is chosen so the samples tier (default 60s buckets) doesn't blow up — 6h × 60 buckets/hour × (typical 50 apps) ≈ 18k rows per query, comfortable for SQLite.

### Q4. Retention purge cadence

PRD §7.9 sets retention windows (30/30/90/365 days). When does purge actually run?

Options:

- **A — Once on service start, then daily at a fixed local hour (e.g. 03:00).** Simple, predictable.
- **B — After every flush (cheap if rows are gone) or every N flushes.** Cheaper to amortize but harder to reason about.
- **C — On user demand only (Settings → Purge button).** Lazy, easy, but DB grows until user clicks.

**Recommend: A.** Purge job acquires a single connection, runs four `DELETE` statements with `WHERE` on `bucket_start`/`created_at` against the relevant indexes. On a 30-day samples table this is ~milliseconds. Schedule via a `PeriodicTimer` in the hosted service. The Settings → Purge button (Phase 6) just kicks the same job immediately.

### Q5. Bucket alignment — UTC vs local time?

Hour and day rollup buckets need a "start of hour" / "start of day" epoch. Options:

- **A — UTC.** Stable across DST and timezone changes; matches what `traffic_samples.bucket_start` already does (UTC unix-ms).
- **B — Local time.** Matches what users see; DST creates 23/25-hour days, requires re-keying buckets on TZ changes.

**Recommend: A (UTC).** Store UTC; render local in the UI (LiveCharts2 axis labels). Users querying "today" hit the service with explicit UTC range based on their local "today" — the UI computes the range. DST is then someone else's problem (the OS time zone DB) and we never have to migrate.

### Q6. App-detail history granularity — same auto-rule as Q3?

App detail view drills into one app and shows its time series. Same `(window, grain)` semantics as `GetTrafficHistory` but scoped to one `app_id`.

**Recommend: yes, same auto-rule.** One code path, one mental model.

### Q7. `GetAppList` filter dimensions — minimum viable?

PRD §9.1 says `GetAppList(window, filter)`. Filter candidates:

- `signature_status` (e.g., show only Unsigned)
- `is_user_writable_path` (boolean)
- `remote_class` (Wan only / Local only / both)
- `image_name LIKE` substring search

**Recommend: ship with NO filters in Phase 4 — just `window`.** Add filters in the UI polish interlude after Phase 4 once the design specifies the filter chrome. Keeps the IPC method tight; we can extend the request DTO additively without bumping `SchemaVersion`.

### Q8. `GetConnections` aggregation level

For an app with N sessions over the window, do we return:

- **A — Per-session connection rows.** N×M rows; UI can group client-side.
- **B — Server-aggregated.** Sum bytes across sessions per `(protocol, remote_addr, remote_port)`. Returns 1 row per endpoint regardless of session count.

**Recommend: B.** Matches the "drill app → its endpoints" mental model. Per-session detail is a rare debugging need — we can add `GetConnections(sessionId, ...)` later if needed (the PRD already allows `appId | sessionId` overloading).

### Q9. UI window picker presets

Recommend: **Last 1h, Last 24h, Last 7d, Last 30d, Last 90d, Custom range** (start/end date-time pickers). "This calendar day" / "yesterday" / "this week" can land in the UI polish phase. Phase 4 ships the rolling-window presets + Custom.

---

## 3. Sprint Plan Phase 4 reference

Restated from `docs/zenvizor-sprint-plan.md` Phase 4:

**Goal:** Rollups, retention, the full query/reporting IPC surface, and the per-app → connections → history navigation.

**Scope**

- Hourly/daily rollup jobs from `traffic_samples`; retention/purge jobs per PRD §7.9.
- Query IPC: `GetAppList`, `GetAppDetail`, `GetConnections`, `GetTrafficHistory` (grain selection).
- UI: Per-App breakdown, App detail (connections + history), History/timeline with **user-defined window**.

**CI gates (headless):**

- [ ] Rollup correctness: sample fixtures roll up to exact hourly/daily totals.
- [ ] Retention: rows older than configured windows are purged; newer retained; rollups preserved per policy.
- [ ] User-defined-window query over fixtures returns exact expected totals at each grain.

**Manual gates (real box):**

- [ ] Per-app list totals reconcile with the daily numbers and with the live view over the same window.
- [ ] Drill app → connections shows correct endpoints with local/WAN + protocol; drill → history series matches.
- [ ] Changing the query window updates results correctly; large windows stay responsive (served from rollups).

---

## 4. Proposed implementation plan

*(Reads cleanest top-to-bottom; section numbers map to commit-sized chunks.)*

### 4.1 Storage: rollup writes on the flush path

```
src/ZenVizor.Storage/Repositories/SqliteFlushSink.cs
  - Inside the existing flush transaction, after writing traffic_samples,
    issue UPSERTs into traffic_hourly + traffic_daily for every distinct
    (app_id, hour_bucket, remote_class) / (app_id, day_bucket, remote_class)
    touched by this flush.
  - Bucket conversion is pure: from a sample's bucket_start (UTC unix-ms)
    compute hour-aligned and day-aligned epochs via integer math.
  - Same transaction means rollups and samples can never diverge.
```

UPSERT SQL:

```sql
INSERT INTO traffic_hourly (app_id, bucket_start, remote_class, bytes_up, bytes_down)
VALUES (?, ?, ?, ?, ?)
ON CONFLICT(app_id, bucket_start, remote_class)
DO UPDATE SET bytes_up = bytes_up + excluded.bytes_up,
              bytes_down = bytes_down + excluded.bytes_down;
```

Requires unique constraint on `(app_id, bucket_start, remote_class)` for hourly/daily — **needs a migration** (current schema only has non-unique indexes).

### 4.2 Storage: migration 003

```
src/ZenVizor.Storage/Migrations/003_phase4_rollup_unique.sql
  - DROP INDEX ix_traffic_hourly_app_bucket (non-unique)
  - CREATE UNIQUE INDEX ux_traffic_hourly_app_bucket_class
        ON traffic_hourly (app_id, bucket_start, remote_class)
  - same for daily
```

### 4.3 Storage: read-side repositories

```
src/ZenVizor.Storage/Repositories/AppHistoryQueryRepository.cs (new)
  - GetAppList(fromMs, toMs)
        SELECT a.app_id, image_name, image_path, publisher, signature_status,
               is_user_writable_path, ...,
               COALESCE(SUM(s.bytes_up), 0) AS up,
               COALESCE(SUM(s.bytes_down), 0) AS down
        FROM apps a
        LEFT JOIN process_sessions ps ON ps.app_id = a.app_id
        LEFT JOIN <tier> s ON s.<key> = ps.session_id AND s.bucket_start BETWEEN ? AND ?
        GROUP BY a.app_id
        ORDER BY (up + down) DESC
    Tier auto-selected by window size (Q3).
    For svchost rows we don't aggregate hosted_services in long windows (Q2);
    the response carries hosted_services only when the underlying rows come
    from the Samples tier (we can join through process_sessions). On hourly/
    daily tiers it's null.

  - GetAppDetail(appId, fromMs, toMs)
        Series at chosen grain + summary totals + recent sessions.

  - GetConnections(appId, fromMs, toMs)
        Aggregated by (protocol, remote_addr, remote_port) across sessions in window.

  - GetTrafficHistory(fromMs, toMs, grain)
        Series across all apps at chosen grain; optionally per-app stacked.
```

### 4.4 Storage: retention purge

```
src/ZenVizor.Storage/Repositories/RetentionRepository.cs (new)
  - PurgeOlderThan(...) — DELETE from each tier based on settings
    Reads retention.* keys at start of run; one DELETE per table:
        DELETE FROM traffic_samples  WHERE bucket_start < ?
        DELETE FROM connections      WHERE last_seen   < ?
        DELETE FROM traffic_hourly   WHERE bucket_start < ?
        DELETE FROM traffic_daily    WHERE bucket_start < ?
        DELETE FROM alerts           WHERE acknowledged_at IS NOT NULL
                                       AND acknowledged_at < ?
    Plus orphan cleanup: DELETE FROM process_sessions WHERE end_time < ?
                          AND NOT EXISTS (SELECT 1 FROM traffic_samples ...)
```

```
src/ZenVizor.Service/ZenVizorHostedService.cs
  - PeriodicTimer at 1h intervals invoking RetentionRepository.PurgeOlderThan(...)
  - Initial purge runs once at startup (after migrations).
```

### 4.5 IPC contracts

```
src/ZenVizor.Ipc.Contracts/IZenVizorIpc.cs

  Task<IpcEnvelope<AppListResult>>        GetAppListAsync(QueryWindow window);
  Task<IpcEnvelope<AppDetailResult>>      GetAppDetailAsync(int appId, QueryWindow window);
  Task<IpcEnvelope<ConnectionListResult>> GetConnectionsAsync(int appId, QueryWindow window);
  Task<IpcEnvelope<TrafficHistoryResult>> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain? grain);

src/ZenVizor.Ipc.Contracts/Dto/QueryWindow.cs
  record QueryWindow(long FromUnixMs, long ToUnixMs);

src/ZenVizor.Ipc.Contracts/Dto/TrafficGrain.cs
  enum TrafficGrain { Auto, Samples, Hourly, Daily }

src/ZenVizor.Ipc.Contracts/Dto/AppListResult.cs
  record AppListResult(QueryWindow Window, IReadOnlyList<AppListEntry> Apps);
  record AppListEntry(int AppId, string ImageName, string ImagePath, string? Publisher,
                      string SignatureStatus, bool IsUserWritablePath,
                      long BytesUp, long BytesDown, long FirstSeenUnixMs, long LastSeenUnixMs);

src/ZenVizor.Ipc.Contracts/Dto/AppDetailResult.cs
  record AppDetailResult(QueryWindow Window, AppListEntry Summary,
                         TrafficGrain GrainUsed,
                         IReadOnlyList<TrafficPoint> Series,
                         IReadOnlyList<SessionInfo> RecentSessions);
  record TrafficPoint(long BucketStartUnixMs, string RemoteClass, long BytesUp, long BytesDown);
  record SessionInfo(long SessionId, int Pid, long StartTimeUnixMs, long? EndTimeUnixMs, string? HostedServices);

src/ZenVizor.Ipc.Contracts/Dto/ConnectionListResult.cs
  record ConnectionListResult(QueryWindow Window, IReadOnlyList<ConnectionRow> Connections);
  record ConnectionRow(string Protocol, string RemoteAddress, int RemotePort, string RemoteClass,
                       long BytesUp, long BytesDown, long FirstSeenUnixMs, long LastSeenUnixMs);

src/ZenVizor.Ipc.Contracts/Dto/TrafficHistoryResult.cs
  record TrafficHistoryResult(QueryWindow Window, TrafficGrain GrainUsed,
                              IReadOnlyList<TrafficPoint> Series);
```

Schema versions: all start at `1`.

### 4.6 Service wiring

```
src/ZenVizor.Service/ZenVizorIpcHandler.cs
  - Add four new methods, each calling a Func provider (mirrors the snapshotProvider pattern)

src/ZenVizor.Service/ZenVizorHostedService.cs
  - Construct AppHistoryQueryRepository(connections)
  - Pass repo methods as Func<...> to the handler
  - Add retention timer
```

### 4.7 CLI

```
src/ZenVizor.Cli/Program.cs

  zvctl apps [--window 24h] [--top N] [--json]
      Aligned-column list: image_name, publisher, sig, bytes_up, bytes_down

  zvctl app <appId> [--window 24h] [--json]
      Summary + recent sessions + sparkline (text-mode)

  zvctl connections <appId> [--window 24h] [--top N] [--json]

  zvctl history [--window 24h] [--grain auto|samples|hourly|daily] [--json]
```

Window parser supports `1h`, `24h`, `7d`, `30d`, `90d`, `1y`, `from=...,to=...`.

### 4.8 UI

```
src/ZenVizor.Ui/Views/PerAppPage.xaml(.cs)     ← replaces placeholder
  - Top: window picker (Q9 presets + Custom)
  - Body: data-grid of apps with sortable columns; row click → AppDetailPage
  - Default sort: total bytes desc

src/ZenVizor.Ui/Views/AppDetailPage.xaml(.cs)  ← new
  - Header: app identity card (name, publisher, signature pill)
  - Time series chart (LiveCharts2) at auto-grain
  - Tabs / sections:
      - Connections (data-grid)
      - Recent sessions (data-grid)

src/ZenVizor.Ui/Views/HistoryPage.xaml(.cs)    ← replaces placeholder
  - Window picker + grain selector (auto/samples/hourly/daily)
  - Aggregate time-series chart, all apps combined

src/ZenVizor.Ui/Services/HistoryQueryClient.cs (new)
  - Thin wrapper over the IPC proxy; one method per query.
  - Caches recent results briefly so re-clicking the same window is instant.
```

Navigation: MainWindow's `RootNavigation` already has entries for Per-App / History; route them to the real pages instead of the placeholder. Adding a path from PerApp → AppDetail uses `RootNavigation.Navigate(typeof(AppDetailPage), appId)` with a navigation context.

### 4.9 Tests

| Project | New tests |
|---|---|
| `ZenVizor.Storage.Tests` | Rollup UPSERT correctness (multiple flushes into same hour/day yield summed bytes); rollup unique constraint enforced; retention purge correctness (rows older deleted, newer retained, rollups preserved). |
| `ZenVizor.Storage.Tests` | `AppHistoryQueryRepository` happy-path: deterministic fixtures populate samples/hourly/daily; `GetAppList`, `GetAppDetail`, `GetConnections`, `GetTrafficHistory` return exact expected rows at each tier. |
| `ZenVizor.Storage.Tests` | Grain auto-selection boundary tests (`window=6h` → Samples; `window=6h+1ms` → Hourly; etc.). |
| `ZenVizor.Ipc.Tests` | Contract round-trip for each new method; envelope `SchemaVersion=1` preserved. |
| `ZenVizor.Integration.Tests` | End-to-end: synthetic captures → flush → query roundtrips return the expected aggregated results. |

### 4.10 Order of execution

1. Migration 003 (unique index for rollup UPSERT).
2. `SqliteFlushSink` rollup UPSERTs + storage tests.
3. `RetentionRepository` + tests + service timer.
4. IPC contracts (DTOs + interface method additions).
5. `AppHistoryQueryRepository` + storage tests at each tier + grain auto-rule.
6. Service handler wiring + IPC contract tests.
7. CLI subcommands (so QA can use CLI before UI lands).
8. UI: `PerAppPage` → `AppDetailPage` → `HistoryPage`.
9. Build, test, walk gates, write `docs/phase-4-verification.md`.

---

## 5. Pre-flight tool dependencies

Per CLAUDE.md, surface BEFORE any validation steps.

**Phase 4 adds no new external tool dependencies.** Same set as Phase 3:

- `sqlite3.exe`
- `dotnet` SDK
- Built-in Windows: `sc.exe`, `Get-Counter`, `Get-Process`

---

## 6. Test strategy

### 6.1 Headless (CI on windows-latest, no admin / no live ETW)

| Concern | How it's covered |
|---|---|
| Rollup correctness | Storage tests: scripted observation sets through SqliteFlushSink → assert exact rows in `traffic_samples` AND `traffic_hourly` AND `traffic_daily`. |
| Rollup atomicity | Storage test: induced sink failure leaves no orphan rollup rows. |
| Retention purge | Storage test: rows at boundary +/- 1 second; assert correct ones deleted. |
| Grain auto-selection | Pure-function tests on the window→grain rule. |
| Query correctness | `AppHistoryQueryRepository` tests against a temp SQLite DB seeded with deterministic fixtures. Exact-byte assertions, not approximations. |
| IPC envelope | Contract round-trips via `FakeIpcHandler` (matches Phase 3 pattern). |

### 6.2 Manual gates (real box)

- Per-app list totals reconcile with daily numbers AND with live view (see `phase-4-verification.md`).
- Drill app → connections shows correct endpoints; drill → history series matches the live data observed during capture.
- Large-window queries (e.g., 90 days) return in &lt;500ms (served from daily rollup tier).

---

## 7. Manual gate prep — `docs/phase-4-verification.md`

Three gates, drafted at gate-walk time.

### Gate #1 — Per-app reconciliation

Run `zvctl snapshot` for live; `zvctl apps --window 1h` for history; verify the top app's byte total over the last hour is consistent (allowing for the live window's partial-flush state). Cross-check with sqlite3 query.

### Gate #2 — Drill-down navigation

In the UI, Per-App page → click app → AppDetail loads with chart + connections + sessions; numbers match what `zvctl connections <id>` and `zvctl history` show.

### Gate #3 — Large-window query responsiveness

Generate 30+ days of synthetic data via a seeding script; query `zvctl history --window 90d`. Should return in under 500ms (daily tier hit). Same query at `--grain samples` should be noticeably slower (samples tier scan).

---

## 8. Architectural guardrails

These remain in effect from prior phases:

- **Invariant #1 — Zero outbound traffic.** Phase 4 only reads SQLite and serves IPC; no library that does network calls.
- **Invariant #3 — UI has NO database access.** All Phase 4 queries flow over IPC.
- **Invariant #4 — No per-event DB writes.** Phase 4 rollup UPSERTs run inside the EXISTING flush transaction; no new write tick.
- **NEW (Phase 4) — Rollups MUST be atomic with samples.** Same transaction; never let `traffic_samples` and `traffic_hourly` diverge.
- **NEW (Phase 4) — Retention purge MUST NOT touch the hot path.** Runs on a dedicated timer thread, its own connection. Acceptable to skip a purge tick if the connection is busy (next tick gets it).

---

## 9. Definition of done

All of the following pass:

- [ ] Open questions §2 answered (recorded in this doc).
- [ ] Migration 003 ships with unique constraints; existing DBs migrate cleanly.
- [ ] CI green: rollup correctness, retention purge, grain selection, query correctness, IPC contract round-trips.
- [ ] `zvctl apps` / `zvctl app <id>` / `zvctl connections <id>` / `zvctl history` work against the live service.
- [ ] UI Per-App, AppDetail, History pages replace placeholders with live data; window picker functional.
- [ ] Manual gates §7 walked.
- [ ] Phase 4 boxes in sprint plan checked off.
- [ ] Commit pushed, CI green on `windows-latest`.

---

## 10. Reference snippets

### Bucket alignment helper

```csharp
// In ZenVizor.Core.Aggregation.BucketAligner (or alongside):
public static long AlignToHour(long unixMs) =>
    unixMs - (unixMs % 3_600_000);
public static long AlignToDay(long unixMs) =>
    unixMs - (unixMs % 86_400_000);
```

### Auto-grain rule (Q3)

```csharp
public static TrafficGrain Resolve(QueryWindow w, TrafficGrain? requested)
{
    if (requested is not null and not TrafficGrain.Auto) return requested.Value;
    var spanMs = w.ToUnixMs - w.FromUnixMs;
    if (spanMs <= 6 * 3_600_000)       return TrafficGrain.Samples;
    if (spanMs <= 30L * 86_400_000)    return TrafficGrain.Hourly;
    return TrafficGrain.Daily;
}
```
