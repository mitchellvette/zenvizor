# ZenVizor Audit 1 — Findings and Fix Prompts

**Date:** 2026-06-10 · **Branch:** `ui-design-prep` (clean at audit time) · **Scope:** the 12-item audit checklist plus review of the proposed MainWindow shutdown-fix/exit-trace prompt (item 13).

Each finding has an ID, the audit item it belongs to, file:line evidence, and a severity. The "Paste-ready fix prompts" section at the bottom bundles findings into self-contained prompts for future sessions; each prompt references the finding IDs it resolves.

---

## Verification summary

- `dotnet build` (Debug): **0 warnings, 0 errors**.
  - Release build is currently **blocked by the running ZenVizor service** (it executes from `src\ZenVizor.Service\bin\Release` and locks the DLLs → MSB3027). Stop the service (`sc.exe stop ZenVizor`) before Release builds.
- `dotnet test` (Debug): **281/281 pass** (Core 85, Storage 87, Attribution 48, Integration 44, Ipc 17).
- Transitive package scan: no telemetry or network-calling SDKs. Serilog sinks are File + EventLog only. TraceEvent ships an (unused) symbol-download capability — see I2.
- **No non-negotiable invariant is currently violated.** See "Verified clean".

---

## Severity index

| ID | Item | Sev | Finding |
|----|------|-----|---------|
| H1 | 1, 2 | High | Pipe ACL grants INTERACTIVE `CreateNewInstance` → local pipe-instance squatting/spoofing |
| H2 | 3 | High | Version negotiation enforced client-side only; server never gates un-negotiated calls |
| H3 | 12 | High | zvctl exit codes are dead — failures exit 0 |
| H4 | 6 | High | Dead ETW capture loop masked as healthy (`IsRunning` stays true) |
| H5 | 6 | High | Flush failure silently drops the swapped byte accumulators |
| H6 | 8 | High | Full merged-snapshot rebuild per ETW event (O(connections) allocs/event) |
| H7 | 8 | High | Full-cache eviction scan per event under lock |
| H8 | 5 | High | ReportsPage MaxHeight hook `EnforceTopAppsGridBound` doesn't exist; wheel-forwarder breaks on overflow |
| H9 | 9 | High | CSS crosswalk stale: shipped migrations still "needs-migration"; real deltas missing |
| H10 | 9 | High | design-system.md documents the `{StaticResource space.*}` → Margin pattern that crashes at runtime |
| H11 | 9 | High | claude-design-primer.md badly stale vs colors_and_type.css (one-way drift, many tokens) |
| H12 | 9 | High | 6 brush keys exist only in BrandAccent/HC dicts; no fallback in DesignTokens.xaml |
| H13 | 9 | High | HighContrast.xaml is never merged — HC mode is dead code |
| H14 | 11 | High | Two debugging docs stale: `window-switch-ui-lag.md` (fix shipped, still "Unresolved P0") and `app-detail-chart-not-rendering.md` (obsolete) |
| M1 | 2 | Med | Raw exceptions (type, message, server stack) serialize back to IPC clients |
| M2 | 2 | Med | Zero input validation on query RPCs (unbounded window + forced `Samples` grain = full-scan DoS lever) |
| M3 | 2 | Med | ProgramData ACL failure is fail-open (logs, then creates DB with inherited Users-read perms) |
| M4 | 2 | Med | ACL upgrade path never purges pre-existing explicit ACEs |
| M5 | 2 | Med | UserWritablePathClassifier misses Desktop/Documents/profile root/`C:\ProgramData`; no `\\?\` strip, 8.3 expansion, or junction resolution |
| M6 | 2 | Med | Basename-only attribution silently classified "not user-writable" + `Unchecked` → silent alert miss |
| M7 | 3 | Med | No client ever checks `IpcEnvelope.SchemaVersion` |
| M8 | 3, 10 | Med | IPC tests exercise only `FakeIpcHandler`; drifted assertion (test expects SchemaVersion 1, prod stamps 2); zero real pipe round-trip tests |
| M9 | 3, 12 | Med | `GetDailyReportAsync` has no zvctl command and no RPC test; `GetCaptureStatsAsync` no RPC test |
| M10 | 4 | Med | `DailyReportRepository` fabricates sequential `AlertId`s not backed by the alerts table |
| M11 | 5 | Med | PerAppPage drill grid missing `Cursor="Hand"` (canonical drill affordance incomplete) |
| M12 | 6 | Med | Shutdown drops queued observations (reader canceled before capture source drained) |
| M13 | 6, 7 | Med | Retention DELETEs unchunked; no explicit `busy_timeout` anywhere |
| M14 | 7 | Med | No downgrade guard: older binary silently opens newer-schema DB |
| M15 | 7, 10 | Med | No crash-mid-migration / partial-failure test |
| M16 | 8 | Med | Phantom PID = Win32 call + thrown exception per event (no negative cache) |
| M17 | 8 | Med | Per-event string/`byte[]` allocations (`Address.ToString()`, `GetAddressBytes()`) |
| M18 | 8 | Med | WinVerifyTrust + SCM enumeration run inside the aggregator lock on session-open |
| M19 | 9 | Med | design-system.md §8 density values stale (22px/6,2 vs shipped 32/12,8) |
| M20 | 9 | Med | design-system.md §1 inventory stale (Reports "placeholder", "double-click" drill, Talkers brush) |
| M21 | 9 | Med | PerApp main DataGrid card uses flat `surface.card` with no documented exception |
| M22 | 11 | Med | CLAUDE.md references `ZenVizor.sln`; actual file is `ZenVizor.slnx` (build commands fail as written) |
| M23 | 11 | Med | `reports-implementation-progress.md` status block contradicts shipped Phases 3–5 |
| M24 | 1 | Med | `SnapshotPathDoesNotReadSqliteTests` does not guard the UI-no-DB invariant (no architecture test exists) |
| M25 | 10 | Med | Untested: `ProgramDataAcl`, pipe-security setup, `ZenVizorIpcHandler`, `CaptureMonitor`, `ZenVizorHostedService`, `ChartBuilder` (no UI test project) |
| M26 | 4 | Med | Alert pipeline is schema-only (no insert path, no IPC methods, no UI feed) — Phase 6 scope, tracked here so it isn't lost |
| L1 | 1, 7 | Low | EnrichmentBackfill: connection-per-row, unbounded total pass, runs on startup critical path before capture |
| L2 | 2 | Low | IP Helper: no `ERROR_INSUFFICIENT_BUFFER` retry; row walk trusts `dwNumEntries` without bounds check |
| L3 | 2 | Low | SCM: `ERROR_MORE_DATA` partial results discarded; raw `IntPtr` handle instead of SafeHandle |
| L4 | 3 | Low | Dead contract fields: `FirstSeenUnixMs`/`LastSeenUnixMs` populated but never consumed |
| L5 | 12 | Low | CLI surfaces raw exception type names (`ERROR FormatException: …`); negative `--top` silently empty |
| L6 | 12 | Low | `install-dev.ps1` stop/delete race (500 ms sleep instead of polling STOPPED) |
| L7 | 4 | Low | No test fake implements `IMonitor` (seam #1 has one untested production impl) |
| L8 | 4 | Low | Stale "Phase 0 stub" doc comments on `ZenVizorIpcHandler` and `IZenVizorIpc` |
| L9 | 5 | Low | ReportsPage comments claim drill goes to HistoryPage; code drills to AppDetailPage |
| L10 | 5 | Low | Em-dash in rendered prose: AlertsPage.cs:7, SettingsPage.cs:7, AppDetailPage.xaml:1174+1262, DashboardPage.xaml.cs:357-358+384, DailyReportHtmlWriter.cs:74+197 |
| L11 | 6 | Low | Poller `Stop()` doesn't join its loop; Start-after-Stop can briefly overlap two loops |
| L12 | 7 | Low | Retention cutoff not aligned via `BucketAligner` (bucket survival depends on purge time-of-day) |
| L13 | 10 | Low | phase-1-verification.md pre-flights sqlite3 *after* first use (rule: pre-flight first) |
| L14 | 10 | Low | phase-4-verification.md:207 uses invalid SQLite literal `30L` |
| L15 | 9 | Low | ReportsPage.xaml:290-293 garbled leftover comment ("radius.card = 8 — wait, those are inverted") |
| L16 | 11 | Low | Phase-3/4 plan exit-criteria checkboxes all unchecked despite both phases shipped |
| L17 | 2, 4 | Low | `RealProcessImageResolver` is dead code with a latent `IsFresh => true` PID-reuse bug — remove |
| I1 | 1 | Info | ReportsPage opens exported HTML via default browser (`UseShellExecute`); report is self-contained, zero fetches — fine, keep the writer self-contained |
| I2 | 1 | Info | TraceEvent's `SymbolReader` can hit symbol servers; unused today — flag any future symbol feature as invariant-1 risk |
| I3 | 2 | Info | `GetServiceStatusAsync` discloses the DB path to any interactive caller (path is predictable anyway) |
| I4 | 2 | Info | Design tension: DB ACL'd against standard users, but IPC serves the same data to INTERACTIVE — document as intentional or gate |
| I5 | 4 | Info | `InMemoryPidTableSource`/`InMemoryProcessImageResolver` ship in the Core assembly but are referenced only by tests (inert) |
| I6 | 3 | Info | Phase-0 methods (Negotiate/Ping/GetServiceStatus) bypass the envelope by documented design |
| I7 | 12 | Info | No WiX installer yet — Phase 6 scope per sprint plan; `uninstall-dev.ps1` deliberately preserves ProgramData unless `-PurgeData` |
| I8 | 10 | Info | `DailyReportRepositoryTests.cs:351` uses `BeApproximately(100.0, 0.5)` where the fixture is exactly 100.0 |
| I9 | 10 | Info | CI triggers only on push/PR to main/master — feature branches get no CI until PR |
| I10 | 5 | Info | Dashboard TalkersList rows are not drillable while equivalent rows elsewhere drill — decide intentionally |
| I11 | 6 | Info | `EtwCaptureSource._internalCts` created/disposed but never used |
| I12 | 8 | Info | Unbounded channel (if reader dies → ties to H4) and unbounded AppEnricher cache — unenforced vs 80 MB budget |
| I13 | 11 | Info | known-bugs.md TRAY-01 is still real and consistent with current code (being instrumented by the item-13 prompt) |
| I14 | 11 | Info | TitaniRun/trctl grep: zero rename misses; only intentional historical references |

---

## Finding details (High + Medium)

### H1 — Pipe ACL grants INTERACTIVE `CreateNewInstance` (items 1, 2)
`src\ZenVizor.Ipc.Server\ZenVizorPipeServer.cs:131-134` grants `PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance` to `InteractiveSid`, with `MaxAllowedServerInstances` (line 83). Any non-admin interactive process can create its own server instance of `\\.\pipe\ZenVizor.Ipc.v1` and impersonate the service to later-connecting clients (UI, zvctl). Clients only need `ReadWrite`.

### H2 — Version negotiation not enforced server-side (item 3)
`IZenVizorIpc.cs:16` promises the server "may close the connection" on mismatch, but `ZenVizorIpcHandler.cs:103-117` just returns `Accepted: false` statelessly, and `ZenVizorPipeServer.HandleConnectionAsync` (`ZenVizorPipeServer.cs:91-111`) tracks nothing. A client that skips `NegotiateVersionAsync` can call every method. Enforcement exists only inside `ZenVizorPipeClient.ConnectAsync` (`ZenVizorPipeClient.cs:72-81`), which a hostile/old client doesn't use.

### H3 — zvctl exit codes dead (item 12)
Handlers set `Environment.ExitCode` (`src\ZenVizor.Cli\Program.cs:14` etc.), but `Main` returns `await root.InvokeAsync(args)` (line 97) and a `Main` return value overrides `Environment.ExitCode`. System.CommandLine 2.0.0-beta4 `SetHandler` returns 0 whenever the handler runs, so "ERROR service is not running" exits 0. The designed codes (1 generic / 2 version mismatch / 3 timeout) never reach scripts.

### H4 — Dead capture masked as healthy (item 6)
`EtwCaptureSource.cs:140-155`: `ProcessLoop` catches all exceptions and completes the channel; `CaptureMonitor.ReaderLoopAsync` (`CaptureMonitor.cs:102-118`) then exits normally — but `IsRunning` stays `true` and IPC keeps reporting capture active forever.

### H5 — Flush failure drops data (item 6)
`TrafficAggregator.cs:142-145` swaps `_samples`/`_connections` to fresh dictionaries *before* `_sink.Flush`; on sink exception only SessionTracker state is preserved (line 199 comment) — the swapped accumulators are lost. `CaptureMonitor.cs:137-140` logs "Flush tick failed" and continues.

### H6 — Full snapshot rebuild per ETW event (item 8)
`TrafficAggregator.Observe` (`TrafficAggregator.cs:81`) reads `_snapshotSource.CurrentSnapshot` per event; `ConnectionLifecycleResolver.CurrentSnapshot` (`ConnectionLifecycleResolver.cs:95-125`) rebuilds the merged view on every call: new List + HashSet + a record alloc per cached endpoint + new `PidTableSnapshot`. O(connections) allocations per event.

### H7 — Full-cache eviction scan per event (item 8)
`ProcessLifecycleResolver.Resolve` (`ProcessLifecycleResolver.cs:123`) calls `EvictStale` — a whole-dictionary scan under lock — and `Resolve` runs per observation via `SessionTracker.TryTrack` (`SessionTracker.cs:67`).

### H8 — ReportsPage phantom MaxHeight hook (item 5)
`ReportsPage.xaml:1296-1297` claims "MaxHeight enforcement happens in code-behind (EnforceTopAppsGridBound)" — the method does not exist; `ReportsPage.xaml.cs:704` admits Phase 3 relies on the fixed 10-row Top-N. The wheel-forwarder (`ReportsPage.xaml.cs:709-718`) unconditionally swallows wheel events and breaks the moment the grid needs internal scroll. (Memory note context: Wpf.Ui NavigationView wraps pages in DynamicScrollViewer, so DataGrids need programmatic MaxHeight.)

### H9 — Crosswalk stale and incomplete (item 9)
`docs/design/colors_and_type.css:42-52` still lists radius.sm/md/lg and the four font sizes as "needs-migration" though `DesignTokens.xaml:280-311` already ships the brand values; the "Current XAML" column for chart.up/downSeries is wrong (XAML ships violet/teal via BrandAccent). Missing deltas: `density.row.compact` (CSS 22 vs XAML 32, `DesignTokens.xaml:473`), compact cell padding (6,2 vs 12,8 at `:486`), `metal.control` (translucent in CSS vs opaque gradient in `BrandAccent.Light.xaml:196-199`), dark `shadow.card` opacity (0.45 vs 0.25, `BrandAccent.Dark.xaml:166`), and the radius role tokens (`radius.control/card/overlay`, `DesignTokens.xaml:293-295`) absent from CSS.

### H10 — design-system.md documents a crash pattern (item 9)
`docs/design-system.md:463-464` ("Tokens are Double resources so they slot directly into Margin/Padding via `{StaticResource space.16}`") and the §9 banner advice (`:579,594`) contradict `DesignTokens.xaml:230-237` and the standing rule: Double → Thickness fails and the page won't load.

### H11 — claude-design-primer.md stale (item 9)
`docs/claude-design-primer.md`: chart series `#56B4E9`/`#E69F00` (`:118-119`), old radius ladder 4/6/8 (`:201-206`), Card = "surface.card + radius.md" (`:239-240`), Urbanist missing SemiBold (`:146`), "Double-click → App Detail" (`:32`). Missing CSS variables: accent.fill, status.caution.text, surface.tooltip.scrim, chart-chrome tokens, metal-card/edge-light/shadow tokens, space-20/40/64, font-size-body-large.

