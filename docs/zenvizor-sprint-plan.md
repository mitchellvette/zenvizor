# ZenVizor — Sprint / Milestone Plan

**Project name:** ZenVizor (renamed from working title "TitaniRun" on 2026-06-01)
**Document type:** Phased build plan (companion to `zenvizor-prd.md`)
**Status:** Scoping complete
**Last updated:** 2026-06-01

> Full product spec — features, data model, architecture, IPC contract, data model, out-of-scope boundaries — lives in **`zenvizor-prd.md`**. This file is the build sequence and the QA gates.

---

## Standalone context (load-bearing facts repeated for use without the PRD)

- **What it is:** lightweight, **passive** Windows network monitor/reporter. Attributes up/down traffic to processes/services, stores history in SQLite, shows a near-live dashboard + daily reports. No firewall, no blocking.
- **Founding invariant (non-negotiable):** the app emits **zero network traffic of its own**. This is a *test gate* (Phases 3 and 6): point the tool at itself → it must report no outbound from its own processes. Any feature that would break this is out of scope.
- **Stack:** C# 14 / .NET 10 (`net10.0-windows`); WPF + WPF-UI + LiveCharts2; ETW via `Microsoft.Diagnostics.Tracing.TraceEvent` (`Microsoft-Windows-Kernel-Network`); IP Helper (`GetExtendedTcpTable`/`GetExtendedUdpTable`) for PID correction; SQLite; named pipes + StreamJsonRpc for IPC; WiX `.msi`; GitHub Actions CI.
- **Architecture:** elevated **Windows Service** (LocalSystem) does capture + owns the DB; non-elevated **WPF UI** displays data; they talk only over a **named pipe**. UI has no DB access.
- **CLI:** `zvctl` is the companion CLI client used for scripted/manual QA throughout.
- **Testing split:** core logic is headless-testable on CI via a **synthetic `ICaptureSource`**; **live ETW + installer + self-monitoring** are manual real-box gates (live kernel-ETW isn't reliable on CI runners).
- **The three seams (built during MVP, see PRD §6):** (1) `IMonitor` collector contract — Phase 1; (2) alert pipeline + `Alert` entity — Phase 6 (with one real alert wired); (3) versioned IPC envelope — Phase 3. The `devices` table is reserved in the schema.

---

## How to read this plan

Phases are **sequential**; each later phase assumes the prior phase's acceptance criteria passed. Every phase separates:

- **CI (headless)** criteria — automated, run in GitHub Actions.
- **Manual (your QA)** criteria — you personally verify on a real Windows box before advancing.

Do not advance past a phase until both checklists pass.

---

## Phase 0 — Scaffolding, CI, service skeleton

**Goal:** A buildable solution with all projects, CI green, an installable do-nothing service, a launchable UI, a working pipe handshake, and a migration runner that creates the DB.

**Scope**

- Solution + project layout per PRD §5.5; `.editorconfig`, analyzers, nullable enabled.
- GitHub Actions: restore/build/test on push; artifacts uploaded.
- Windows Service skeleton (installs, starts, stops, logs) — no capture yet.
- UI skeleton (WPF + WPF-UI shell, tray icon, empty nav) — launches.
- `Ipc.Contracts` + named-pipe server/client: **handshake + version negotiation only**.
- SQLite migration runner: creates full schema (PRD §7) incl. reserved `devices` table.
- `zvctl` skeleton that connects and calls a `Ping`/`GetServiceStatus` stub.

**Acceptance criteria — CI (headless)**

- [x] `dotnet build` and `dotnet test` pass in Actions on a clean checkout.
- [x] Migration runner creates a DB with all tables; a test asserts schema matches expected.
- [x] IPC contract test: client and server negotiate version in-process.

**Acceptance criteria — manual (your QA)**

- [x] Service installs, starts, stops, uninstalls via CLI; writes a startup log line.
- [x] UI launches, shows shell + tray icon, close-to-tray works, Exit quits.
- [x] `zvctl ping` round-trips over the real named pipe; unauthorized/unACL'd access is rejected.

---

## Phase 1 — Capture engine + attribution core (headless-first)

**Goal:** Correct per-process byte attribution from events to SQLite, fully testable on CI via synthetic events, then validated live.

**Scope**

- `IMonitor` contract (**seam #1**); capture is the first implementation.
- `ICaptureSource` abstraction with **two** implementations: real ETW (`TraceEvent`, `Microsoft-Windows-Kernel-Network`) and **synthetic/recorded**.
- IP Helper PID-correction layer (`GetExtendedTcpTable`/`GetExtendedUdpTable`).
- In-memory rolling aggregate keyed by session; flush (~5s) into 60s `traffic_samples` buckets; `connections` upserts.
- `process_sessions` lifecycle (PID start/end, reuse handling).
- Remote `Local`/`Wan` classification incl. IPv6 ranges.

**Acceptance criteria — CI (headless)**

- [x] Synthetic event streams produce **exact expected** `traffic_samples` and `connections` rows (deterministic).
- [x] PID-correction test: an event with wrong/missing PID is corrected to the table's owning PID.
- [x] IPv6 + WAN/local classification covered by tests (v4 RFC1918, v6 fe80::/10, fc00::/7, loopback).
- [x] Session reuse test: same PID reused for a new process yields a new session row.

**Acceptance criteria — manual (your QA)**

- [x] On a real box, generate known traffic from a known process; the DB attributes bytes to the correct PID within tolerance, and totals are sane vs. Resource Monitor.
- [x] Idle CPU **< 1%**, service working set **< ~80 MB** under light load.
- [x] No per-event DB writes observed (writes occur on the flush tick).

---

## Phase 2 — Attribution enrichment (svchost, signer, path)

**Goal:** Turn "svchost.exe / unknown" into actionable identity — the core of the product's value.

**Scope**

- svchost → **service-name** resolution (`QueryServiceStatusProcess` / WMI `Win32_Service`) into `hosted_services`; multi-service PIDs listed, **bytes not split**.
- Signer/publisher + `signature_status` via offline `WinVerifyTrust` (`WTD_REVOKE_NONE`); cached per `app_id`.
- `is_user_writable_path` heuristic (temp/AppData/user-writable).
- `apps` dedup on `(image_path, publisher)`.

**Acceptance criteria — CI (headless)**

- [x] Given fixture PIDs/paths, service resolution and dedup produce expected `apps`/`process_sessions` rows (service lookups mocked behind an interface).
- [x] Signature classifier maps known signed/unsigned/invalid fixtures to correct `signature_status`.
- [x] Path heuristic flags user-writable locations correctly.

**Acceptance criteria — manual (your QA)**

See `docs/phase-2-verification.md` for the full walkthrough.

- [x] Real svchost traffic resolves to named services (e.g., `Dnscache`, `Dhcp`), not bare `svchost.exe`; multi-service PIDs show the honest list. *(Walked 2026-06-01: 5 svchost PIDs with named services. Multi-service comma-join deferred to CI — no co-hosted svchost generates network traffic on this Win11 build; covered by `SessionTrackerTests.TryTrack_SvchostPid_PopulatesHostedServices`.)*
- [x] A known signed app shows its publisher; an unsigned binary run from `%TEMP%` shows `Unsigned` + user-writable flag. *(Walked 2026-06-01: Code/Discord/Chrome all `Signed` with correct publisher CNs; `Add-Type`-generated unsigned PE in `%TEMP%` correctly classified `Unsigned + is_user_writable_path=1`.)*
- [x] Enrichment does **not** raise idle CPU above budget (caching verified — no repeated `WinVerifyTrust` per event). *(Walked 2026-06-01: 0.08% avg, 1.55% max over 60s sample.)*

---

## Phase 3 — IPC data plane + near-live activity pane

**Goal:** The UI shows a near-live dashboard fed entirely over IPC from the in-memory aggregate; the versioned envelope seam is in place.

**Scope**

- **Seam #3:** versioned IPC envelope finalized.
- `GetCurrentActivitySnapshot()` served from in-memory aggregate; optional `ActivityTick` push.
- Dashboard / Current Activity view with LiveCharts2 (per-app up/down rates, top talkers).
- `zvctl` gains `snapshot` command.

**Acceptance criteria — CI (headless)**

- [x] Contract tests: snapshot request/response and envelope versioning round-trip in-process.
- [x] Snapshot is served from the in-memory aggregate (test asserts no SQLite read on the snapshot path).

**Acceptance criteria — manual (your QA)**

- [x] Generate live traffic; the dashboard reflects it with only minor delay/aggregation; rates match reality within tolerance. *(Gate #1 — verified by `scripts\verify-attribution.ps1`, 5/5 deterministic passes.)*
- [x] **Self-monitoring check:** with the tool running and the UI open, the tool reports **zero outbound** from its own service/UI processes (named-pipe IPC produces no network rows). *This is the founding-invariant gate.*
- [x] `zvctl snapshot` returns the same data the UI shows.

**Note:** Phase 3 also uncovered and fixed two attribution gaps that the original plan didn't anticipate: (1) short-lived processes lost image resolution post-exit — fixed via `ProcessLifecycleResolver` fed by kernel ETW `ProcessStart`/`Stop` events; (2) sub-second TCP connections lost receive-path attribution because the polled `GetExtendedTcpTable` snapshot missed them — fixed via `ConnectionLifecycleResolver` fed by kernel ETW `TcpIpConnect`/`Accept`/`Disconnect`. Both fixes use a 60 s grace cache for post-event resolution. New gate: `verify-attribution.ps1` exercises N curl downloads and asserts zero unattributed observations.

---

## Phase 4 — History tiers, query surface, per-app drill-down

**Goal:** Rollups, retention, the full query/reporting IPC surface, and the per-app → connections → history navigation.

**Scope**

- Hourly/daily rollup jobs from `traffic_samples`; retention/purge jobs per PRD §7.9.
- Query IPC: `GetAppList`, `GetAppDetail`, `GetConnections`, `GetTrafficHistory` (grain selection).
- UI: Per-App breakdown, App detail (connections + history), History/timeline with **user-defined window**.

**Acceptance criteria — CI (headless)**

- [x] Rollup correctness: sample fixtures roll up to exact hourly/daily totals.
- [x] Retention: rows older than configured windows are purged; newer retained; rollups preserved per policy.
- [x] User-defined-window query over fixtures returns exact expected totals at each grain.

**Acceptance criteria — manual (your QA)**

- [x] Per-app list totals reconcile with the daily numbers and with the live view over the same window.
- [x] Drill app → connections shows correct endpoints with local/WAN + protocol; drill → history series matches.
- [x] Changing the query window updates results correctly; large windows stay responsive (served from rollups).

Manual gates documented in `docs/phase-4-verification.md`. CI gates cover the
rollup-on-flush path, retention-by-tier deletion, and per-tier query
correctness via fixtures. All three manual gates passed on 2026-06-03 after
a UI-side perf round (DataGrid virtualization, chart point cap, persistent
IPC, duplicate-scan removal). 212 / 212 tests passing.

---

## Interlude — UI design polish (between Phase 4 and Phase 5)

Non-phase polish pass before Phase 5 adds new feature surface. Tightens the
visual language and resolves accumulated drift via a per-screen design loop
(findings doc → brief → Claude Design mock → XAML implementation →
per-screen verification). Lives outside the phased acceptance criteria; the
per-screen briefs in `docs/design-briefs/` and the design system in
`docs/design-system.md` (with verification gate at §10) are the contracts.

**Documentation follow-ups uncovered during this pass:**

- **No `ZenVizor.sln` exists.** Phase 0's "Solution + project layout per
  PRD §5.5" was only partially delivered — the repo builds via per-`.csproj`
  invocations and CI builds each project directly. `CLAUDE.md` references
  `dotnet build ZenVizor.sln` which fails today. Resolve either by adding a
  top-level `.sln` (preferred: makes IDE multi-project navigation work and
  matches the documented build command) or by updating `CLAUDE.md` to
  reflect per-project invocation. Deferred for a housekeeping pass —
  flagged here so it doesn't drop.

- **Hostname resolution (passive-DNS observer) — promoted into MVP.**
  Previously deferred as F2 in `docs/design-briefs/app-detail.md`; promoted
  during App Detail Phase 6 review (the user wanted hostnames so the
  Connections grid reads as "talked to **google.com**" instead of an
  opaque IPv4/IPv6 address that the user otherwise has to copy out and
  look up manually). PRD §7.4 already reserves the
  `connections.resolved_host` column for this. **Strictly passive** —
  source is the user's *existing* DNS traffic which ETW already observes,
  parsed to build an IP → hostname mapping. Active `Dns.GetHostEntry` /
  `Dns.GetHostAddresses` calls remain forbidden per CLAUDE.md invariant 1
  (the application emits ZERO network traffic). **Implementation scope:**
  ETW subscriber for DNS-protocol events (either the
  `Microsoft-Windows-DNSClient` provider or the UDP/53 response payloads
  from `Microsoft-Windows-Kernel-Network`) + DNS-response parser
  (RFC 1035) + storage wire-up to `connections.resolved_host` + IPC
  contract field (`ConnectionRow.ResolvedHost`) + UI column on App
  Detail's Connections grid (and likely Per-App's expanded view). Scope
  into a phase before MVP cut — flagged here so it doesn't drop.

- **App Detail — specific-calendar-day window mode (post-MVP follow-up).**
  App Detail today uses trailing rate-window presets (`1h / 24h / 7d /
  30d / 90d`). The Reports → drill question
  (`docs/design-briefs/findings/reports.md` §9F review, 2026-06-09)
  established that a richer-fidelity destination for "this app, on
  this specific day" — peak times, endpoints, sessions — lives in
  App Detail with a calendar-day window mode (rather than a trailing
  window). Phase 5 routes the Reports drill to **History**
  pre-filtered to `(app, date)` instead (History grows the filter
  capability per `docs/design-briefs/findings/history.md` F8). Adding
  a specific-day mode to App Detail proper is the natural follow-up
  once the History route is in: it gives the user a way to pivot
  from the History timeline view of `(app, date)` to App Detail's
  Connections / Sessions / chart depth scoped to the same day.
  Scope: App Detail picker grows a "specific date" option alongside
  the trailing-window presets; the chart's X-axis labels and Y-axis
  rate unit reflow to a 24-hour day; Connections and Sessions grids
  filter to events whose timestamps fall within the day. Deferred
  for a post-MVP pass — flagged here so it doesn't drop.

- **High Contrast runtime merge wiring not implemented.**
  `Resources/HighContrast.xaml` ships with full token collapses for every
  semantic surface, text, accent, status, border, chart, plus the polish
  round 2 additions (`metal.card`, `edge.light`, `shadow.card`) AND the
  Per-App round additions (`shadow.sm`, `surface.tooltip.scrim`,
  `metal.control`) — but is never merged into
  `Application.Current.Resources.MergedDictionaries` at runtime.
  `App.xaml.cs` has no subscription to
  `SystemParameters.StaticPropertyChanged` (or
  `SystemEvents.UserPreferenceChanged` with category `Color`). Result:
  when the user enables Windows HC, brand brushes from
  `BrandAccent.{Light,Dark}.xaml` stay active and the HC dictionary never
  wins resource lookup — brand violet, metal gradients, and shadows all
  keep rendering, so the visual treatment is wrong end-to-end. Tracked
  in `docs/design-system.md` §11 item 9 ("HC merge wiring") and called
  out in `docs/dashboard-UI-phase-plan.md` (lines 130-137) during
  Dashboard polish round 2: the static token audit was completed there,
  only the runtime merge remains. Per-App polish round (2026-06-06/07)
  re-validated the gap during the final HC gate. **Implementation
  scope**: subscribe in `App.OnStartup`, gate the merge on
  `SystemParameters.HighContrast`, and re-evaluate on the
  StaticPropertyChanged event. Merge LAST so HC keys win over
  `DesignTokens.xaml` + `BrandAccent.*.xaml`. Defer to a housekeeping
  pass — flagged here so it doesn't drop again.

---

## Phase 5 — Daily report + CSV/HTML export

**Goal:** The headline deliverable — a daily overview report, viewable in-app and exportable.

**Scope**

- `GetDailyReport(date)` structured payload (top apps, up/down totals, WAN vs local split, notable items e.g. new unsigned-from-temp talkers).
- In-app Daily Report view.
- CSV + HTML export (filesystem write from the UI side, user-chosen location).

**Acceptance criteria — CI (headless)**

- [ ] Report aggregation over fixtures yields expected totals/sections.
- [ ] CSV/HTML serializers produce well-formed output matching the report payload (snapshot-tested).

**Acceptance criteria — manual (your QA)**

- [ ] Daily report numbers reconcile with the history view for the same date.
- [ ] CSV opens cleanly in a spreadsheet; HTML opens in a browser; both match the in-app view.

### Follow-up: anchor-baseline insufficient-history guard (deferred from 5b)

The Reports page's "vs N-day average" hero deltas + Uncommon Talkers'
"× the 7-day median" reasons compare against a rolling baseline whose
length the user picks (7 / 30 / 90 days). On a fresh install — or on
any machine whose service has been running for fewer days than the
chosen anchor window — that baseline is a partial average; the deltas
and "Nx the median" multiples it produces aren't directly comparable
to a full-history machine's numbers and may misrepresent normal
usage as anomalous (or vice versa).

The aggregator already has the data needed to detect the shortfall:
the per-day baseline-row count it pulls for `LoadAnchorBaseline` and
for `LoadUnusualVolume`. The `UnusualVolumeMinBaselineDays = 4` guard
in `DailyReportRepository` already prevents that *category* from
false-firing — this follow-up extends the same idea to the hero
deltas and to the cross-page user expectation.

Pick one of two treatments at implementation time (both viable):

- **(a) Placeholder ("pending until X").** Compute the
  earliest-eligible date as `first_service_run + anchor_days`. Before
  that date, the delta chip + caption hide entirely; the hero shows
  totals only, and a small inline message reads "Comparisons unlock
  on {Date}." Same logic suppresses Uncommon Talkers' "Nx" phrasing
  in favor of "Today's volume: {bytes}." until the baseline matures.
- **(b) Warning banner.** Always show the comparison, but if the
  baseline window has insufficient history, paint a `status.caution`
  inline note (next to the anchor label) reading "Comparison based on
  {N} days of history; may not reflect typical usage." Lighter touch;
  delta chip stays visible.

(a) is the honest treatment when the baseline is genuinely too short
to be informative; (b) is the right call when there's *some*
baseline but it's known partial. A reasonable mid-ground: (a) for
<3 days of baseline history (no signal at all); (b) for 3-to-N
days (partial).

**Where the gap lives:** the service has no per-machine
"first-run" timestamp wired to this query yet. Either persist one
in the `settings` table at install time, or derive it from
`MIN(first_seen)` across the `apps` table (cheap, no migration).

**Status:** Noted 2026-06-10. Not blocking 5c/5d/5e. Implement as a
post-Phase-5 polish round or fold into Phase 6 alongside the Alerts
deep-link wiring.

---

## Phase 6 — Alert pipeline (first customer), Settings, tray polish, installer

**Goal:** Wire the alert seam with one real alert, finish Settings (incl. configurable autostart) and tray, and ship a clean .msi. This is the MVP-complete gate.

**Scope**

- **Seam #2:** alert pipeline + `Alert` entity + `GetAlerts`/`AcknowledgeAlert` + `AlertRaised` push + Alerts feed UI + optional toast.
- **First real alert:** *unsigned binary from a user-writable path making network connections* (purely local, no new monitor, no network). The remaining five `AlertType` enum values ship as vocabulary placeholders in Phase 6.0; **pre-MVP backlog item P4 expands this to all six producers wired** — that gate must close before the installer freeze.
- Settings: **autostart toggle** (service start mode incl. "off" for fast-boot users), retention windows, purge history, flush/bucket intervals, toast toggle, theme.
- Tray polish; close-to-tray/Exit finalized.
- **WiX .msi:** installs/registers the service with the chosen start mode, installs the UI, sets DB ACLs and `%ProgramData%\ZenVizor\` layout; clean uninstall; `wix build` CLI-drivable in CI.
- **Reports → Alerts deep-link wiring (deferred from Phase 5 Reports implementation).**
  Reports' "Notable today" incident cards render a visible-but-inert `Alerts · #N`
  chip linking each item to its corresponding `Alert` (see
  `docs/design-briefs/reports.md` §16.2 and the mockup hand-off). Phase 5 cannot
  wire the navigation because the Alerts page is still a Group-B placeholder
  and the alert pipeline (`Alert` entity, `GetAlerts`, alert IDs) lands here in
  Phase 6. **Deferral is necessary, not optional.** Action: when the Alerts page
  ships its real layout in this phase, wire the `Alerts · #N` chip Click on
  Reports to navigate to the Alerts feed, filtered/scrolled to the matching
  `Alert.Id`. Update Reports' code-behind in the same commit so the chip stops
  being inert.

**Acceptance criteria — CI (headless)**

- [ ] Alert rule test: fixture (unsigned, user-writable, has connections) raises exactly one correctly-typed alert; acknowledge flow tested.
- [ ] Settings persistence round-trips; purge invokes retention correctly.
- [ ] Installer builds in Actions and produces an `.msi` artifact.

**Acceptance criteria — manual (your QA)**

- [ ] Fresh `.msi` install on a clean machine: service registered with chosen start mode, DB created + ACL'd, UI present; **uninstall removes everything cleanly**.
- [ ] Toggling autostart **off** yields no service at boot (fast-boot path); **on** restores it.
- [ ] Triggering the unsigned-from-temp condition raises an alert in the feed (+ toast if enabled); acknowledge clears it.
- [ ] **Full-system pass:** run for a realistic period, then verify — attribution sane (incl. svchost service names), live + history + daily report reconcile, performance within budget, and the **zero-own-traffic invariant still holds** end-to-end.

---

## Phase 6 closeout — remaining work and ordering

Phase 6.0–6.7 landed end of 2026-06-17. Six producers wired,
zvctl `alerts` subcommands shipped, Settings UI with the three
threshold knobs hot-reloads via `CachedAlertSettingsLookup`, IPC
schema at `Settings v3 / Alerts v1 / Query v1 / DailyReport v2 /
ActivitySnapshot v2`, 459 headless tests pass. Last commit: `c6cf1cc`.

Phase 6.8 is split for sequencing — see the rationale in the
2026-06-18 chat thread (TL;DR: avoid two manual MSI install/uninstall
QA passes by deferring the acceptance gates until the polish items
have landed). **Mandatory order:**

> **Phase 6.8a → Pre-MVP polish (P2) → Pre-v1 follow-ups (A1, A2) → Phase 6.8b → Phase 7 → Phase 8.**

P1 closed in 6.5. P4 closed in 6.7. P2/A1/A2 closed 2026-06-18.
P3 + P5 were promoted into full phases (Phase 8 and Phase 7
respectively, see below) when the single-fix sketches turned out to
be known-incomplete in both cases.

### Phase 6.8a — installer scaffolding (NEXT)

**Goal:** Land the WiX project + CI build step so every push
produces an `.msi` artifact. Headless-only — defer human
install/uninstall QA to 6.8b.

**Scope**

- `installer/` directory (referenced in CLAUDE.md, not yet created).
  `ZenVizor.wixproj` + `ZenVizor.wxs` authored against current
  binary layout (`src/ZenVizor.Service/bin/Release/`, `src/ZenVizor.Ui/bin/Release/`,
  `src/ZenVizor.Cli/bin/Release/`).
- Service registration via the WiX `util:ServiceConfig` /
  `ServiceInstall` elements (same SC entries `install-dev.ps1`
  produces today — start mode demand by default).
- ACL setup for `%ProgramData%\ZenVizor\` (SYSTEM + Administrators
  full, INTERACTIVE none — `%ProgramData%\ZenVizor\` itself is the
  thing the UI must NOT have direct DB access to; the IPC named
  pipe is the only INTERACTIVE-readable surface).
- Upgrade rules (major-upgrade pattern, `UpgradeCode` GUID locked).
- Uninstall cleanup — service stops + uninstalls; `%ProgramData%\ZenVizor\`
  preserved by default (data) with optional `REMOVE_DATA=1` property
  to wipe. Mirror of `uninstall-dev.ps1 -PurgeData`.
- Start menu shortcut for `ZenVizor.Ui.exe`.
- `.github/workflows/installer.yml` (or extend the existing CI workflow):
  `wix build installer/ZenVizor.wixproj` runs on every push; MSI
  artifact uploaded.

**Acceptance criteria — CI (headless)**

- [x] `wix build installer/ZenVizor.wixproj -c Release` succeeds locally
      (no errors, MSI produced). *(verified 2026-06-18, 759cb90)*
- [ ] `msiexec /i ZenVizor.msi /qn /log install.log` succeeds in a
      Windows Sandbox or CI runner. Service `ZenVizor` registers; the
      DB directory ACL matches the dev install. *(deferred to 6.8b — manual gate)*
- [ ] `msiexec /x ZenVizor.msi /qn /log uninstall.log` removes service,
      removes binaries, leaves `%ProgramData%\ZenVizor\` intact. *(deferred to 6.8b)*
- [ ] `msiexec /x ZenVizor.msi REMOVE_DATA=1 /qn` also wipes the data
      directory. *(deferred to 6.8b)*
- [x] CI workflow produces the MSI artifact on every push to `main`.
      *(landed 2026-06-18, 759cb90)*

### Pre-MVP polish items (between 6.8a and 6.8b)

- **P2** — Alerts nav badge isn't seeded with active alerts on launch.
  **(closed 2026-06-18 — c1473c7)**

P3 (reverse DNS) and P5 (.NET runtime prereq) were promoted to their
own phases — **Phase 8** and **Phase 7** respectively — when scoping
the polish backlog (2026-06-18 chat). The single-fix sketches the
briefs originally proposed were known-incomplete in both cases (the
DNS cache-read produces partial coverage; the registry-based runtime
check inspects a key that doesn't exist on machines with the runtime
actually installed). Doing each piece once, architecturally correctly,
beats shipping a partial fix in v1 and re-doing the work post-MVP.

### Pre-v1 architectural follow-ups (also between 6.8a and 6.8b)

- **A1** — Universal page-reactive `ServiceReconnected` event for
  the four `HistoryQueryClient`-holding pages (Scope 2 of Phase 6.1a).
  **(closed 2026-06-18 — 6a724aa)**
- **A2** — Centralize query clients at app scope (Scope 3 of Phase
  6.1a). Lands cleanly after A1. **(closed 2026-06-18 — 26e9a8c)**

A1 landed before A2 — A2's centralization removed the per-page
reconnect handlers A1 introduced, so the order avoided writing and
immediately deleting code.

### Phase 6.8b — manual acceptance gates (after polish)

**Goal:** Run the human-QA gates against the most-MVP-like build —
all polish items already in.

**Scope**

- Fresh MSI install on a clean Windows machine (the project has a
  dedicated test box; psexec is the alternative if a clean VM
  isn't available).
- Toggling autostart off → service does not start at boot →
  toggling back on restores it.
- Trigger each of the six alert types per the gates in
  `docs/phase-6.7-verification.md`; confirm desktop notification +
  nav badge updates fire for each.
- **Self-monitoring zero-own-traffic gate (invariant #1 acceptance):**
  run ZenVizor pointed at itself for a realistic period (≥ 1 hour).
  Verify that `apps` does not contain `ZenVizor.Service.exe` or
  `ZenVizor.Ui.exe` with any outbound bytes; `connections` table
  contains no rows attributed to either PID; CaptureStats
  `ObservationsUnattributed` does not include traffic-emitting events
  attributed to the ZenVizor process tree.
- Full-system pass: attribution sane (incl. svchost service names),
  live + history + daily report reconcile, performance within budget
  (idle CPU < 1%, service working set < ~80 MB).
- Clean uninstall.

**Acceptance criteria — manual**

The full list in the Phase 6 acceptance block above is the canonical
checklist. The split just defers when it runs.

---

## Pre-MVP polish backlog

UI / visual polish surfaced during Phase 6 hands-on QA. Smaller-than-A1/A2
in surface area but ship-blockers for the 1.0 feel. Address before the
installer freeze.

### P1 — Amber / caution status banners regressed in dark mode

**Discovered:** Phase 6.2 manual validation, 2026-06-16.

**The problem:** The amber (caution) status banners that appear across
multiple pages render with poor contrast in dark mode — the foreground
text is difficult to read against the tinted background. Affects every
page that surfaces a caution banner, not just Settings.

**Fix path:** Re-audit the `status.caution.background` /
`status.caution.text` (and `status.caution`) token pairs in
`src/ZenVizor.Ui/Resources/DesignTokens.xaml` and `HighContrast.xaml`
against the dark ramp. Verify WCAG AA contrast for body text on the
tinted background in both light and dark. Update
`docs/design/colors_and_type.css` + the crosswalk in the same commit
per the design-system rule in CLAUDE.md.

**Estimated effort:** Small — token tweak + visual sweep.

### P2 — Alerts nav badge isn't seeded with active alerts on launch

**Discovered:** Phase 6.2 manual validation, 2026-06-16.

**The problem:** `MainWindow` only mutates the nav-rail badge counters
on `AlertRaised` push events (`MainWindow.xaml.cs:36-43` documents the
drift envelope). Alerts that were already Active in the DB at app launch
do not appear in the badge until the user navigates to the Alerts page,
which is what authoritatively calls `UpdateAlertsBadge`. Result: launch
into an app with active alerts and the nav rail reads zero.

**Fix path:** On `MainWindow.OnLoaded`, kick a one-shot fetch of the
active-alert counts (either reuse `AlertsClient.GetAlertsAsync(State=Active)`
and aggregate per-severity, or add a small `GetActiveAlertCountsAsync`
IPC that returns per-severity counts cheaply). Seed
`_badgeCritical / _badgeWarning / _badgeInfo` from the result before any
push arrives, then call `RenderBadgeFromLocalCounts`. Aligns with the
A2 follow-up's eventual count-summary projection — this is the cheap
pre-A2 patch.

**Estimated effort:** Small — one IPC fetch on Loaded + one render call.

### P3 — Reverse DNS for endpoint columns on Per-App / drill-down

**Superseded by Phase 8 — see below.** Promoted into a full phase
on 2026-06-18 when reviewed against the Interlude entry "Hostname
resolution (passive-DNS observer) — promoted into MVP" (above): the
DNS-cache-read approach P3 originally proposed produces only partial
coverage (the Windows DNS cache evicts entries after ~30 min, so
connections older than that lose resolution) and would need to be
replaced by the ETW passive-DNS observer anyway. Doing the ETW path
once — architecturally correct, full coverage, populates
`connections.resolved_host` from the same DNS-response stream the
user's other processes already trigger — is strictly cleaner than
shipping a partial fix in v1 and re-doing the work post-MVP.

### P4 — Wire the remaining five alert producers

**Discovered:** Phase 6.6 manual validation, 2026-06-17 — `zvctl alerts
catalog` surfaced that 5 of 6 enum values were vocabulary placeholders
with no `IAlertRule` implementation behind them. The original Phase 6
scope locked the MVP gate to "**one** real alert wired"
(`UnsignedFromUserPath`); on review, MVP should ship the full catalog,
not a single producer plus five labels for post-MVP wiring.

**The problem:** The `AlertType` enum, the `IAlertRule` /
`AlertProducer` plumbing, the persisted `alerts` schema, the UI feed,
the why-copy lookup, and the catalog vocabulary all already cover six
types. Only one — `UnsignedFromUserPath` — has a producer registered
with the host service. The other five (`InvalidSignature`,
`FirstRunWanTalker`, `UnusualDailyVolume`, `LargeDownload`,
`OutboundHeavy`) never raise. The architecture seam is the entire
point — five empty slots cost almost nothing because the producer
iterates `IEnumerable<IAlertRule>` and the IPC / UI surfaces are
type-agnostic; the cost is just writing each rule's evaluation
predicate + cooldown + detail-render against the existing
`NewSessionContext` / rollup feed.

**Per-type implementation notes:**

1. **`InvalidSignature`** — Critical, `SourceMonitor.Capture`. Mirror
   of `UnsignedFromUserPathRule` but predicate fires on
   `signature_status = Invalid`, not `Unsigned + user-writable`.
   Entity = App. 24 h cooldown per rule. Reuses the same
   `NewSessionContext` and detail-render scaffolding.
2. **`FirstRunWanTalker`** — Info, `SourceMonitor.Capture`. Predicate:
   `app.first_seen_unix_ms` within last N seconds (proposed 60 s)
   AND a WAN-class session opened. Entity = App. Cooldown = forever
   per app (one-shot per first-seen). Needs `first_seen_unix_ms`
   plumbed onto `NewSessionContext` (currently the producer has
   `IsUserWritablePath` + `SignatureStatus` + `WanConnection` but no
   first-seen anchor — small additive change to the context shape).
3. **`UnusualDailyVolume`** — Warning, `SourceMonitor.Rollup`. Runs on
   the daily-rollup tick, not per-session. Predicate: app's
   day-bucket bytes ≥ median(last 14 days) + k·MAD(last 14 days)
   with k tunable (proposed k=4). Entity = App. One raise per app per
   day. Needs a `IRollupAlertSink` parallel to the existing per-event
   sink so `Rollup`-source rules get their own evaluation hook on
   day-roll. Storage repo already carries the data needed
   (`daily_app_traffic` rows).
4. **`LargeDownload`** — Info, `SourceMonitor.Capture`. Predicate: a
   single connection's accumulated `bytes_down` crosses a threshold
   (proposed 50 MB) within a sliding window (proposed 60 s). Entity =
   Session. Cooldown = 24 h per (app, remote_class) so chronic
   high-download apps (cloud sync, Steam) don't spam. Needs a
   per-connection bytes accumulator the producer can subscribe to
   (the aggregator already keeps `ConnectionAcc`; the rule reads
   from it on each flush).
5. **`OutboundHeavy`** — Warning, `SourceMonitor.Capture`. Predicate:
   over the last N minutes, an app's outbound bytes ≥ R × inbound
   AND outbound ≥ floor (proposed R=3, N=15, floor=10 MB). Entity =
   App. 24 h cooldown. Reads from the same aggregator state
   `LargeDownload` does; predicate fires on flush rather than on
   session-open.

**Shared architecture work:**

- Extend `NewSessionContext` (or add a parallel `RollupContext` for
  Rollup-source rules) so first-seen and rollup row data flow in.
- Add a producer entry-point for Rollup rules — `OnDailyRollupTick`
  parallel to `OnSessionConnectedWan` — that iterates the
  `IRollupAlertRule` set after the rollup repo commits.
- Each new rule lands a unit test in `ZenVizor.Core.Tests` per the
  `UnsignedFromUserPathRule` test precedent.
- Update the `zvctl alerts catalog` Producer column markers when
  each rule lands. (`Program.cs:GetCatalogEntry` is the single
  source for the wired flag — five `false` → `true` flips total.)

**Why before MVP:** "One alert wired, five vocabulary placeholders"
is a soft posture for a first release of an alerting product —
demoware-shaped rather than usable. The seam is already paid for;
the rules are individually small (each ~50-100 LOC + a test); and
without them the user-visible value of the Alerts surface is one
alert type's worth of signal. Ship the catalog the catalog page
advertises.

**Estimated effort:** Medium overall — small per rule, plus the
Rollup hook plumbing once. Per-rule budgets: `InvalidSignature`
~2 h (clone of Unsigned), `FirstRunWanTalker` ~3 h (context shape
change), `LargeDownload` + `OutboundHeavy` ~4 h each (aggregator
accumulator integration), `UnusualDailyVolume` ~6 h (rollup hook +
median+MAD test surface). Call it ~3 days end-to-end including
tests + catalog-page wiring updates.

### P5 — .NET 10 desktop runtime prerequisite not handled by the MSI

**Superseded by Phase 7 — see below.** Promoted into a full phase
on 2026-06-18 when the brief's recommended approach was reviewed
against reality on the dev box. The launch-condition treatment
P5 originally recommended (treatment (b)) inspects the registry key
`HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App`
for a 10.x entry — but that key **does not exist on machines that
have the .NET 10 desktop runtime actually installed and working**.
On the dev box, with `dotnet --list-runtimes` showing
`Microsoft.WindowsDesktop.App 10.0.8`, the only subkey under
`InstalledVersions\x64` is `sharedhost\Version`. A
RegistrySearch-based launch condition built against the documented
path would therefore block legitimate installs.

Detecting the runtime reliably needs either Burn's canonical detection
logic (Microsoft-blessed, maintained against future installer
changes), or a brittle file-system probe under
`[ProgramFiles64Folder]dotnet\shared\Microsoft.WindowsDesktop.App\10.*`.
Burn is also the right answer for the user-facing install UX —
a single `ZenVizorSetup.exe` that installs the runtime if missing and
then runs the MSI, embedded for offline installability. Phase 7 below
is that work, scoped end-to-end.

---

## Pre-v1 architectural follow-ups

Findings surfaced during Phase 6 implementation that should land before v1
ships. Not detection-model limits (those live in
`docs/threat-model-limits.md`) — these are architectural cleanups that
make the existing surface more responsive or extensible.

### A1 — Universal page-reactive `ServiceReconnected` event (Scope 2 of Phase 6.1a)

**Discovered:** Phase 6.1a manual validation, 2026-06-15.

**The problem:** When the ZenVizor service restarts, every data page that
holds a `HistoryQueryClient` (HistoryPage, ReportsPage, PerAppPage,
AppDetailPage) ends up with a stale pipe handle. The first `RefreshAsync`
call after restart hits the stale handle, fails with `IsConnectionLost`,
resets the client, throws — and the page paints a Disconnected banner.
The SECOND `RefreshAsync` would succeed, but no second refresh fires
until the user navigates away and back. So every data page exhibits the
same "banner sticks until user re-navigates" UX as Alerts did before
Phase 6.1a fixed it.

**What Phase 6.1a Scope 1 did:** Added `MainWindow.ServiceReconnected`
event fired on disconnected→connected transitions, with MainWindow
force-reconnecting the shared `AlertsClient` and the AlertsPage
subscribing to refresh. Other pages can subscribe to the same event with
no additional MainWindow plumbing.

**What's left for Scope 2:** Each of the four `HistoryQueryClient`-holding
pages needs:
1. A `ForceReconnectAsync` on `HistoryQueryClient` (one-time shared
   change — same `ResetAsync` + `EnsureProxyAsync` shape as
   `AlertsClient.ForceReconnectAsync`).
2. A subscription to `ServiceReconnected` in each page's `OnLoaded`
   handler. The handler calls `_client.ForceReconnectAsync()` then
   `RefreshAsync()`.

**Estimated effort:** Small — five files, one new method on
`HistoryQueryClient`, one handler per page. The event scaffolding is
already in place.

**Why before v1:** Service restart is a routine event (Windows updates,
manual restart, install/uninstall). Every restart currently degrades the
UX across four data pages. v1 should feel responsive when the user
restarts the service from the Settings panel (which lands in Phase 6.2).

### A2 — Centralize query clients at app scope (Scope 3 of Phase 6.1a)

**Discovered:** Phase 6.1a manual validation, 2026-06-15.

**The problem:** Each data page constructs its own `HistoryQueryClient`
instance (`private readonly HistoryQueryClient _client = new();`). Four
pages, four connections to the same named pipe. Each one independently
holds its own pipe handle, signs up for the same negotiation, gets
torn down and rebuilt on page unload/load. The pages reinvent
reconnect handling, banner state, and refresh lifecycle separately.

The `AlertsClient` is already a singleton owned by `MainWindow`
specifically because it owns a push subscription that has to outlive
page navigation. The same lifetime extension is a quieter win for the
read-only query clients: a single shared instance survives nav, retains
the connection across page switches, and lets MainWindow's
`ServiceReconnected` event drive a single force-reconnect that all
consumers see.

**What Scope 3 entails:**
1. Move `HistoryQueryClient` ownership to `MainWindow` as a single
   instance (or a small `IQueryClientProvider` service that pages
   resolve).
2. Pages take a reference instead of constructing their own.
3. Single `ForceReconnectAsync` call in `MainWindow.OnStatusChanged`
   covers every page.
4. Remove the per-page reconnect handling Scope 2 adds. Reconnect logic
   lives in exactly one place.

**Estimated effort:** Medium — refactor surface across ~6 files plus
tests. Worth scoping after Scope 2 ships because Scope 2 is the
incremental win that buys the time to do Scope 3 right.

**Why before v1:** The current pattern is fine for four pages but
multiplies if a future page (e.g., a post-MVP host view or forensics
view) needs the same data. The refactor is much easier now (six call
sites) than later (eight, ten, twelve).

**Scope 3 also unblocks:** the count-summary IPC payload that lifts the
nav-badge accuracy gap from Phase 4b — once `MainWindow` is the owner
of an authoritative query path, periodic background polling of
`GetAlertsAsync(State=Active)` for badge state becomes a clean addition
rather than a layering violation.

---

## Phase 7 — Burn bootstrapper (MSI + .NET 10 runtime)

**Goal:** Ship `ZenVizorSetup.exe` as the canonical install artifact —
a single double-click that detects the .NET 10 desktop runtime,
installs it if missing, and runs the Phase 6.8a MSI. Closes the
runtime-prereq gap that P5 exposed without committing to a brittle
registry-check workaround.

**Scope**

- `installer/Bundle/ZenVizor.Bundle.wixproj` + `ZenVizor.Bundle.wxs`
  alongside the existing MSI wixproj. WiX `<Bundle>` element with
  `WixToolset.Bal.wixext` for the default UI (pin to 6.0.1 — the
  MIT line; do **not** advance to 7+ per the OSMF licensing
  constraint already documented in `Directory.Packages.props`).
- **Embed** the .NET 10 Desktop Runtime `.exe` payload inside the
  bundle (`<ExePackage Cache="keep" Vital="yes">` referencing the
  redist binary committed to or fetched at build time into
  `installer/Bundle/payloads/`). Embedded > chained: ZenVizor is a
  network monitoring tool whose target audience includes restricted
  environments. The ~50–60 MB size increase is the right tradeoff
  for offline installability; avoids dependency on a Microsoft
  download URL surviving across the product's lifetime.
- Detect-existing logic via `<bal:WixStandardBootstrapperApplication>`
  + Burn's canonical `<ExePackage DetectCondition>` shape — Burn
  ships with its own runtime-detection logic that's maintained
  against future installer-layout changes. We do NOT roll our own
  RegistrySearch.
- Bundle UpgradeCode locked the same way the MSI's is. The bundle
  major-upgrade pattern supersedes the MSI's; both versions advance
  in lockstep with the product `<Version>` in `Directory.Build.props`.
- CI workflow `.github/workflows/ci.yml` extension: after the
  existing `dotnet build installer/ZenVizor.wixproj` step, also
  `dotnet build installer/Bundle/ZenVizor.Bundle.wixproj` and upload
  `ZenVizorSetup.exe` as a separate artifact alongside the bare MSI.
- Update the project README + any operator docs that still tell
  users to install .NET first.

**Acceptance criteria — CI (headless)**

- [ ] WiX Bal extension 6.0.1 confirmed MIT-licensed (no OSMF
      flip mid-build).
- [ ] `dotnet build installer/Bundle/ZenVizor.Bundle.wixproj -c Release`
      succeeds locally and in CI; produces `ZenVizorSetup.exe`
      ≤ ~120 MB (45 MB MSI + ~60 MB embedded runtime).
- [ ] CI uploads `ZenVizorSetup.exe` on every push to `main`.
- [ ] Bundle UpgradeCode locked and pinned via the
      `installer/Bundle/ZenVizor.Bundle.wxs` source.

**Acceptance criteria — manual (human QA — clean Windows Sandbox)**

- [ ] Windows Sandbox **without .NET 10 desktop runtime pre-installed**:
      `ZenVizorSetup.exe` detects the missing runtime, installs it,
      then installs ZenVizor. End state: service running, UI launches,
      Add/Remove Programs shows ZenVizor.
- [ ] Windows Sandbox **with .NET 10 already installed**: `ZenVizorSetup.exe`
      detects the runtime and skips the runtime install; MSI install
      proceeds.
- [ ] Uninstall via Add/Remove Programs leaves the .NET 10 runtime in
      place (it is shared with other apps) — only the ZenVizor MSI
      payload is removed. `%ProgramData%\ZenVizor\` preserved by default.
- [ ] Bundle reinstall over a prior version upgrades cleanly (major
      upgrade) without two side-by-side entries.

**Sequencing note:** Phase 7 runs after Phase 6.8b manual gates. 6.8b
validates the MSI on its own (user pre-installs .NET 10 on the test
box). Phase 7 then adds the bootstrapper layer on top and validates
the chained-install UX in Sandbox.

---

## Phase 8 — ETW passive DNS observer + hostname resolution

**Goal:** Populate the reserved `connections.resolved_host` column
from passively-observed DNS-response traffic — strictly invariant-#1
safe — so AppDetail's Connections grid reads "talked to
**outlook.office.com**" instead of an opaque IPv6 string. Closes
the gap P3 sketched without shipping the partial-coverage cache-read
workaround.

The Interlude entry "Hostname resolution (passive-DNS observer) —
promoted into MVP" (above, ~line 195) is the canonical scope brief
authored when this work was promoted into MVP. Phase 8 is the
sequenced delivery of that brief.

**Scope**

- New `IMonitor` implementation: `PassiveDnsMonitor` (or a second
  `ICaptureSource` if the seam shape calls for it — review at
  implementation time). Strictly passive — observes existing
  DNS-response traffic on the host; never originates a query.
- ETW provider selection: prefer `Microsoft-Windows-DNS-Client`
  (high-level, parsed records straight from the resolver). Fall back
  to `Microsoft-Windows-Kernel-Network` UDP/53 payloads + an RFC 1035
  parser if the high-level provider is unreliable on the project's
  target SKUs (Win 10/11 Home + Pro). Decision gate: a 30-min smoke
  on the dev box exercising `dotnet --list-runtimes` style traffic
  + `dig`/`nslookup` mix; if the high-level provider returns all
  expected records (incl. CNAME chains, AAAA), use it. If gaps,
  fall back to UDP/53.
- In-memory IP → hostname store with TTL respect + CNAME chain
  resolution (most-specific name wins; honour the response's TTL,
  expire on tick). Bounded LRU cap (proposed 64k entries — same
  order-of-magnitude as the Windows DNS cache) to prevent memory
  growth on hosts with long-running services that talk to many
  endpoints.
- Storage wire-up: at flush time, before writing `connections` rows
  the aggregator looks up `remote_addr` in the store and populates
  `resolved_host` when present. Existing rows stay null (no
  backfill — we don't have historical DNS data, see "Storage
  backfill" below).
- IPC schema bump: `IpcSchemaVersion.Query` v1 → v2.
  `ConnectionRow.ResolvedHost` new optional field. Negotiation path
  already handles older clients (server returns v2 envelope; v1
  clients ignore the unknown field per the additive-tolerance rule).
- UI: `AppDetailPage.xaml` Connections grid's "Remote endpoint"
  column renders `ResolvedHost` when non-null with the raw address
  as a smaller-font subscript (mockup-style); falls back to raw
  address when null. Per-App expanded view gets the same treatment
  if/when it surfaces connections directly.
- Self-monitoring invariant gate: the new ETW subscriber is in the
  capture path. Phase 6.8b's "zero own traffic" check (already in
  the canonical manual gate list) continues to verify that ZenVizor
  PIDs emit no outbound traffic; Phase 8 expands the code surface
  the gate covers but the gate itself doesn't change.

**Acceptance criteria — CI (headless)**

- [ ] Synthetic DNS-response fixture stream (hand-crafted RFC 1035
      payloads incl. A, AAAA, CNAME chain) feeds `PassiveDnsMonitor`'s
      `ICaptureSource`-equivalent and produces exact-expected IP →
      hostname entries in the store. Determinism gate per the
      "synthetic events assert exact rows" rule (CLAUDE.md "Testing
      conventions").
- [ ] TTL expiry test: an entry past its response TTL is evicted on
      the next tick; a new lookup for the same IP returns null until
      a fresh response arrives.
- [ ] CNAME chain test: a response with a CNAME chain
      (e.g., `outlook.office.com` → `outlook.office365.com.s-0001.s-msedge.net`
      → A record) resolves to the most-specific user-facing name in
      the store.
- [ ] Storage wire-up: aggregator + DNS store produces the expected
      `connections.resolved_host` value at flush, given a fixture
      session whose `remote_addr` matches a fixture DNS A record.
- [ ] IPC contract test for the v2 envelope round-trips
      `ResolvedHost` for present + null cases.
- [ ] LRU bound test: store at capacity drops oldest entries; never
      grows unbounded.

**Acceptance criteria — manual (human QA — real Windows box)**

- [ ] Run ZenVizor for ≥ 30 min of normal use. AppDetail Connections
      grid on a known app (e.g., browser) shows hostname resolution
      for at least the most-recent connections; verify the displayed
      hostname against the user's actual DNS history (browser dev
      tools, `Get-DnsClientCache`).
- [ ] IPv6-heavy app (`outlook.office.com`, `*.cdn.cloudflare.net`)
      renders human-readable hostnames rather than long hex-colon IPs.
- [ ] **Self-monitoring zero-own-traffic gate** (invariant #1 — same
      gate as 6.8b, run again to verify the new ETW subscriber adds
      no outbound). ZenVizor PIDs continue to attribute zero outbound
      bytes; no rows in `connections` belong to either ZenVizor PID.
- [ ] Performance: idle CPU < 1%, service working set < ~80 MB still
      hold with the DNS subscriber active. DNS-response traffic is
      low-rate so this should be uneventful, but it's worth verifying
      end-to-end.

**Storage backfill (deferred):** Existing `connections` rows stay
null. Back-population would require historical DNS data we don't
have. New rows post-Phase-8 install populate normally. This is a
documented "you see hostnames starting from when Phase 8 lands"
property of the rollout.

**Sequencing note:** Phase 8 runs after Phase 7. The order isn't
strictly mandatory — installer and capture surface are orthogonal —
but Phase 7's bundle has to pack the Phase 8 service binaries, so
Phase 8 first means re-cutting the bundle. Phase 7 first lets the
installer artifact stabilise before the capture surface grows.

---

## MVP definition of done

All Phase 0–8 acceptance criteria pass (CI + manual). The product: passively captures up/down traffic; attributes it to process incl. svchost service names and signer/path enrichment; shows a near-live dashboard; stores tiered history with user-defined windows and configurable retention; produces a daily report with CSV/HTML export; raises local alerts via an extensible pipeline; **resolves remote endpoint addresses to hostnames** from passively-observed DNS traffic (Phase 8); **installs via a single `ZenVizorSetup.exe`** that bundles the .NET 10 desktop runtime for offline installability (Phase 7); uninstalls cleanly; runs within the performance budget; and **emits no network traffic of its own**, verified by self-monitoring. The collector contract, alert pipeline, and versioned IPC envelope seams (plus the reserved `devices` table) are in place so the post-MVP modules in PRD §10 can be added without re-architecting.
