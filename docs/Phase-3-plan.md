# Phase 3 — Plan

**Status:** Open questions resolved 2026-06-01 — proceeding with implementation
**Last updated:** 2026-06-01
**Prerequisites:** Phase 2 complete (commit `5e2e933`, CI green) + project renamed `TitaniRun → ZenVizor` (commit `cca5e3e`, PR #1)

## Resolved open questions (2026-06-01)

All eight Q1–Q8 answered at the proposed defaults, with **Q2 and Q6 revised** after the user pushed back on a 30-second smoothing window being too laggy for a "live" view.

| # | Decision |
|---|---|
| Q1 | **Envelope on new methods only.** New Phase-3+ methods return `IpcEnvelope<T>` with a per-payload `SchemaVersion`; existing Phase-0 methods (`NegotiateVersionAsync`, `PingAsync`, `GetServiceStatusAsync`) stay bare to avoid breaking compat with the version negotiation we just shipped. |
| Q2 | **Snapshot source = previous completed flush bucket + current partial accumulator.** That's a sliding **5–10 s** window that always covers at least the last 5 s. Pure in-memory; no SQLite touch. NOT a 30 s ring (rev. from the original proposal). |
| Q3 | **Per-app rollup happens at flush time** inside `TrafficAggregator.Flush`, under the existing lock — we already have the `SessionTracker` state there, so `pid → AppIdentity` mapping is free. Snapshot becomes a pure ring-copy. |
| Q4 | **Defer `ActivityTick` push notifications.** Scope says "optional"; 2 s polling hits the gates cheaply. Pull-only for Phase 3. |
| Q5 | **UI polls every 2 s.** Faster than the Phase-0 status poller (5 s). Configurable via settings later. |
| Q6 | **Server returns `BytesUpPerSec` + `BytesDownPerSec` + `WindowSeconds` per app.** UI displays the rate directly and appends to its own local LiveCharts2 series for trailing history (rev. from the original proposal — chart history is the UI's job, not a server payload). |
| Q7 | **Server returns ALL apps with non-zero bytes in the window**, client (UI/CLI) takes top-N for display. Cheap; lets `zvctl snapshot --all` show the full set. |
| Q8 | **Do NOT self-filter ZenVizor's own PIDs.** Founding invariant is verified by the tool NOT appearing in its own data — filtering would mask a regression. If we ever appear, that's a bug we need to see. |

> **For a new Claude Code session picking this up cold:** start by reading this
> file top-to-bottom, then `docs/zenvizor-prd.md` §9 (IPC contract) and §11 (UI
> info architecture). The Q1–Q8 decisions are settled — do not re-litigate
> without surfacing to the user.

---

## 1. Cold-start context

### 1.1 Where we are now

The repo at commit `cca5e3e` has Phase 0–2 shipped plus the full `TitaniRun → ZenVizor` rename. CI is green on windows-latest. 127 headless tests pass:

- `ZenVizor.Core.Tests` (58)
- `ZenVizor.Storage.Tests` (24)
- `ZenVizor.Attribution.Tests` (30)
- `ZenVizor.Ipc.Tests` (11) — Phase 0 IPC contract: version negotiation + in-process round-trip
- `ZenVizor.Integration.Tests` (4) — synthetic capture → real SQLite, includes the architectural guard *"Observe() must not write to disk"*

The dev service runs as `ZenVizor` (Windows Service name), DB at `C:\ProgramData\ZenVizor\zenvizor.db`, IPC pipe `\\.\pipe\ZenVizor.Ipc.v1`. `zvctl status` round-trips clean.

### 1.2 What's already in place that Phase 3 builds on

| Concern | Where it lives | Phase 3 hooks here? |
|---|---|---|
| IPC interface | `src/ZenVizor.Ipc.Contracts/IZenVizorIpc.cs` | **Yes** — add `GetCurrentActivitySnapshotAsync` |
| IPC server-side handler | `src/ZenVizor.Service/ZenVizorIpcHandler.cs` | **Yes** — needs a snapshot provider `Func<ActivitySnapshot>` |
| IPC pipe server | `src/ZenVizor.Ipc.Server/ZenVizorPipeServer.cs` | **No** — no change |
| IPC client + pipe | `src/ZenVizor.Ipc.Client/ZenVizorPipeClient.cs` | **No** — proxy is generated from the interface |
| Aggregator | `src/ZenVizor.Core/Aggregation/TrafficAggregator.cs` | **Yes** — maintains the per-app rolling window; new `TakeActivitySnapshot()` method |
| Session tracker | `src/ZenVizor.Core/Aggregation/SessionTracker.cs` | **No** — already exposes `SnapshotPersistedSessions` which Phase 3 reads under the lock |
| CLI | `src/ZenVizor.Cli/Program.cs` | **Yes** — `snapshot` command |
| UI host | `src/ZenVizor.Ui/MainWindow.xaml(.cs)` + `Views/DashboardPage.cs` | **Yes** — replace `DashboardPage` placeholder with a real page |
| UI service-status poller | `src/ZenVizor.Ui/Services/ServiceStatusPoller.cs` | **Sibling pattern reused** — add `ActivitySnapshotPoller` |
| Schema | `src/ZenVizor.Storage/Migrations/001_initial.sql` | **No schema changes** — snapshot path is pure in-memory |

### 1.3 What's intentionally NOT in scope here

- **History/rollup query surface** (`GetAppList`, `GetAppDetail`, `GetConnections`, `GetTrafficHistory`) — Phase 4
- **Daily report + CSV/HTML export** — Phase 5
- **Alert pipeline / first alert** — Phase 6
- **Settings UI / autostart / retention tuning** — Phase 6
- **WiX installer** — Phase 6 (gets `zvctl` on PATH, fixes the dev-time full-path nuisance)
- **`ActivityTick` push notifications** — deferred per Q4
- **Tray balloon / system notifications** — Phase 6 (alerts)

---

## 2. Open Questions — answered

Restated here in full so the trade-offs we accepted are recoverable from this doc alone.

### Q1. Envelope shape — wrap every result, or only new methods?

**Decision:** new methods only. New Phase-3+ methods return `IpcEnvelope<T> { int SchemaVersion; T Payload; }`. Existing methods (`NegotiateVersionAsync`, `PingAsync`, `GetServiceStatusAsync`) stay bare.

**Why:** wrapping the existing methods would break compatibility with the version-negotiation handshake we just shipped — and the negotiation handshake exists exactly so we don't need a per-payload envelope on every method. The envelope's value is on payloads whose schema is likely to evolve (snapshot, future queries), not on liveness probes.

### Q2. Snapshot window — what time range backs each `BytesPerSecond` value?

**Decision (revised):** the previous completed flush bucket + the current partial accumulator. That's a sliding **5–10 s** window that always covers at least the last 5 s of activity. Pure in-memory read, no SQLite touch.

**Why:** the original proposal of a 30 s ring conflated *freshness* (how stale the newest data is) with *smoothing window* (how many seconds the rate is averaged over). 30 s of smoothing on a "live" view flattens short spikes to ~17 % of their real magnitude — too much for the dashboard's job. A 5–10 s window is short enough to surface real traffic spikes but long enough that the rate doesn't dip to zero immediately after every flush tick (the partial accumulator alone would, since flush swaps it for a fresh empty dict).

### Q3. Per-app grouping — at flush time, or at snapshot time?

**Decision:** at flush time. `TrafficAggregator.Flush` already holds the lock and has the `SessionTracker` state — building a per-app rollup there costs an extra dictionary pass over the already-collected sample rows. The snapshot endpoint then becomes a near-zero-cost ring copy under the lock.

**Why:** the alternative (walk `pid → session → app` at snapshot time) would acquire the lock a second time and do O(active sessions × buckets) work on every poll. Doing it once per flush is O(samples) and amortizes naturally.

### Q4. Push notifications (`ActivityTick`) — in Phase 3 or later?

**Decision:** defer. Polling at 2 s from the UI satisfies the manual gate ("dashboard reflects with only minor delay") without adding StreamJsonRpc notification plumbing now.

**Why:** smaller blast radius for Phase 3. Push is a clean follow-on if 2 s polling ever feels laggy in real use; the contract already allows it per PRD §9.2.

### Q5. UI polling cadence

**Decision:** 2 s.

**Why:** the existing `ServiceStatusPoller` runs at 5 s for connection state; that's too slow for activity. 2 s gives ~30 chart points per minute — comfortable update cadence for LiveCharts2 — with negligible CPU cost (one pipe round-trip + small JSON payload).

### Q6. Snapshot payload — raw bucket totals or server-computed rates?

**Decision (revised):** server returns per-app rates directly. Each `AppActivity` record carries `BytesUpPerSec`, `BytesDownPerSec`, `WindowSeconds`, and totals over the window. The UI appends each polled point to its own local LiveCharts2 series (trimmed to e.g. last 60–120 points = 2–4 min of trailing chart history).

**Why:** one source of truth for the rate calculation. `zvctl snapshot` and the UI display agree without each having to redo the same `bytes / window_seconds` math. The UI owns chart history because it's a display concern — sending history bytes over the pipe on every poll would waste IPC bandwidth.

### Q7. Top-talkers cutoff

**Decision:** server returns all apps with non-zero bytes in the window; client takes top-N for display.

**Why:** snapshots are typically small (tens of apps with traffic in a 10 s window), so sending all is cheap. Lets `zvctl snapshot --all` show the full set when investigating, while the UI defaults to top-10 for readable charts.

### Q8. Self-filter our own PIDs?

**Decision:** no.

**Why:** PRD §1.1 founding invariant ("the application generates no network traffic of its own") is verified by the tool *not appearing in its own data*. Filtering out `ZenVizor.Service.exe` / `ZenVizor.Ui.exe` / `zvctl.exe` PIDs would silently mask a regression that broke the invariant. If ZenVizor processes ever show up in the snapshot, that's a bug we need to see — not hide.

---

## 3. Sprint Plan Phase 3 reference

Restated from `docs/zenvizor-sprint-plan.md` lines 118–139:

**Goal:** The UI shows a near-live dashboard fed entirely over IPC from the in-memory aggregate; the versioned envelope seam is in place.

**Scope:**

- Seam #3: versioned IPC envelope finalized.
- `GetCurrentActivitySnapshot()` served from in-memory aggregate; optional `ActivityTick` push.
- Dashboard / Current Activity view with LiveCharts2 (per-app up/down rates, top talkers).
- `zvctl` gains `snapshot` command.

**CI gates (headless):**

- [ ] Contract tests: snapshot request/response and envelope versioning round-trip in-process.
- [ ] Snapshot is served from the in-memory aggregate (test asserts no SQLite read on the snapshot path).

**Manual gates (real box):**

- [ ] Generate live traffic; the dashboard reflects it with only minor delay/aggregation; rates match reality within tolerance.
- [ ] **Self-monitoring check:** with the tool running and the UI open, the tool reports **zero outbound** from its own service/UI processes (named-pipe IPC produces no network rows). *This is the founding-invariant gate.*
- [ ] `zvctl snapshot` returns the same data the UI shows.

---

## 4. Proposed implementation plan

### 4.1 New contract surface (`ZenVizor.Ipc.Contracts`)

```
src/ZenVizor.Ipc.Contracts/
  IpcEnvelope.cs                    — record IpcEnvelope<T>(int SchemaVersion, T Payload)
                                       SchemaVersion starts at 1 for every new payload type.
  Dto/ActivitySnapshot.cs           — top-level snapshot: capture time + per-app entries +
                                       the WindowSeconds covered.
  Dto/AppActivity.cs                — per-app row: app identity bits (name/path/publisher/
                                       sig status/is_user_writable_path/hosted_services),
                                       BytesUpPerSec, BytesDownPerSec, BytesUpTotal,
                                       BytesDownTotal over the window.
  IZenVizorIpc.cs                   — new method:
                                       Task<IpcEnvelope<ActivitySnapshot>>
                                           GetCurrentActivitySnapshotAsync();
```

**Schema version policy:** the envelope's `SchemaVersion` is a *per-payload-type* discriminator. Adding optional fields to `ActivitySnapshot` keeps version 1; removing or renaming a field bumps to version 2 and the server may serve both for a deprecation window. Phase 3 ships v1 only.

### 4.2 Core changes (`ZenVizor.Core`)

```
src/ZenVizor.Core/Aggregation/
  RollingActivityWindow.cs          — new. Holds:
                                       (a) the most-recently-completed flush bucket's
                                           per-app totals (frozen at flush time)
                                       (b) the flush tick's wall-clock timestamp so the
                                           snapshot can compute elapsed-since-last-flush
                                     Methods:
                                       OnFlush(perAppRollup, flushTimeUnixMs)
                                       TakeSnapshot(currentPartialPerApp,
                                                    nowUnixMs) -> ActivitySnapshot
  TrafficAggregator.cs              — Flush() additionally:
                                       1. Builds a per-app dictionary (AppIdentity →
                                          BytesUp/Down) from the sample rows + the
                                          SessionTracker's pid→app map (under existing lock)
                                       2. Calls _activityWindow.OnFlush(...) with that map
                                          and the flush timestamp
                                     New method:
                                       TakeActivitySnapshot() -> ActivitySnapshot
                                       — also under the existing lock; combines the frozen
                                         last-bucket with whatever's in the partial
                                         accumulator right now, converts to AppActivity
                                         records with rates computed server-side.
```

**Key invariant:** `TakeActivitySnapshot()` does NOT touch the `IFlushSink` or any DB-bound code path. Pure in-memory composition.

### 4.3 Service wiring (`ZenVizor.Service`)

```
src/ZenVizor.Service/ZenVizorIpcHandler.cs
  - Constructor gains: Func<ActivitySnapshot> snapshotProvider
  - GetCurrentActivitySnapshotAsync() returns
    new IpcEnvelope<ActivitySnapshot>(SchemaVersion: 1, Payload: _snapshotProvider())

src/ZenVizor.Service/ZenVizorHostedService.cs
  - Captures `aggregator` reference at startup
  - Passes () => aggregator.TakeActivitySnapshot() into ZenVizorIpcHandler
```

### 4.4 CLI (`ZenVizor.Cli`)

```
src/ZenVizor.Cli/Program.cs
  - New `snapshot` subcommand:
      zvctl snapshot                — prints top 10 apps by total bytes in the window
      zvctl snapshot --all          — prints all apps with non-zero bytes
      zvctl snapshot --json         — emits raw IpcEnvelope<ActivitySnapshot> as JSON
                                       (for QA scripts / future automation)
  - Default formatter: aligned columns —
      App                    Pub                Sig         Up/s         Dn/s    Win
      chrome.exe             Google LLC         Signed     12.3 KB/s  450.0 KB/s  10s
      svchost.exe [Dnscache] Microsoft Corp.    Signed      0.2 KB/s    0.1 KB/s  10s
      ...
```

### 4.5 UI (`ZenVizor.Ui`)

```
src/ZenVizor.Ui/
  ZenVizor.Ui.csproj
    + NuGet: LiveChartsCore.SkiaSharpView.WPF (pin version)

  Services/ActivitySnapshotPoller.cs
    - Sibling of ServiceStatusPoller. 2 s cadence. Raises SnapshotReceived event
      with the deserialized ActivitySnapshot.
    - On IPC failure, raises SnapshotFailed with the exception type/name; the
      DashboardPage shows a transient "service disconnected" banner without
      blanking the chart.

  Views/DashboardPage.xaml + .xaml.cs           ← REPLACES the placeholder
    - Top-N talkers list (default N=10): app name + publisher chip + signature
      status pill + current Up/Dn rate. Click row → (Phase 4 hook, no-op for now).
    - LiveCharts2 cartesian chart, stacked area: aggregate Up rate + Dn rate over
      the trailing ~2 min of polls. Series owns ~60 points; trimmed FIFO.
    - Updates driven by ActivitySnapshotPoller.SnapshotReceived on the UI thread
      via Dispatcher.
```

**WPF-UI Fluent conformance:** the chart uses LiveCharts2's `SKDefaultColors` for series fills; row chrome uses `Wpf.Ui.Controls.CardAction` for consistent hover/focus states. No custom styling — leverage what's already pulled in.

### 4.6 Tests

| Project | New tests |
|---|---|
| `ZenVizor.Ipc.Tests` | `IpcEnvelope_VersionRoundTripsThroughJsonRpc` (envelope's `SchemaVersion` survives serialization). `GetCurrentActivitySnapshot_RoundTripsThroughEnvelope` using a `FakeIpcHandler` that returns a scripted snapshot. |
| `ZenVizor.Core.Tests` | `RollingActivityWindowTests` — deterministic OnFlush + TakeSnapshot from a scripted sequence of (flush bucket, partial accumulator) pairs; asserts exact `AppActivity` rows and rates. `TrafficAggregatorTests.TakeActivitySnapshot_ProducesPerAppRates` — end-to-end through the aggregator with synthetic observations. |
| `ZenVizor.Integration.Tests` | `Snapshot_DoesNotReadSqlite` — wraps the `ConnectionFactory` to fail if `OpenAsync` is called during a snapshot. This is the CI half of the "no SQLite read on snapshot path" gate, mirroring the existing Phase 1 "Observe() must not write to disk" guard. |

### 4.7 Order of execution

1. `IpcEnvelope<T>` + `ActivitySnapshot` / `AppActivity` DTOs in `Ipc.Contracts`. New method on `IZenVizorIpc`.
2. `RollingActivityWindow` + unit tests in `Core.Tests` (purely in-memory, no Service deps).
3. Hook `TrafficAggregator.Flush` to feed the window; add `TakeActivitySnapshot()`. Update aggregator tests.
4. Wire snapshot provider into `ZenVizorIpcHandler` + `ZenVizorHostedService`.
5. Contract test in `Ipc.Tests`. Integration "no SQLite read" guard in `Integration.Tests`.
6. `zvctl snapshot` subcommand (CLI is the easiest sanity check before UI).
7. Add LiveCharts2 NuGet ref to `ZenVizor.Ui.csproj`.
8. `ActivitySnapshotPoller` + replace `DashboardPage` placeholder with real chart + list.
9. Manual smoke on real box: build, reinstall service, run UI, run `zvctl snapshot`, compare.
10. Write `docs/phase-3-verification.md` with the three gates.
11. Walk the gates, check the boxes in `docs/zenvizor-sprint-plan.md`, commit, push.

---

## 5. Pre-flight tool dependencies

Per CLAUDE.md standing behavior — surface BEFORE any validation steps.

**Phase 3 adds no new external tool dependencies.** Everything needed is already in place from Phases 1–2:

- `sqlite3.exe` — read-only DB queries (validation gate #2 below)
- `dotnet` SDK — build + test
- Built-in Windows: `sc.exe`, `Get-Counter`, `Get-CimInstance`, `Get-Process`

**Reminder about `zvctl` on PATH:** the Phase 6 WiX installer is what puts `zvctl.exe` on `PATH`. Until then, manual gates use the full build-output path: `.\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe`. The verification doc bakes this in.

---

## 6. Test strategy

### 6.1 Headless (CI must run on windows-latest with no admin/elevation needs)

| Concern | How it's covered |
|---|---|
| Envelope versioning | In-process JsonRpc round-trip preserves `SchemaVersion`. |
| Snapshot determinism | Scripted observation sequences → exact expected `AppActivity` rows + rates. |
| "No SQLite on snapshot path" | Integration test wraps `ConnectionFactory` to throw on any `OpenAsync` call during a snapshot call. Architectural guard, mirrors the Phase-1 "Observe must not write to disk" pattern. |
| Per-app grouping correctness | Multi-PID, multi-app observation streams yield correctly-summed per-app rows. |
| Rate math | `BytesPerSec = WindowBytes / WindowSeconds` with the elapsed-since-last-flush exact to within ms tolerance. |
| Top-talkers ordering | Server returns unsorted (client-side concern); CLI test asserts top-N sort + tie-breaking by app name. |

### 6.2 Not on CI (manual gates on real box)

- Live dashboard responsiveness vs. real traffic (`curl -o NUL https://speed.cloudflare.com/__down?bytes=50000000`).
- Self-monitoring zero-own-traffic check.
- LiveCharts2 visual polish (rendering, color contrast, DPI handling).

---

## 7. Manual gate prep — `docs/phase-3-verification.md`

To be drafted at gate-walk time, following the Phase 2 verification doc's structure. The three gates:

### Gate #1 — Dashboard vs. real traffic

1. Build + reinstall service: `.\scripts\install-dev.ps1` (elevated).
2. Launch UI: `dotnet run --project src\ZenVizor.Ui -c Release` (non-elevated).
3. Generate known traffic: `curl.exe -o NUL https://speed.cloudflare.com/__down?bytes=50000000`.
4. Within ~5 s, the dashboard's stacked-area chart shows a visible Dn spike. Top-talkers list shows `curl.exe` at the top with a non-trivial Dn/s rate.
5. At the moment of the spike, run `.\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe snapshot` — top app + rate matches what the dashboard renders (within one poll cycle).

**Pass criteria:** curl is visible within 5 s; rate is in the right order of magnitude (tens of MB/s for a 50 MB download); UI and CLI agree.

### Gate #2 — Self-monitoring (founding invariant)

Let the service + UI run idle for **60 s**, then (from an **elevated** PowerShell — DB is ACL'd to SYSTEM+Administrators):

```
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
sqlite3.exe -readonly -header -column $db "SELECT a.image_name, SUM(s.bytes_up+s.bytes_down) AS bytes FROM apps a JOIN process_sessions ps USING(app_id) JOIN traffic_samples s USING(session_id) WHERE a.image_name IN ('ZenVizor.Service.exe','ZenVizor.Ui.exe','zvctl.exe') GROUP BY a.image_name;"
```

**Pass criteria:** zero rows returned, OR zero bytes for every row. Also cross-check `zvctl snapshot --all` does not list any `ZenVizor.*` or `zvctl.exe` row. If either of these lights up, **stop** and investigate before advancing.

**Single-line `sqlite3.exe` form is mandatory** — per the CLAUDE.md terminal gotchas, multi-line PowerShell here-strings (`@'…'@`) frequently break on paste into interactive PS. Use `-readonly -header -column` and pass the SQL as a double-quoted argument.

### Gate #3 — No SQLite read on snapshot path (spot-check)

Mostly a CI gate, but a real-box spot-check is cheap:

```
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
$before = (Get-Item $db).LastWriteTime
1..50 | ForEach-Object { & .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe snapshot --json | Out-Null }
$after = (Get-Item $db).LastWriteTime
"DB mtime delta: $($after - $before)"
```

**Pass criteria:** delta is zero or near-zero. Some non-zero delta is acceptable if a flush tick happens to fall during the 50-call loop; what we're confirming is the *snapshot* doesn't itself open the DB. The CI test is the canonical evidence.

---

## 8. Architectural guardrails (do NOT violate)

These are CLAUDE.md invariants Phase 3 must respect:

- **Invariant #1 — Zero outbound network from our processes.** Polling/push must use the named pipe; under no circumstances introduce loopback TCP, REST, or any DNS-resolving client library. LiveCharts2 is a pure local renderer (no network).
- **Invariant #2 — IPC is named pipes only.** Snapshot is served over the existing pipe; no new endpoint mechanism.
- **Invariant #3 — UI has NO database access.** Snapshot is served from in-memory; UI never opens `zenvizor.db`.
- **Invariant #4 — No per-event DB writes.** Snapshot path is in-memory-only; existing flush rules unchanged.
- **NEW (Phase 3) — Snapshot path must NOT read SQLite either.** Same spirit as invariant #4 (no synchronous DB I/O on hot/freq-called paths). Enforced by the integration test in §6.1.

---

## 9. Definition of done

All of the following pass:

- [ ] Open questions §2 answered (answers committed alongside this plan).
- [ ] CI green: 6 test projects, all passing, no skipped tests. New tests: envelope round-trip, snapshot determinism, "no SQLite on snapshot path" guard.
- [ ] `zvctl snapshot` works against the running service and produces sane output (top-N + `--all` + `--json`).
- [ ] UI dashboard replaces the placeholder; LiveCharts2 area chart and top-talkers list update on the 2 s poll.
- [ ] Manual gates §7 walked by user on a real box.
- [ ] `docs/phase-3-verification.md` exists with the three gates.
- [ ] **Self-monitoring gate passes — zero own traffic from ZenVizor processes.** (Founding invariant.)
- [ ] CPU budget still passes (< 1 % idle, < 80 MB working set) with the dashboard open + polling.
- [ ] Phase 3 boxes in Sprint Plan checked off.
- [ ] Commit pushed, CI green on `windows-latest`.

---

## 10. Reference snippets the implementer will need

### Pipe / IPC primer

```csharp
// New contract addition (ZenVizor.Ipc.Contracts/IZenVizorIpc.cs):
Task<IpcEnvelope<ActivitySnapshot>> GetCurrentActivitySnapshotAsync();

// Envelope (ZenVizor.Ipc.Contracts/IpcEnvelope.cs):
public sealed record IpcEnvelope<T>(int SchemaVersion, T Payload);

// Snapshot (ZenVizor.Ipc.Contracts/Dto/ActivitySnapshot.cs):
public sealed record ActivitySnapshot(
    long CapturedAtUnixMs,
    double WindowSeconds,
    IReadOnlyList<AppActivity> Apps);

public sealed record AppActivity(
    string ImageName,
    string ImagePath,
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath,
    string? HostedServices,
    long BytesUpTotal,
    long BytesDownTotal,
    double BytesUpPerSec,
    double BytesDownPerSec);
```

### Build / install / smoke loop

```powershell
# From repo root, non-elevated:
dotnet build .\ZenVizor.slnx -c Release
dotnet test  .\ZenVizor.slnx -c Release

# Elevated (reinstall service after a code change):
.\scripts\uninstall-dev.ps1            # add -PurgeData to wipe %ProgramData%\ZenVizor\
.\scripts\install-dev.ps1
sc.exe query ZenVizor                  # confirm STATE 4 RUNNING

# Non-elevated, smoke-test:
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe status
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe snapshot
```

### Read-only DB query (elevated PS, single-line form per CLAUDE.md gotcha)

```powershell
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
sqlite3.exe -readonly -header -column $db "SELECT a.image_name, COUNT(*) AS rows FROM apps a JOIN process_sessions ps USING(app_id) JOIN traffic_samples s USING(session_id) GROUP BY a.image_name ORDER BY rows DESC LIMIT 20;"
```