### H12 — Brush keys without base fallback (item 9)
`surface.tooltip.scrim`, `metal.card`, `metal.control`, `edge.light`, `shadow.sm`, `shadow.card` exist only in BrandAccent.{Light,Dark}.xaml + HighContrast.xaml. Views reference them via DynamicResource (e.g. `AppDetailPage.xaml:1291`); if the brand dict is unmerged before HC merges (the documented HC scenario, `design-system.md:330-332`), every card silently loses background/shadow.

### H13 — HighContrast.xaml never merged (item 9)
No code references HighContrast.xaml; its own header (`HighContrast.xaml:8-15`) says "App startup is responsible for merging." Known gap (design-system.md §11 item 9) but currently dead code with a misleading header.

### H14 — Stale debugging docs (item 11)
`debugging/window-switch-ui-lag.md`: marked "Unresolved, P0" and instructs a fresh session to start an audit — but the H2 fix path (downsampling) shipped as `ChartSeriesDownsampler` (commit 0a97ab7) and the AnimationsSpeed revert is done. `debugging/app-detail-chart-not-rendering.md`: symptom fixed in 0a97ab7; doc recommends an OxyPlot port that never happened; no resolution note.

### M1 — Raw exceptions to IPC clients (item 2)
`ZenVizorRpcHost.cs:23` attaches StreamJsonRpc with defaults → `CommonErrorData` serializes exception type, message, and server stack trace into `error.data`. `SqliteException` text and `InvalidOperationException($"Unsupported grain {grain}")` (`AppHistoryQueryRepository.cs:54`) reach any interactive caller.

### M2 — No input validation on query RPCs (item 2)
`ZenVizorIpcHandler.cs:155-186` passes `QueryWindow`/`TrafficGrain`/`appId` straight through. `QueryWindow` (`Dto\QueryWindow.cs:8`) has no bounds; `TrafficGrainResolver.Resolve` (`TrafficGrain.cs:35`) passes any non-Auto value; a client can force `grain=Samples` over a 100-year window per call. Negative ranges silently served.

### M3 — ACL failure fail-open (item 2)
`ZenVizorHostedService.cs:60-65` catches `UnauthorizedAccessException`, logs "continuing with inherited perms", and proceeds to create the DB — which then inherits `%ProgramData%` Users-read.

### M4 — Stale ACEs survive upgrade (item 2)
`ProgramDataAcl.cs:25-37` breaks inheritance and adds SYSTEM+Admins but never purges pre-existing explicit ACEs; a looser ACE from an earlier install survives every restart.

### M5 / M6 — Classifier gaps (item 2)
`UserWritablePathClassifier.cs:67-110`: per-user prefixes are only AppData + Downloads (Desktop/Documents/profile root and `C:\ProgramData` unflagged); `NormalizePath` (`:113-118`) doesn't strip `\\?\`, expand 8.3 names, or resolve junctions. `EtwCaptureSource.cs:412-432`: basename-only attributions flow through as "not user-writable" + `Unchecked` — silent alert misses. These feed the (future) unsigned-binary alert; fix before Phase 6 wires it.

### M7 / M8 / M9 — Contract/test hygiene (items 3, 10, 12)
No consumer reads `IpcEnvelope.SchemaVersion` (`HistoryQueryClient.cs:53-54` returns Payload unconditionally). All `InProcessRpcTests` run against `FakeIpcHandler`; `InProcessRpcTests.cs:109` asserts SchemaVersion 1 while production stamps 2 (`ZenVizorIpcHandler.cs:24`) — undetected because the production handler is never under test. Zero real-pipe tests exist despite CLAUDE.md placing them in Integration.Tests. `GetDailyReportAsync` and `GetCaptureStatsAsync` lack RPC tests; zvctl cannot script reports.

### M10 — Fabricated AlertIds (item 4)
`DailyReportRepository.cs:608,629` — `AlertId: alertSeq++` synthesizes IDs not backed by the `alerts` table; will collide/mislink when real alerts land in Phase 6.

### M12 — Shutdown drops queued observations (item 6)
`CaptureMonitor.StopAsync` (`CaptureMonitor.cs:73-89`) cancels the reader loop first; observations still in the channel (plus ETW events arriving until `DisposeAsync` at line 84) never reach the aggregator before the final flush.

### M13 — Retention DELETE risk (items 6, 7)
`RetentionRepository.cs:52-57` runs unchunked DELETEs on a separate connection; first purge after long uptime holds the WAL write lock for the whole statement. No explicit `busy_timeout` anywhere (`ConnectionFactory.cs:18-26`); combined with H5, a flush timeout loses that window's data.

### M14 / M15 — Migration gaps (item 7)
`Migrator.cs:62-74`: versions above the binary's max are silently ignored (no downgrade refusal). Migration transactions are correct by construction (`Migrator.cs:119-141`), but no test exercises a failing migration (`RollupBackfillMigrationTests` covers math + idempotency only).

### M16–M18 — Hot path (item 8)
`ProcessLifecycleResolver.cs:134,211-222`: cache miss → `Process.GetProcessById` throws per event for exited PIDs; no negative cache. `TrafficAggregator.cs:113`: `RemoteEndpoint.Address.ToString()` per event; `RemoteAddressClassifier.cs:32,77`: `GetAddressBytes()` allocates per event. `SessionTracker.OpenSession` (`SessionTracker.cs:106-115`) runs WinVerifyTrust + FileInfo + SCM enumeration while `Observe` holds `_gate` (`TrafficAggregator.cs:84`).

### M19–M21 — Design docs/cards (item 9)
design-system.md §8 density stale (`:544-547`); §1 inventory stale (`:90-99` Reports placeholder, `:61` double-click, `:52` Talkers brush; `:434` CharacterSpacing claim contradicts `DesignTokens.xaml:422-427`). PerApp main DataGrid card is flat `surface.card` (`PerAppPage.xaml:310`) while `design-system.md:643` claims PerApp ships the canonical recipe — migrate or document the exception.

### M22 / M23 — Doc drift (item 11)
CLAUDE.md build commands and layout reference `ZenVizor.sln`; only `ZenVizor.slnx` exists (confirmed: `dotnet build ZenVizor.sln` fails MSB1009). `reports-implementation-progress.md:30-34,384` marks Phases 3–5 open; commit 1121b4f shipped them.

### M24 — UI-no-DB invariant has no guard (item 1)
`SnapshotPathDoesNotReadSqliteTests.cs:42-99` is strong for the snapshot path (armed `ConnectionFactory.Open()` throws) but never touches `ZenVizor.Ui`; adding a Storage reference to the UI would fail no test. Needs an architecture test on the UI's referenced assemblies.

### M25 / M26 — Coverage gaps; alert pipeline (items 10, 4)
Untested production classes: `ProgramDataAcl`, pipe security setup (`ZenVizorPipeServer.cs:113-137`), `ZenVizorIpcHandler`, `CaptureMonitor`, `ZenVizorHostedService`, `ChartBuilder` (no UI test project exists). Alert pipeline: `alerts` table exists (`001_initial.sql:119`) and retention deletes from it, but nothing inserts, no IPC methods, no UI feed — Phase 6 scope, tracked so the "one real alert" PRD commitment isn't lost.

---

## Verified clean (the good news)

- **Invariant 1 (zero own traffic):** no network APIs anywhere in src; the sole `System.Net.Sockets` import is the `AddressFamily` enum in a pure classifier; all packages benign and pinned; no telemetry SDKs in the transitive graph.
- **Invariant 2 (named pipes only):** no TCP/HTTP/gRPC listeners; explicit `PipeSecurity` (SYSTEM + Admins FullControl, INTERACTIVE ReadWrite — see H1 for the one excess right); no Everyone/Anonymous/remote ACE.
- **Invariant 3 (UI no DB):** UI references only Core + Ipc.Client + Ipc.Contracts; no Sqlite anywhere in the UI project (guard-test gap is M24, not a violation).
- **Invariant 4 (no per-event writes):** `Observe` mutates memory under a lock; one transaction per 5 s flush tick covers sessions, samples, connections, and rollups.
- **Invariant 5 (honest attribution):** co-hosted services enumerated and joined; rollups sum PID totals; no byte splitting/proration anywhere.
- **Invariant 6 (offline verification):** `WTD_REVOKE_NONE` + `WTD_REVOCATION_CHECK_NONE` + `WTD_CACHE_ONLY_URL_RETRIEVAL`; state handle closed unconditionally; results cached per (path, mtime, size).
- ProgramData DACL: exactly SYSTEM + Admins, inheritance broken, applied before DB creation, re-applied every start.
- SQL: fully parameterized; only compile-time constants interpolated.
- `devices` table: zero writes (CREATE TABLE only).
- HTML report writer: every dynamic field escaped; CSS-class injection separately sanitized; tests assert it.
- Determinism: synthetic-event tests assert exact rows (only justified tolerances found; I8 is the one nit).
- CI: build + headless tests, no ETW/elevation dependency; test failure fails the job.
- No `.Result`/`.Wait()`/sync-over-async; all `async void` are WPF event handlers.
- Kernel ETW session: `StopOnDispose`, stale-session cleanup on start, idempotent Start/Stop.
- Spacing-token Thickness misuse in XAML: zero. Nav matrix: complete, both directions. Drill double-click leftovers: none.
- Rename: zero TitaniRun/trctl misses.

---

## Item 13 — Review of the shutdown-fix + exit-trace prompt

**Verdict: root cause confirmed, fix is sound. One instruction conflict must be corrected before pasting; two small additions recommended.** The corrected prompt is P10 below.

Evidence verified this session:

1. `App.xaml:5` — `ShutdownMode="OnExplicitShutdown"`. Confirmed.
2. `MainWindow.xaml.cs:120` — `SystemThemeWatcher.UnWatch(this)` is the first line of `OnClosed`, exactly as the prompt claims. Confirmed.
3. Wpf.Ui **4.0.2** (pinned, `Directory.Packages.props:28`) source: `UnWatch` inline-throws `InvalidOperationException("Could not get window handle.")` when `new WindowInteropHelper(window).Handle == IntPtr.Zero`. WPF raises `Closed` after WM_DESTROY, so the handle is zero there; the throw aborts `OnClosed` before poller disposal and `Application.Current.Shutdown()`, and with `OnExplicitShutdown` the app never exits cleanly. Mechanism confirmed.
4. Edge cases checked: the `_exiting` branch of `OnClosing` runs while the HWND is still alive (correct new home for UnWatch); a duplicate `Application.Current.Shutdown()` is a no-op; an OS-forced close (session end) takes the `_exiting == false` path so UnWatch is skipped entirely there — harmless, the hook dies with the HWND. `docs/known-bugs.md` TRAY-01 is consistent with the state this prompt instruments (I13).

Corrections applied in P10:

1. **Contradiction fixed:** the original says "Do NOT alter control flow to add these" but `cm-resolved` and `popup-cast` live inside compound pattern-match `if`s (`MainWindow.xaml.cs:165-168`); logging them requires decomposing those conditions into locals. P10 explicitly authorizes the decomposition with semantics held identical.
2. **Added (cheap, high value):** trace `closing:unwatch-ok` / `closing:unwatch-threw <ExceptionType>` inside the new try/catch — field-confirms the root cause from the log itself.
3. **Expectation note added:** the log will also contain `closing:enter exiting=False` lines from ordinary close-to-tray hides; expected, not a bug.
4. **Logger pinned to `File.AppendAllText` per line** — flush-per-call means lines survive even if the process crashes mid-exit.

---

## Paste-ready fix prompts

Run each in its own session. They are ordered roughly by value; P10 (shutdown) is independent and can go first.

### P1 — IPC surface hardening (H1, H2, M1, M2, I3)

```
Harden the ZenVizor IPC surface. Four changes, all service/server side:

1. src\ZenVizor.Ipc.Server\ZenVizorPipeServer.cs:131-134 — the INTERACTIVE ACE grants
   PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance. Drop CreateNewInstance
   (clients only need ReadWrite); SYSTEM/Admins keep FullControl. This closes a local
   pipe-instance squatting hole.
2. Enforce version negotiation server-side. Today ZenVizorIpcHandler.NegotiateVersionAsync
   (ZenVizorIpcHandler.cs:103-117) returns Accepted:false statelessly and nothing gates
   later calls — a client that skips negotiation can call everything
   (IZenVizorIpc.cs:16 promises otherwise). Track per-connection negotiation state in
   ZenVizorPipeServer.HandleConnectionAsync (ZenVizorPipeServer.cs:91-111): on mismatch,
   fault/dispose the JsonRpc session; reject enveloped query methods on un-negotiated
   connections with a typed error. Phase-0 methods (Negotiate/Ping/GetServiceStatus)
   stay callable pre-negotiation by design (IpcEnvelope.cs:13-15).
3. Stop leaking raw exceptions to clients. ZenVizorRpcHost.cs:23 attaches StreamJsonRpc
   with defaults, so exception type/message/stack (incl. SqliteException text) serialize
   into error.data. Configure the JsonRpc instance to return a sanitized generic fault
   (log the full exception server-side with Serilog).
4. Validate inputs on every query RPC (ZenVizorIpcHandler.cs:155-186): reject
   QueryWindow.To < From and windows beyond the retention horizon; validate TrafficGrain
   via Enum.IsDefined (TrafficGrain.cs:35 currently passes any non-Auto value through);
   reject appId <= 0. Return typed validation errors, not exceptions.

Add tests: pipe-security rule assertions, a negotiation-gating test, an error-sanitization
test, and validation tests — host the PRODUCTION ZenVizorIpcHandler in-process (see also
the test-hygiene prompt; InternalsVisibleTo may be needed). Constraints: named pipes only,
no new dependencies, zero own network traffic. Build + test clean before stopping.
```

### P2 — Capture/aggregation reliability (H4, H5, M12, I11)

```
Fix three reliability bugs in the ZenVizor capture/aggregation pipeline (no behavior
changes beyond the fixes):

1. Dead capture is masked as healthy. EtwCaptureSource.ProcessLoop
   (src\ZenVizor.Capture\EtwCaptureSource.cs:140-155) catches all exceptions and completes
   the channel; CaptureMonitor.ReaderLoopAsync (src\ZenVizor.Service\CaptureMonitor.cs:102-118)
   exits normally but IsRunning stays true and IPC keeps reporting capture active. Surface
   loop death into a health flag consumed by the capture-active status (and consider a
   bounded restart policy). Add a test via the synthetic ICaptureSource.
2. Flush failure silently drops data. TrafficAggregator.Flush
   (src\ZenVizor.Core\Aggregation\TrafficAggregator.cs:142-145) swaps _samples/_connections
   to fresh dictionaries BEFORE _sink.Flush; on sink exception the swapped accumulators are
   lost (only SessionTracker state survives, line 199). Merge the snapshot back into the
   live accumulators on sink failure so the next tick retries with the data intact. Add a
   failing-sink test asserting exact rows after recovery (determinism rule: exact, not
   approximate).
3. Shutdown drops queued observations. CaptureMonitor.StopAsync (CaptureMonitor.cs:73-89)
   cancels the reader loop first, so channel-queued observations never reach the aggregator
   before the final flush. Reorder: dispose the capture source first (completes the channel
   writer), await reader drain to completion, then final flush.

Cleanup while there: EtwCaptureSource._internalCts (lines 39, 68, 491) is created and
disposed but never used — remove it. Keep the hot path allocation-free; build + test clean.
```

### P3 — Hot-path performance (H6, H7, M16, M17, M18, I12)

```
Performance pass on ZenVizor's per-ETW-event hot path. Project rule: optimize aggressively;
idle CPU < 1%, working set < 80 MB. Six changes:

1. ConnectionLifecycleResolver.CurrentSnapshot
   (src\ZenVizor.Attribution\ConnectionLifecycleResolver.cs:95-125) rebuilds the merged
   snapshot on EVERY call — and TrafficAggregator.Observe (TrafficAggregator.cs:81) calls it
   per event. Cache the merged snapshot; invalidate on connect/disconnect/fallback refresh.
2. ProcessLifecycleResolver.Resolve (ProcessLifecycleResolver.cs:123) runs EvictStale — a
   full _byPid scan under lock — per observation. Amortize (timer or every-N-calls).
3. Same file, lines 134, 211-222: a cache-missed PID hits Process.GetProcessById which
   throws ArgumentException for exited PIDs, every event, forever. Add a short-TTL negative
   cache.
4. TrafficAggregator.cs:113 allocates RemoteEndpoint.Address.ToString() per event for
   ConnectionKey — key by IPAddress (or a struct) and defer string formatting to flush time.
5. RemoteAddressClassifier (src\ZenVizor.Core\Classification\RemoteAddressClassifier.cs:32,77)
   allocates GetAddressBytes() per event — use TryWriteBytes into a stackalloc span.
6. SessionTracker.OpenSession (SessionTracker.cs:106-115) runs WinVerifyTrust + FileInfo +
   SCM enumeration while Observe holds TrafficAggregator._gate (TrafficAggregator.cs:84).
   Move enrichment outside the lock (enrich-then-publish, or queue session-open enrichment).

Also bound the two unbounded growth points: the capture channel (EtwCaptureSource.cs:30-35)
and the AppEnricher cache (AppEnricher.cs:31).

All existing synthetic-event tests must still assert EXACT rows and pass unchanged unless a
test itself encoded the bug. No per-event DB writes, no new allocations on the hot path.
Build + test clean.
```

### P4 — Attribution & native-interop hardening (M5, M6, L2, L3, L17)

```
Harden ZenVizor's attribution layer. This feeds the Phase-6 unsigned-binary alert, so
misclassification = missed alerts. Honest-attribution invariant: never fabricate precision;
unknown must read as unknown, not as "safe".

1. UserWritablePathClassifier (src\ZenVizor.Attribution\Paths\UserWritablePathClassifier.cs:67-110):
   per-user prefixes are only AppData + Downloads. Add: the rest of the user profile root
   (Desktop, Documents, etc. — keep the existing Public/Default exclusions) and C:\ProgramData.
   NormalizePath (:113-118) must strip \\?\, expand 8.3 short names
   (C:\Users\MITCH~1\... currently fails StartsWith), and resolve via Path.GetFullPath.
   Comparisons stay OrdinalIgnoreCase. Add exact-assertion tests for each new root and
   each path form.
2. Basename-only attributions (EtwCaptureSource.cs:412-432): when ImageFileName isn't a
   full path and CommandLine parsing fails, the classifier currently returns
   "not user-writable" and the verifier returns Unchecked — a silent alert miss. Introduce
   a distinct path-unknown classification so downstream consumers can't mistake it for safe.
3. IpHelperPidTableSource (src\ZenVizor.Attribution\IpHelper\IpHelperPidTableSource.cs:192-227):
   add a bounded retry when the table grows between the size probe and the call
   (ERROR_INSUFFICIENT_BUFFER), and clamp the row walk so 4 + rowCount*rowSize <= bufferSize.
4. ScmServiceHostResolver (src\ZenVizor.Attribution\Services\ScmServiceHostResolver.cs:96-153):
   loop on ERROR_MORE_DATA with the resume handle instead of discarding partial results;
   wrap the SCM handle in a SafeHandle.
5. Delete src\ZenVizor.Attribution\RealProcessImageResolver.cs — dead code (composition root
   uses ProcessLifecycleResolver) with a latent IsFresh => true PID-reuse bug.

Headless tests only (synthetic streams); no elevation in CI. Build + test clean.
```

### P5 — Storage robustness (M13, M14, M15, L1, L12)

```
Storage/migration robustness pass for ZenVizor (SQLite, service-owned DB):

1. Downgrade guard: Migrator.cs:62-74 silently ignores schema_migrations versions above the
   binary's max embedded migration. Refuse to start (clear log + fail fast) when the DB
   records a version the binary doesn't ship.
2. Crash-mid-migration test: migrations are transactional by construction
   (Migrator.cs:119-141) but unproven. Add a test that injects a deliberately failing
   migration and asserts: no version row, no partial DDL.
3. Retention sweeps: RetentionRepository.cs:52-57 runs unchunked DELETEs (first purge after
   long uptime can hold the WAL write lock through the 5s flush tick). Chunk via
   DELETE ... LIMIT loops. Also align each tier's cutoff with BucketAligner
   (currently raw nowUnixMs - DaysToMs(days), so bucket survival depends on purge
   time-of-day).
4. Set an explicit busy_timeout on every connection path (ConnectionFactory.cs:18-26
   currently relies on driver defaults).
5. EnrichmentBackfill (src\ZenVizor.Storage\EnrichmentBackfill.cs): opens one connection per
   updated row (:137-153), loads ALL pending rows in one pass (:118-135, no LIMIT), and runs
   in StartAsync BEFORE capture starts — a big backlog delays capture startup. Reuse one
   connection/transaction per batch, cap rows per service start, and move it off the
   startup critical path (background after capture is up).

Tests use temp SQLite files (never %ProgramData%) and assert exact rows. Build + test clean.
```

### P6 — IPC/CLI contract & test hygiene (H3, M7, M8, M9, L4, L5, L6, I8)

```
Contract/test hygiene pass on ZenVizor's IPC and zvctl CLI:

1. zvctl exit codes are dead: handlers set Environment.ExitCode
   (src\ZenVizor.Cli\Program.cs:14 etc.) but Main returns root.InvokeAsync(args) (:97),
   which overrides it — every handled failure exits 0. With System.CommandLine
   2.0.0-beta4, switch to the SetHandler(InvocationContext) overloads and set
   context.ExitCode (1 generic / 2 version mismatch / 3 timeout, as designed). Verify with
   a script-level check.
2. Clients never check IpcEnvelope.SchemaVersion (HistoryQueryClient.cs:53-54 returns
   Payload unconditionally; CLI same). Add an expected-version (or floor) check in
   HistoryQueryClient and the CLI with a typed error.
3. IPC tests only exercise FakeIpcHandler. InProcessRpcTests.cs:109 asserts
   SchemaVersion == 1 while production stamps 2 (ZenVizorIpcHandler.cs:24) — undetected
   drift. Add in-process tests hosting the PRODUCTION ZenVizorIpcHandler (InternalsVisibleTo
   as needed), fix the drifted assertion, and add RPC tests for GetCaptureStatsAsync and
   GetDailyReportAsync.
4. Add ONE real named-pipe round-trip test in ZenVizor.Integration.Tests
   (ZenVizorPipeServer <-> ZenVizorPipeClient; pipes work unelevated on CI) — CLAUDE.md
   already mandates this placement. Keep contract tests in-process.
5. Add zvctl report --date <yyyy-MM-dd> [--json] wired to GetDailyReportAsync so QA can
   script the Phase-5 report surface.
6. Minor: friendly validation for --window from=,to= (currently raw
   "ERROR FormatException"); reject negative --top; decide FirstSeenUnixMs/LastSeenUnixMs
   on AppListEntry/ConnectionRow (consume in zvctl output or comment as reserved);
   install-dev.ps1:56-59 should poll sc query for STOPPED instead of sleeping 500ms.
7. Nit: DailyReportRepositoryTests.cs:351 BeApproximately(100.0, 0.5) — the fixture is
   exactly 100.0; assert Be(100.0).

Build + test clean; zvctl help must match wired commands.
```

### P7 — UI fixes (H8, M11, L9, L10, I10)

```
UI polish pass on ZenVizor (WPF + Wpf.Ui). Known constraint: NavigationView wraps hosted
pages in a DynamicScrollViewer, so DataGrids need MaxHeight set programmatically
(code-behind Loaded + SizeChanged), not via XAML bindings.

1. ReportsPage: XAML comment (ReportsPage.xaml:1296-1297) claims MaxHeight enforcement in
   code-behind method EnforceTopAppsGridBound — that method DOES NOT EXIST
   (ReportsPage.xaml.cs:704 admits Phase 3 relies on the fixed 10-row Top-N). Implement it,
   mirroring PerAppPage's pattern (PerAppPage.xaml.cs:61,82,97), and make the wheel-forwarder
   (ReportsPage.xaml.cs:709-718) conditional: only forward when the grid has no internal
   scroll to do.
2. PerAppPage: AppsGrid drills (PreviewMouseLeftButtonUp, PerAppPage.xaml:339) with hover
   chevron but is missing Cursor="Hand" — add it (canonical drill affordance: hover chevron
   + hand cursor + single click; ReportsPage.xaml:1358 has it right).
3. Fix stale ReportsPage comments saying row drill "navigates to HistoryPage"
   (ReportsPage.xaml:280-282, 1302-1306) — code drills to AppDetailPage with date
   (ReportsPage.xaml.cs:738-743).
4. Em-dash sweep (rule: no em-dash in rendered prose; bare "—" as a no-data placeholder is
   fine). Replace with period/colon/semicolon at: AlertsPage.cs:7, SettingsPage.cs:7,
   AppDetailPage.xaml:1174 and :1262, DashboardPage.xaml.cs:357-358 and :384,
   DailyReportHtmlWriter.cs:74 and :197 (exported HTML title "ZenVizor — Daily report").
   Do NOT touch the placeholder glyphs (MainWindow.xaml:245,256, MainWindow.xaml.cs:241-242,
   DashboardPage.xaml:200,209, and the Text="—" placeholders on PerApp/History/AppDetail).
5. Decide: Dashboard TalkersList rows are not drillable while equivalent rows on
   PerApp/Reports drill to App Detail. Either wire the canonical drill or leave a comment
   documenting why not.

Launch the app and verify each page renders and drills correctly before stopping (UI changes
require in-app verification, not just a clean build). Build with 0 warnings.
```

### P8 — Design-system sync (H9, H10, H11, H12, H13, M19, M20, M21, L15)

```
Design-token sync pass for ZenVizor. Sources of truth: src\ZenVizor.Ui\Resources\DesignTokens.xaml
(app) and docs\design\colors_and_type.css (Claude Design mocks); the CSS header crosswalk must
record every value delta. docs\design-system.md mirrors the XAML; docs\claude-design-primer.md
mirrors the CSS. Rule: change a token in either file -> update the other + crosswalk in the
same commit.

1. Rebuild the crosswalk header in docs\design\colors_and_type.css:42-52. Stale rows: radius
   sm/md/lg and font.size subtitle/title/title.large/display are marked "needs-migration" but
   DesignTokens.xaml:280-311 already ships the brand values; chart.up/downSeries "Current
   XAML" column is wrong (XAML ships violet/teal via BrandAccent, theme-swapped). Missing
   deltas to add: density.row.compact (CSS 22px vs XAML 32, DesignTokens.xaml:473), compact
   cell padding (6,2 vs 12,8 at :486), metal.control (CSS translucent rgba vs opaque gradient
   in BrandAccent.Light.xaml:196-199), dark shadow.card opacity (CSS 0.45 vs 0.25 in
   BrandAccent.Dark.xaml:166), radius role tokens radius.control/card/overlay
   (DesignTokens.xaml:293-295, absent from CSS).
2. docs\design-system.md: §6 (:463-464) and §9 (:579,594) document binding space.* tokens
   into Margin/Padding via StaticResource — that CRASHES at runtime (Double->Thickness;
   DesignTokens.xaml:230-237 warns against it). Rewrite to "literal values matching the
   token scale". Also fix §8 density (:544-547, 22px/6,2 -> 32/12,8), §1 inventory (:90-99
   Reports is a full page now; :61 drill is single-click; :52 Talkers card uses metal.card),
   and §5 (:434) CharacterSpacing claim (DesignTokens.xaml:422-427 says letter-spacing is
   not possible in WPF).
3. Regenerate docs\claude-design-primer.md from colors_and_type.css: fix chart series
   (:118-119), radius ladder (:201-206 -> 6/10/14 + xs/xl/pill), card recipe (:239-240 ->
   metal.card + radius.card), Urbanist weights (:146, add SemiBold), drill description (:32).
   Add missing variables: accent.fill, status.caution.text, surface.tooltip.scrim,
   chart-chrome tokens (--chart-axis..--chart-legend-text), metal-card/edge-light/shadow
   tokens, space-20/40/64, font-size-body-large, density 32.
4. Add base fallbacks in DesignTokens.xaml for the 6 keys that exist only in
   BrandAccent.{Light,Dark}.xaml + HighContrast.xaml: surface.tooltip.scrim, metal.card,
   metal.control, edge.light, shadow.sm, shadow.card (views DynamicResource them, e.g.
   AppDetailPage.xaml:1291 — unmerged brand dict = silent card loss).
5. HighContrast.xaml is never merged anywhere (its header :8-15 claims app startup merges
   it). Either wire the SystemParameters.HighContrast merge per design-system.md §11 item 9,
   or change the header to state it is not yet wired — do not leave the contradiction.
6. PerApp main DataGrid card is flat surface.card (PerAppPage.xaml:310) while
   design-system.md:643 claims PerApp ships the canonical metal.card recipe. Either migrate
   the card or document the exception with its rationale (Mica opacity) in design-system.md.
7. Delete the garbled comment at ReportsPage.xaml:290-293 ("radius.card = 8 — wait, those
   are inverted") — radius.card is 10; also check :1154.

Update XAML/CSS/both docs in the SAME commit per the standing rule. If the app builds, launch
and spot-check Dashboard + Reports rendering.
```

### P9 — Docs & dead-code cleanup (H14, M10, M22, M23, L7, L8, L13, L14, L16, I4)

```
Documentation and dead-code cleanup for ZenVizor (no behavior changes except item 2):

1. CLAUDE.md: the solution file is ZenVizor.slnx, not ZenVizor.sln — fix the build/test
   commands and the repository-layout block (dotnet build ZenVizor.sln currently fails
   MSB1009). While there, note that installer/ does not exist yet (Phase 6).
2. DailyReportRepository.cs:608,629 — AlertId: alertSeq++ fabricates IDs not backed by the
   alerts table; when Phase 6 wires real alerts these will collide. Use a sentinel (0) and
   a comment marking them synthesized-until-Phase-6.
3. debugging\window-switch-ui-lag.md — marked "Unresolved, P0" but the fix shipped
   (ChartSeriesDownsampler, commit 0a97ab7; AnimationsSpeed revert done, Dashboard
   deliberately gated at DashboardPage.xaml.cs:154-159). Add a resolved header pointing at
   the commit, or delete the doc.
4. debugging\app-detail-chart-not-rendering.md — obsolete (fixed in 0a97ab7; recommends an
   OxyPlot port that never happened). Add a resolved header pointing at the commit.
5. docs\reports-implementation-progress.md:30-34,384 — status says Phases 3-5 open; commit
   1121b4f shipped Phases 1-5. Update the status block.
6. docs\Phase-3-plan.md:372-381 and docs\Phase-4-plan.md:446-453 — exit-criteria checkboxes
   all unchecked though both phases shipped (commits 82eb9f8, 709898d) with verification
   docs. Tick them with a dated note.
7. Stale "Phase 0 stub" doc comments: ZenVizorIpcHandler.cs:9 and IZenVizorIpc.cs:27 — the
   handler wires real Phase 4/5 providers now. Update.
8. docs\phase-1-verification.md — sqlite3 pre-flight (:126) appears AFTER first sqlite3 use
   (:105-118); move it into the top pre-flight section (:15) per the standing
   tool-dependency rule. docs\phase-4-verification.md:207 — replace invalid SQLite literal
   30L*86400000 with 30*86400000.
9. Document the intentional design tension somewhere durable (PRD or CLAUDE.md): the DB is
   ACL'd against standard users while the IPC pipe serves the same data to INTERACTIVE —
   intentional because the UI is non-elevated; the ACL protects the raw DB, not the data.
10. Optional: add a fake IMonitor in tests so seam #1 has an exercised test double.
```

### P10 — Shutdown fix + temporary exit tracing (item 13, corrected)

The original prompt's root cause and fix were verified correct this audit (see "Item 13" section above). Changes from the original: explicit permission to decompose the compound `if`s for the two condition-result trace lines; two added trace lines inside the new UnWatch try/catch; a note that close-to-tray hides also log `closing:enter exiting=False`; logger mechanism pinned to `File.AppendAllText`.

```
Two-part task on src/ZenVizor.Ui/MainWindow.xaml.cs only: (1) apply a confirmed
shutdown fix, (2) add TEMPORARY exit-sequence tracing to diagnose the tray freeze.
Do NOT change the menu-close logic in OnTrayExitClicked (the IsOpen/Closed/
PopupAnimation dance) — we are MEASURING it this round, not fixing it. Do not touch
the pollers, App.xaml, ChartBuilder, or anything else. Keep the ChartBuilder labeler
guard intact. Build clean and STOP for human verification.

=== PART 1 — shutdown fix (real, keep permanently) ===

Root cause (confirmed against Wpf.Ui 4.0.2 source): OnClosed runs after WM_DESTROY, so
SystemThemeWatcher.UnWatch(this) on line 120 throws InvalidOperationException
("Could not get window handle.") because WindowInteropHelper(window).Handle is already
IntPtr.Zero. That throw skips poller disposal AND Application.Current.Shutdown() — and
with ShutdownMode=OnExplicitShutdown (App.xaml:5), the app never shuts down cleanly.

Fix:
a) In OnClosing, on the `_exiting == true` branch (where the HWND is still alive), call
   UnWatch there, guarded, before letting the close proceed:

       if (_exiting)
       {
           try { SystemThemeWatcher.UnWatch(this); ExitTrace.Log("closing:unwatch-ok"); }
           catch (Exception ex) { ExitTrace.Log($"closing:unwatch-threw {ex.GetType().Name}"); }
           return;
       }

b) In OnClosed, REMOVE the SystemThemeWatcher.UnWatch(this) line (it's now done in
   OnClosing), and make disposal defensive so nothing can abort the Shutdown call:

       try { _poller.Dispose(); } catch { }
       try { _activityPoller.Dispose(); } catch { }
       // Tray.Dispose() intentionally NOT called here (unchanged — keep existing comment)
       Application.Current.Shutdown();

=== PART 2 — temporary exit tracing (remove later) ===

Add a small temporary static logger (e.g. private static class ExitTrace inside
MainWindow.xaml.cs) that appends timestamped lines to %TEMP%\zenvizor-exit-trace.log
using File.AppendAllText (per-line append, so lines survive a crash). Use DateTime.Now
formatted "HH:mm:ss.fff". Wrap all file IO in try/catch so it can never throw. Mark it
`// TEMP EXIT TRACE — remove after diagnosis`.

Emit a trace line at each of these points, in this order, with the exact label given:
  - OnTrayExitClicked: first line                          → "exit-click:enter"
  - OnTrayExitClicked: log whether the ContextMenu resolved  → "cm-resolved=<true/false>"
  - OnTrayExitClicked: log whether (LogicalTreeHelper.GetParent(cm) is Popup) succeeded
                                                            → "popup-cast=<true/false>"
  - OnTrayExitClicked: immediately before `cm.IsOpen = false` → "menu-close:requested"
  - OnTrayExitClicked: in the else/fallback branch before Close() → "exit-click:fallback-close"
  - OnTrayMenuClosed: first line                            → "menu-closed:fired"
  - OnClosing: first line (log the _exiting value)          → "closing:enter exiting=<bool>"
  - OnClosed: first line                                    → "closed:enter"
  - OnClosed: immediately before Application.Current.Shutdown() → "closed:pre-shutdown"

Logging cm-resolved and popup-cast requires decomposing the compound pattern-match
conditions at MainWindow.xaml.cs:165-168 into locals (evaluate `sender is MenuItem mi`,
then `ContextMenuService.GetContextMenu(mi)`, then the Popup cast, logging each). That
decomposition IS permitted — but the resulting behavior must be EXACTLY identical to the
current compound conditions: same branches taken in every case, same order of operations,
PopupAnimation override still only applied when the Popup cast succeeds, Closed+IsOpen
still executed when cm resolves even if the Popup cast fails, fallback Close() only when
cm does not resolve. Beyond that decomposition, do NOT alter control flow — only insert
logging calls at existing points.

Note: OnClosing also fires on every close-to-tray hide, so the log legitimately contains
"closing:enter exiting=False" lines from normal titlebar closes — expected, not a bug.

=== THEN ===

Build, confirm 0 warnings / 0 errors, and STOP. Print exactly:

  CHECKPOINT exit-traced — relaunch, confirm App Detail chart still renders, then exit
  via the tray Exit menu. Reply "done". I will read %TEMP%\zenvizor-exit-trace.log.

Do not propose a menu fix, do not remove the trace, do not change OnTrayExitClicked's
menu logic. Wait for my reply, then read the log and report every line verbatim with the
inter-line time deltas computed.

(Heads-up before you start: if the dev service is running from
src\ZenVizor.Service\bin\Release, Release builds fail with MSB3027 file locks — the UI
project alone in Debug is fine for this task: dotnet build src\ZenVizor.Ui -c Debug.)
```

---

## Suggested execution order

1. **P10** — shutdown fix + trace (unblocks the tray-freeze diagnosis already in progress).
2. **P1** — IPC hardening (the only security-relevant exposure).
3. **P2** — capture reliability (silent data loss).
4. **P3** — hot path (perf budget).
5. **P5 / P4** — storage + attribution (P4 before Phase 6 wires the alert).
6. **P6** — contract/test hygiene.
7. **P7 / P8 / P9** — UI polish, design sync, doc cleanup (any order).
