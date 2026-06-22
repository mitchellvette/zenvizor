# ZenVizor — Sprint / Milestone Plan

**Project name:** ZenVizor (renamed from working title "TitaniRun" on 2026-06-01)
**Document type:** Phased build plan (companion to `zenvizor-prd.md`)
**Status:** Scoping complete
**Last updated:** 2026-06-20

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

- **High Contrast runtime merge wiring — RESOLVED in Phase 6.5.**
  `App.xaml.cs` subscribes to `SystemParameters.StaticPropertyChanged`
  and `RefreshHighContrastMerge()` adds/removes `HighContrast.xaml`
  from `MergedDictionaries` based on `SystemParameters.HighContrast`,
  merged LAST so HC keys win over `DesignTokens` + `BrandAccent`. See
  `src/ZenVizor.Ui/App.xaml.cs:118-134` and `RefreshHighContrastMerge`
  at line 315. Entry retained as a stale-fact correction during the
  Phase 9 housekeeping pass (2026-06-20).

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

> **Phase 6.8a → Pre-MVP polish (P2) → Pre-v1 follow-ups (A1, A2) → Phase 6.8b → Phase 7 → Phase 8 → Phase 9.**

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
  `WixToolset.Bal.wixext` for the default UI. Pin to 6.0.1 across
  SDK, Util, Bal, NetFx. **Correction (2026-06-18, during Phase 7
  implementation):** an earlier rev of this brief said "pin to
  6.0.1 — the MIT line." That was wrong — all WiX 6.0.x packages
  ship under the Open Source Maintenance Fee Agreement (the source
  is OSI/MS-RL; the fee is on binary releases and applies only to
  revenue-generating use). ZenVizor's current non-revenue use is
  exempt; same practical outcome. Full rationale and the rule for
  revisiting if ZenVizor ever monetises lives in
  `Directory.Packages.props` (Installer (WiX) group).
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

- [x] WiX 6.0.1 licensing posture confirmed: SDK/Util/Bal/NetFx all
      OSMF-on-binary, source MS-RL, ZenVizor's non-revenue use exempt.
      (Re-stated; brief originally said "MIT" which was incorrect —
      see scope note above and `Directory.Packages.props` for the
      revisit rule.) *(verified 2026-06-18)*
- [x] `dotnet build installer/Bundle/ZenVizor.Bundle.wixproj -c Release`
      succeeds locally; produces `ZenVizorSetup.exe` ≤ ~120 MB
      (actual: ~101 MB = 45 MB MSI + ~60 MB embedded runtime).
      *(verified 2026-06-18 — clean build, both payloads embedded)*
- [x] CI uploads `ZenVizorSetup.exe` on every push to `main`.
      *(workflow extension landed 2026-06-18 — `Build installer bundle`
      step in `.github/workflows/ci.yml`)*
- [x] Bundle UpgradeCode locked and pinned via the
      `installer/Bundle/ZenVizor.Bundle.wxs` source.
      *(UpgradeCode `A696D724-25F7-4277-AC10-897408A34C83`, distinct
      from the MSI's, locked at first build.)*

**Acceptance criteria — manual (human QA — clean Windows VM)**

All four gates walked end-to-end on 2026-06-20 against bundle v0.1.1.
Test environment was VirtualBox 7.2.10 + Win11 Enterprise 25H2 eval ISO
(Sandbox was unavailable on the dev box's Win11 Home SKU — the
`Containers-DisposableClientVM` feature isn't in the catalog). Full
walkthrough, command sequences, and findings live in
`docs/phase-7-verification.md`.

- [x] Gate 1: clean VM **without .NET 10 desktop runtime pre-installed**:
      `ZenVizorSetup.exe` detects the missing runtime, installs it,
      then installs ZenVizor. End state: service RUNNING, UI launches,
      Add/Remove Programs shows ZenVizor + both runtime components.
- [x] Gate 2: clean VM **with .NET 10 already installed**: `ZenVizorSetup.exe`
      detects the runtime via `netfx:DotNetCoreSearch`, plans the
      runtime package as `execute: None`, skips it; only the MSI
      installs. Burn log confirms `Condition
      'WindowsDesktopRuntimeVersion >= v10.0.8' evaluates to true.`
- [x] Gate 3: Uninstall via Add/Remove Programs leaves the .NET 10
      runtime in place (`Permanent="yes"` honored); only the ZenVizor
      MSI payload is removed. `%ProgramData%\ZenVizor\` preserved by
      default. `REMOVE_DATA=1` opt-in correctly wipes data when passed
      either to the bundle or to `msiexec`.
- [x] Gate 4: Bundle reinstall over a prior version upgrades cleanly
      (major upgrade) — single ARP entry post-upgrade, old bundle
      `BundleProviderKey` removed, MSI swapped in lockstep, service
      stays RUNNING, data dir preserved.

**Findings landed during Phase 7 testing (see verification doc for
detail):**

- MSI wxs: `SetREMOVE_DATA_FOLDER` was scheduled too late in the
  InstallExecuteSequence; `WixRemoveFoldersEx` ran first and errored
  out on the missing property, silently breaking the documented
  `REMOVE_DATA=1` opt-in. Fixed by scheduling `After="LaunchConditions"`.
- Bundle wxs: `<MsiPackage Visible="no" />` added to suppress the inner
  MSI's duplicate ARP entry in Settings → Apps.
- Bundle wxs: `REMOVE_DATA` declared as `bal:Overridable="yes"` bundle
  variable + `MsiProperty` passthrough into the inner MSI, so the
  bundle's `/uninstall REMOVE_DATA=1` flow works end-to-end.
- Bundle wxs: `LogoFile` + `IconSourceFile` wired to existing brand
  assets (`assets/zv_logomark_v1.png`,
  `src/ZenVizor.Ui/Assets/favicon.ico`) — BA UI and ARP icon now show
  the ZenVizor logomark instead of WiX default CD icons.
- Project version bumped 0.1.0 → 0.1.1 in `Directory.Build.props`
  (Phase 7 delivery: branded installer, runtime detection, REMOVE_DATA
  passthrough). MVP versioning will be re-evaluated after Phase 8.

**Deferred items (documented in `docs/phase-7-verification.md`):**

- REMOVE_DATA user-choice checkbox in BA UI (requires custom Burn
  theme — half a day to a day of work).
- Runtime payload caching even when not installed (~60 MB on disk;
  `Cache="remove"` would reclaim it at the cost of repair-flow
  optionality).

**Sequencing note:** Phase 7 ran ahead of Phase 6.8b manual gates
because the bootstrapper bundle wraps the MSI; the four Phase 7 gates
exercise the MSI install/uninstall paths end-to-end (the bundle just
adds the runtime-chain on top). 6.8b's "MSI on its own with user pre-
installing .NET 10" is now redundant with Gate 2 above and can be
treated as closed by the Phase 7 walk.

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

- [x] Synthetic DNS-response fixture stream (hand-crafted RFC 1035
      payloads incl. A, AAAA, CNAME chain) feeds `PassiveDnsMonitor`'s
      `ICaptureSource`-equivalent and produces exact-expected IP →
      hostname entries in the store. Determinism gate per the
      "synthetic events assert exact rows" rule (CLAUDE.md "Testing
      conventions").
- [x] TTL expiry test: an entry past its response TTL is evicted on
      the next tick; a new lookup for the same IP returns null until
      a fresh response arrives.
- [x] CNAME chain test: a response with a CNAME chain
      (e.g., `outlook.office.com` → `outlook.office365.com.s-0001.s-msedge.net`
      → A record) resolves to the most-specific user-facing name in
      the store.
- [x] Storage wire-up: aggregator + DNS store produces the expected
      `connections.resolved_host` value at flush, given a fixture
      session whose `remote_addr` matches a fixture DNS A record.
- [x] IPC contract test for the v2 envelope round-trips
      `ResolvedHost` for present + null cases.
- [x] LRU bound test: store at capacity drops oldest entries; never
      grows unbounded.

**Acceptance criteria — manual (human QA — real Windows box)**

- [x] Run ZenVizor for ≥ 30 min of normal use. AppDetail Connections
      grid on a known app shows hostname resolution for at least
      the most-recent connections. *Walked 2026-06-21 with Steam,
      Outlook, Claude desktop as reference cases — all produced
      human-readable hostnames. Browser hit rate near zero owing
      to default DoH; see Known limitations subsection below.*
- [x] IPv6-heavy app (`outlook.office.com`, `*.cdn.cloudflare.net`)
      renders human-readable hostnames rather than long hex-colon IPs.
      *Walked 2026-06-21 with Outlook desktop — IPv6 endpoints
      rendered as `outlook.office.com`.*
- [x] **Self-monitoring zero-own-traffic gate** (invariant #1 — same
      gate as 6.8b, run again to verify the new ETW subscriber adds
      no outbound). ZenVizor PIDs continue to attribute zero outbound
      bytes; no rows in `connections` belong to either ZenVizor PID.
      *Walked 2026-06-21 — zero rows, invariant intact.*
- [x] Performance: idle CPU < 1%, service working set < ~80 MB still
      hold with the DNS subscriber active. *Walked 2026-06-21 — both
      well inside budget; second TraceEventSession cost negligible
      as design decision D5 predicted.*

**Status (2026-06-21):** **Closed with known gap.** All gates pass;
pipeline works end-to-end. Browser coverage is structurally zero
because of default DoH (Phase 8 ETW provider only sees the Windows
resolver). Documented in *Known limitation — DoH and in-app
resolvers* below; pre-MVP follow-up scoped as **Phase 8.5 —
Endpoint visibility investigation**. Verification doc at
`docs/phase-8-verification.md` carries the full walk + diagnostic
queries.

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

### Known limitation — DoH and in-app resolvers (discovered 2026-06-21)

**Discovered during the Phase 8 manual gate walkthrough:** the
hostname hit rate on real-world browsing was much lower than the
spec implied, dominated by **Chrome producing essentially zero
resolved hostnames** while non-Chrome apps (Steam, Claude desktop)
worked as designed.

**Root cause:** Chrome ships with DNS-over-HTTPS (DoH) enabled by
default (`chrome://settings/security` → "Use secure DNS"). When DoH
is on, Chrome resolves hostnames *itself* by sending HTTPS queries
to Cloudflare / Google / Quad9 instead of asking the Windows
resolver. The `Microsoft-Windows-DNS-Client` ETW provider — our
Phase 8 source — only emits event 3008 for queries that travel
through the Windows resolver. Apps that bypass the resolver
(Chrome with DoH, Firefox with DoH/DoT, anything embedding its own
resolver) are **structurally invisible** to the Phase 8 observer.
This is by design on the app side; nothing we can change in our
own code recovers visibility for those apps from this provider.

**Affected surface:** browsers are the largest miss. Anything else
using a system HTTP client, the OS resolver, or a legacy network
stack continues to work (Steam, Outlook, Windows Update, .NET apps
using `HttpClient`, Electron apps that don't override the resolver,
etc.).

**User-side workaround:** disabling Chrome's DoH
(`chrome://settings/security` → "Use secure DNS" → Off) routes
lookups back through the Windows resolver and restores Phase 8
visibility for Chrome. This is a per-user privacy trade-off, not
something we can or should force. Document, don't override.

**Code-side follow-up:** scoped as **Phase 8.5 — endpoint
visibility investigation** below. Pre-MVP requirement: ZenVizor's
core value is "which app talked to where," and accepting "no idea
for Chrome traffic" as the v1 answer is too large a gap to ship.
The investigation evaluates passive TLS SNI observation and any
other invariant-#1-safe technique that recovers coverage for
DoH-using apps.

**Accepted for Phase 8 sign-off:** the four manual gates verified
the pipeline works end-to-end (Steam + Claude desktop produced
human-readable hostnames; zero own-traffic invariant held;
performance budget held). The gap is a coverage limit of the
underlying ETW provider, not a defect in the slice work — Phase 8
is closed-with-known-gap and Phase 8.5 picks up the gap as a
distinct piece of work.

### Phase 8 design decisions and alternatives considered

Recorded during the Phase 8 implementation kickoff (2026-06-20). Each
entry: what was chosen, what was rejected, why, and what would prompt
revisiting. Kept evergreen so a future maintainer reopening any of
these can read the original tradeoff without reconstructing the
conversation.

#### D1 — DNS observer seam shape

**Chosen:** A sibling `ICaptureSource` implementation
(`DnsCaptureSource`) plus a shared in-memory `IDnsResolutionStore`
that `TrafficAggregator.Flush` reads at flush time. The DNS source
lives next to `EtwCaptureSource`, not inside it; the store is the
join point between the two capture paths.

**Alternatives rejected:**

- *New `IMonitor` implementation owning its own flush ticker.* DNS
  events don't feed `TrafficAggregator.Observe(NetworkObservation)`
  — they feed a side-store the aggregator reads at flush time. A
  parallel `IMonitor` would duplicate the existing `CaptureMonitor`
  scaffolding (ETW lifecycle + reader loop + flush timer) for no
  gain.
- *Single combined `EtwCaptureSource` enabling both kernel network
  and DNS Client providers.* See D5 — same tradeoff space; combining
  blurs the source seam.

**Why this shape:** Matches the PRD §6 seam #1 contract (each
`IMonitor` is one source, future passive watchers slot in here) and
keeps the synthetic-source test seam swappable for each capture
path independently.

**Revisit if:** a future passive observer (e.g. hosts-file watcher)
also needs to feed `TrafficAggregator` via a side-store. At three
side-stores we should generalise the join machinery rather than
hand-wiring each one.

#### D2 — IPC schema versioning scope

**Chosen:** Bump the shared `IpcSchemaVersion.Query` from 1 to 2.
The constant covers all four Phase-4 query payloads (`AppList`,
`AppDetail`, `ConnectionList`, `TrafficHistory`); they all move
together.

**Alternative rejected:** Split the constant — introduce a
dedicated `IpcSchemaVersion.Connections = 2` and leave the other
three at v1.

**Why shared bump:** Matches the existing comment on the constant
("Shared schema version of the Phase-4 query result payloads") and
the current pattern. The four payloads have travelled together
through Phase 4; staying together keeps the floor-check surface
small (clients have one shared check, not four). The only cost is
that v2 clients reject a v1 server even for payloads that didn't
actually change shape — but since server + clients ship in lockstep
via the Burn bundle, that's a theoretical not practical cost.

**Revisit if:** the four payloads start evolving on independent
cadences (e.g. `AppList` adds a column without `AppDetail` doing the
same), or a third-party client appears that wants to pin individual
payload versions.

#### D3 — `connections.resolved_host` update semantics

**Chosen:** On the existing `INSERT ... ON CONFLICT ... DO UPDATE`
upsert in `SqliteFlushSink.UpsertConnections`, populate
`resolved_host` on INSERT and use
`COALESCE(resolved_host, excluded.resolved_host)` on UPDATE. A null
existing value gets filled by a later non-null arrival; a non-null
existing value is never overwritten.

**Alternative rejected:** INSERT-only — `resolved_host` set on
first insert, never updated. Simpler SQL but misses the case where
the capture path saw the connection *before* the DNS source had the
A record cached.

**Why COALESCE:** The DNS observer can be racy — a connection
may flush with the IP before the matching DNS-response event has
been parsed and stored. COALESCE lets the next flush rescue that
row's hostname instead of leaving it permanently null.

**Why not "always take the freshest"?** A non-null `resolved_host`
is treated as load-bearing — if the IP later genuinely resolves to a
different name (CDN aliasing), the originally observed name is
preserved because it matches what the user's *traffic* actually
asked for. The "most recent observation" preference, if we ever want
it, belongs in the read-side picker (see D4), not the write path.

**Revisit if:** users report stale hostnames on long-lived
connection rows (e.g. an Outlook session that lasts days through
multiple CDN flips). At that point reconsider as
"`MAX(resolved_host)` with a recency tiebreaker" at the SQL level.

#### D4 — Per-endpoint hostname picker on read side

**Chosen:** `MAX(c.resolved_host) AS host` in
`AppHistoryQueryRepository.GetConnections`'s existing GROUP BY.
Lexicographic pick across the app's session rows for a given
`(protocol, remote_addr, remote_port)`.

**Alternative rejected:** Most-recent non-null — a correlated
subquery or window function selecting the `resolved_host` from the
session row with the largest `last_seen`. More intuitive for the
user when an IP genuinely served multiple names, but materially
more complex SQL.

**Why MAX:** For ~95% of endpoints the two options return the
same value (most IPs have one hostname). The two diverge only on
CDN IPs that genuinely served multiple names within the window
(CloudFront, Akamai, Office 365 edge), where the divergent names
are usually variants of each other ("close enough" for the user's
investigation). Lexicographic picks alphabetically — not "best,"
not "most recent" — but the cost of a wrong pick is a "huh?"
moment, not wrong information. The IPC contract is identical for
either option (`string?`), so we can swap server-side without
touching the schema, the wire, or the UI.

**Revisit if:** manual QA on real CDN endpoints surfaces user
confusion ("the hostname shown doesn't match what I see in dev
tools"). At that point promote to most-recent-non-null with a
window function — purely a query-side change.

#### D5 — Two `TraceEventSession`s vs one combined

**Chosen:** Two separate sessions —
`ZenVizor.Capture` (existing, kernel network provider) and
`ZenVizor.Capture.Dns` (new, `Microsoft-Windows-DNS-Client`
user-mode provider). Each owned by its respective `ICaptureSource`.

**Alternative rejected:** One combined `TraceEventSession` that
enables both kernel and user-mode providers. The TraceEvent API
allows this — kernel and user-mode providers can coexist in one
session.

**Why two sessions:**

- *Lifecycle isolation.* A fault subscribing to or processing one
  provider doesn't take the other down. The existing capture path
  is load-bearing for every other ZenVizor feature; the DNS path
  is purely an enrichment. They should fail independently.
- *Seam cleanliness.* `EtwCaptureSource` is built around the kernel
  parser (`_session.Source.Kernel.TcpIpRecv += …`). Adding the DNS
  Client provider would mean subscribing via the dynamic parser
  inside the same class — the class is no longer "the kernel
  network source," it's "the omnibus ETW thing," and the synthetic
  test seam can no longer substitute the two feeds independently.
- *Feature toggling.* If we ever want to ship a build with DNS
  observation disabled (e.g. a corporate variant where IT-security
  forbids subscribing to the DNS Client provider), it's a wiring
  change in the composition root, not a refactor.

**Cost of the choice:** Roughly 1–2 MB additional ETW buffer memory
(DNS session can be sized far smaller than the kernel ring because
DNS event rate is low), one extra ETW reader thread, one extra ETW
session slot (Windows allows ~64 per machine — we go from 1 to 2,
negligible). Sub-1% of the perf budget.

**Revisit if:** the ETW session-slot ceiling becomes a real concern
(unlikely unless ZenVizor grows to 5+ providers), or if buffer
memory shows up in the working-set budget on low-RAM SKUs.

---

## Phase 8.5 — Endpoint visibility investigation (pre-MVP requirement)

**Goal:** scope and prototype an invariant-#1-safe technique that
recovers hostname visibility for **DoH-using and in-app-resolver
apps** (Chrome by default, Firefox by config, anything embedding
its own resolver) — the class of apps that Phase 8's DNS observer
is structurally blind to. Exit criterion is a brief + a
go/no-go/scope-down decision on shipping the technique pre-MVP.

**Status:** **Complete, 2026-06-21.** Desk analysis, throwaway
prototype (real-box run), and formal findings doc
(`docs/phase-8.5-endpoint-visibility.md`) all landed. **Decision:
outcome 1 — ship pre-MVP at full coverage.** The prototype settled
both open unknowns (PktMon delivers truncated payloads into our own
TraceEventSession; perf upper bound 0.23% CPU / 35 MB WS), confirmed
invariant #1 (empty self-monitoring lens), and proved both parsers
(TLS live, QUIC offline against the RFC 9001 §A.1 vector).
Implementation is the (now confirmed) Phase 8.6 below.

**Why pre-MVP:** ZenVizor's headline value is "which app talked to
*where*." Shipping v1 with "no idea where, for Chrome traffic" is
too large a hole — Chrome dominates real-world browsing traffic
on most desktops. The user has explicitly elevated this from
post-MVP follow-up to pre-MVP gate.

**The hard constraint:** invariant #1 (zero own traffic) holds
absolutely. Any approach that originates packets, performs
reverse DNS lookups, or initiates connections is **off the table**
regardless of how clean the user-facing result would be. Active
techniques live behind the boundary documented in PRD §10; that
boundary does not move for this work.

### Scope

The spike covers:

- **Survey + comparative analysis** of the techniques that
  *could* recover endpoint hostnames passively. Initial candidate
  list (extend during the spike if other approaches surface):
  - **TLS SNI extraction** from the unencrypted ClientHello of the
    TCP+TLS handshake. SNI is sent in plaintext in TLS 1.2 and in
    TLS 1.3 *without* Encrypted ClientHello (ECH). Coverage falls
    off when ECH is enabled — currently a minority of traffic but
    a moving target (Chrome ships ECH support; Cloudflare and a
    handful of large origins offer it).
  - **HTTP Host header extraction** for non-TLS HTTP/1.1 traffic.
    Tiny coverage on the modern web (HTTPS is near-universal), but
    near-zero cost to add if the capture surface already exists.
  - **QUIC SNI** — same idea as TLS but over UDP. Most of Chrome's
    Google traffic is QUIC. Plaintext SNI in QUIC v1; encrypted in
    later drafts.
  - **Microsoft-Windows-PktMon** ETW provider — packet-level
    visibility, but heavy and complex to consume. Worth evaluating
    as the substrate the SNI extraction would sit on.
  - **NDIS Lightweight Filter (LWF)** — kernel-mode driver
    delivering packet contents. Most coverage, biggest deployment
    cost (signed driver, install elevation, anti-virus friction).
  - **Windows Filtering Platform (WFP)** callouts — alternative
    kernel surface, similar tradeoffs to NDIS LWF.
- **Coverage modelling.** For each candidate, estimate what
  fraction of Phase 8's currently-blind connection rows it would
  recover. Use the Phase 8 manual gate session data as the
  baseline (rows where `resolved_host IS NULL` from `chrome.exe`,
  `msedge.exe`, etc.).
- **Performance projection** against the perf budget (idle CPU
  < 1 %, working set < ~80 MB). TLS SNI inspection on every TCP
  SYN is dramatically more event traffic than DNS observation;
  the budget pressure is the load-bearing risk for any
  packet-level approach.
- **Invariant #1 audit** for each candidate: does the technique
  itself, the library that implements it, or any default
  configuration of that library, emit *any* outbound traffic?
  This includes loopback DNS resolution, NTP probes from libraries
  that auto-update, "telemetry" hooks. Zero tolerance.
- **Prototype of the leading candidate** — minimal end-to-end
  spike that captures one TLS handshake from one Chrome connection
  to a known endpoint and produces the SNI hostname. The prototype
  is throwaway code; its job is to verify the engineering effort
  estimate, not to ship.
- **Ship/scope/defer recommendation** with explicit reasoning
  against the pre-MVP gate. Three plausible outcomes:
  1. **Ship pre-MVP at full coverage** — one of the candidates is
     tractable, fast, and recovers most lost coverage. Phase 8.6
     scopes the implementation.
  2. **Ship pre-MVP scoped down** — partial coverage is acceptable
     (e.g., TLS 1.2 SNI only, accept ECH gap, accept QUIC gap).
     Phase 8.6 scopes the narrower implementation; the gap is
     documented analogously to the Phase 8 DoH gap.
  3. **Defer post-MVP with justification** — every candidate is
     too expensive / too risky / too lossy to clear the pre-MVP
     bar, and the right MVP move is to ship Phase 8 as-is with
     the limitation more prominently documented in the UI itself
     (not just the verification doc). Requires user sign-off
     because this overrides the "pre-MVP requirement" stake.

### Leading direction (recorded 2026-06-21)

Desk survey + coverage/perf/invariant analysis landed on
**outcome 1 — ship pre-MVP at full coverage**, pending the throwaway
prototype confirming two unknowns (below). Recorded here so the
conclusion survives even if the formal findings doc lands later.

**Why this is additive, not a re-architecture.** Phase 8 already
built the entire back half and it is *source-agnostic*: the
`DnsResolutionStore` is the single join point (design decision D1),
read once per connection at flush (`TrafficAggregator.Flush` →
`TryGetHostname`), COALESCE-upserted (D3), picked with `MAX()` (D4),
carried as the v2 `ResolvedHost` string, and rendered by the
AppDetail grid. None of that cares where a hostname came from. The
DoH gap is therefore *one missing thing — a second feeder into the
store* — not a redesign.

**Root cause, restated.** The hostname for a DoH/in-app-resolver
flow exists in plaintext nowhere in the running system except the
**TLS/QUIC handshake on the wire**: the kernel network provider
carries only metadata (addresses, ports, PID, byte counts — no
payload), and DoH both bypasses the Windows resolver *and* encrypts
the query inside HTTPS. (Note: the built-but-unwired
`Rfc1035ResponseDecoder` UDP/53 fallback would not have helped here —
DoH is not UDP/53.) Recovering these hostnames requires
packet-payload access, which Phase 8's substrate does not have.

**Chosen technique:** passive extraction of the **plaintext SNI**
from the ClientHello (TLS-over-TCP and QUIC) plus the HTTP/1.1
**Host** header. Observe-only; emits zero traffic; invariant #1 holds
absolutely.

**Substrate — ranked:**

1. **`Microsoft-Windows-PktMon` ETW provider (primary).** Built into
   Win10 1809+/11 (all target SKUs), no driver, no signing. Its
   **port filter + packet truncation** are the load-bearing detail
   the original candidate list under-weighted: filter to TCP 443/80 +
   UDP 443 and truncate to ~the first 320 bytes, so the kernel→user
   copy is bounded at the source. Consumed via the existing
   TraceEvent + sibling-session pattern (D5).
2. **Raw socket `SIO_RCVALL` (documented fallback).** Receive-only,
   user-mode, fully documented Winsock; emits nothing. No kernel-side
   filter (post-filter in user mode), so it copies more — kept as the
   de-risking option if PktMon's control surface proves awkward.
3. **WFP callout / NDIS LWF (rejected).** A signed kernel driver
   breaks the non-elevated install story (elevation, AV friction) for
   no coverage gain over the above. Explicitly off the table.

**Parsers (each feeds the existing store):**

- **TLS-over-TCP SNI** — plaintext in TLS 1.2 and TLS 1.3-without-ECH;
  a simple binary walk in the `Rfc1035ResponseDecoder` style. Big
  win, low effort.
- **QUIC Initial SNI** — the ClientHello rides CRYPTO frames in the
  QUIC Initial, "encrypted" with keys *deterministically derivable*
  from the Destination Connection ID + the fixed RFC 9001 salt, i.e.
  readable by any observer. Decrypt uses **`System.Security.Cryptography.HKDF`
  + `AesGcm`/`Aes` — all in-box, no new dependency, no network.** More
  code than TLS but bounded and fully specified; recovers Chrome↔Google
  (YouTube/gstatic) and HTTP/3 origins.
- **HTTP/1.1 Host header** (TCP 80) — trivial, tiny coverage,
  near-free once the substrate exists.

**Perf framing.** SNI lives in the *first* client→server packet of a
flow, so cost scales with **new-connection rate, not packet rate**: a
per-flow "already-classified" bounded LRU (same shape as
`DnsResolutionStore`) drops packets for flows we have already named,
truncation caps per-packet copy, and the port filter caps which
packets we see at all. At idle — the actual budget line — new flows
are ~zero, so cost is ~zero.

**Coverage / residual gap.** TLS + QUIC + Host recovers the dominant
share of Chrome/DoH traffic. The only structural residue is
**ECH-enabled origins** (a small, watch-it-grow minority), documented
with the same pattern as the Phase 8 DoH note — a true limit, not an
excuse.

**Two unknowns the throwaway prototype must still settle** (neither a
blocker):

1. **PktMon control surface** — whether enabling
   `Microsoft-Windows-PktMon` in a `TraceEventSession` yields
   truncated payloads directly, or needs PktMon's capture component
   started (CLI / control API) alongside. The raw-socket fallback
   exists precisely to de-risk this.
2. **Real-box perf under sustained inbound bulk** — confirm
   filter + truncate + per-flow-gate holds idle CPU < 1% /
   WS < ~80 MB during a large HTTPS download.

**Implementation lands as the stubbed Phase 8.6 below**, gated on
these two confirmations.

### Out of scope

- Any active technique (reverse DNS, captive portal probing,
  active SNI replay). PRD §10 boundary.
- Recovering hostnames for ECH-encrypted traffic specifically —
  by design we can't, regardless of mechanism.
- Identifying *which* protocol an app uses to make its DNS
  decisions. Useful diagnostic data but doesn't feed the
  connection-row hostname column.
- A general packet-capture surface. Even if PktMon turns out to be
  the right substrate, the spike's deliverable is hostname
  visibility, not arbitrary packet inspection.

### Acceptance criteria — CI (headless)

There is no CI work for this phase; the deliverable is a brief.
Any prototype code lives in a throwaway branch and is deleted at
spike close.

### Acceptance criteria — manual (human review)

- [x] Findings doc at `docs/phase-8.5-endpoint-visibility.md`
      containing the survey, coverage model, perf projection,
      invariant-#1 audit, prototype results, and the
      ship/scope/defer recommendation.
- [x] Follow-on Phase 8.6 entry confirmed (ship at full coverage);
      its two gating unknowns are now settled in the findings doc.
- [x] The recommendation is referenced from the PRD §10
      active-probe boundary section and from the
      Phase 8 verification doc's known-limitations block, so the
      MVP doc set tells a single coherent story about what
      coverage v1 ships.

**Sequencing note:** Phase 8.5 runs after Phase 8 closes and
*before* Phase 9 — Phase 9 is the MVP finalization phase and
needs to know whether it's wrapping Phase 8 alone or Phase 8 + a
Phase 8.6 implementation. Phase 9.6 (re-cut MSI + Burn bundle)
must be re-evaluated against the Phase 8.5 outcome before it runs.

---

## Phase 8.6 — Passive SNI/QUIC/Host hostname recovery (DoH-blind apps)

> **CLOSED 2026-06-21 — CI green + all manual gates walked.**
> The parsers + QUIC crypto + dual substrate (PktMon primary,
> raw-socket fallback) are ported from the spike into
> `src/ZenVizor.Capture/Sni/`, wired into the service composition root
> (`SniCaptureEnabled` toggle), and feed the unchanged
> `DnsResolutionStore`. All six headless acceptance criteria pass
> (41 new tests in `tests/ZenVizor.Core.Tests/Sni/`; 554/554
> solution-wide) and the four real-box gates pass (Chrome TLS+QUIC
> hostnames, QUIC YouTube/Google, zero-own-traffic, perf under bulk).
> Full walkthrough + sign-off in `docs/phase-8.6-verification.md`. The
> spike (`spike/SniSpike/`) has been deleted now that the port is green.
>
> The §7 PktMon control-surface unknown is **resolved**: the capture
> component is **required** — enabling the `Microsoft-Windows-PktMon`
> ETW provider alone delivers no payloads; `pktmon start --capture`
> must be running, so the source's child-process spawn is load-bearing.
> Settled via a `pktmon stop` counterfactual (fresh site lands with
> capture on, not with capture off), which controlled the confound the
> Phase 8.5 spike couldn't isolate (see
> `docs/phase-8.5-endpoint-visibility.md` §7). §8's build notes
> (Ethernet-header strip on PktMon; per-protocol truncation with the
> QUIC-needs-the-full-datagram constraint; mandatory per-flow gate) are
> all applied.

**Goal:** Close the Phase 8 DoH / in-app-resolver coverage gap by
adding a **second passive feeder** into the existing
`DnsResolutionStore` that extracts hostnames from plaintext **TLS
SNI**, **QUIC Initial SNI**, and **HTTP/1.1 Host** headers. Recovers
hostname visibility for Chrome-with-DoH and any app embedding its own
resolver — the class `Microsoft-Windows-DNS-Client` (Phase 8) is
structurally blind to. Strictly observational; emits ZERO traffic of
its own (invariant #1).

**Scope**

- New capture front-end — **`Microsoft-Windows-PktMon` ETW provider
  (primary)** with a port filter (TCP 443/80, UDP 443); **raw socket
  `SIO_RCVALL` (fallback)** if the PktMon control surface proves
  awkward. New ETW session is a sibling per D5 (lifecycle isolation;
  feature-toggleable at the composition root). Kernel drivers
  (WFP/NDIS) are out — see Phase 8.5 "Leading direction."
- **Truncation is per-protocol, NOT a single global cap** (spike
  finding, `phase-8.5-endpoint-visibility.md` §8): a flat ~320 B cap
  is wrong. **QUIC/UDP must capture the full Initial datagram** —
  AES-128-GCM is all-or-nothing over the full ciphertext + 16 B tag,
  so a truncated Initial fails AEAD auth and yields no SNI.
  **TLS/TCP needs ≈512 B+ or per-flow segment reassembly** — a large
  TLS 1.3 ClientHello (key shares, GREASE, ALPN) can push the SNI
  extension past a small cap and can span multiple TCP segments.
- **PktMon payloads carry a 14-byte Ethernet L2 header** (spike
  finding): the PktMon adapter strips L2 (handle `0800` IPv4 / `86DD`
  IPv6 EtherType + any VLAN tag) before the IP walk; the raw-socket
  path starts at the IP header. Keep IP/TCP/UDP/parser code
  substrate-agnostic; the L2 strip lives in the PktMon adapter only.
- **TLS ClientHello SNI parser** (TCP) — plaintext for TLS 1.2 and
  TLS 1.3-without-ECH. Mirror the robustness contract of
  `Rfc1035ResponseDecoder` (empty result, never throw, on malformed
  input).
- **QUIC Initial parser** (UDP 443) — derive initial secrets from the
  Destination Connection ID + RFC 9001 v1 salt, decrypt the Initial,
  read the ClientHello SNI from the CRYPTO frames. BCL only
  (`System.Security.Cryptography.HKDF` + `AesGcm`/`Aes`); no new
  dependency, no network.
- **HTTP/1.1 Host header parser** (TCP 80) — trivial; near-free once
  the substrate exists.
- **Per-flow "already-classified" bounded LRU gate** (same shape as
  `DnsResolutionStore`) so steady-state cost scales with
  new-connection rate, not packet rate.
- **Store wire-up:** extracted `(remote IP → hostname)` lands in
  `DnsResolutionStore.Record` with a default TTL (SNI carries no TTL
  — same fixed-default pattern as event 3008; see
  `DnsCaptureSource.DefaultTtlSeconds`). **No changes to the flush
  join, COALESCE upsert, IPC schema, or UI** — those already carry a
  source-agnostic `ResolvedHost`.
- **UI honesty:** surface the residual ECH gap in-app (not just the
  verification doc), per the Phase 8.5 acceptance criteria.

**Implementation starting point (port from the spike — do not rewrite
the crypto)**

The Phase 8.5 throwaway harness (`spike/SniSpike/`) already contains
production-shaped, validated parsers. Port them; the QUIC crypto in
particular is the risky part and is already pinned to the RFC vector,
so a rewrite would only reintroduce retired risk.

- **Code placement parallels the Phase 8 DNS source.** The parser
  robustness template (`Rfc1035ResponseDecoder`) and the DNS capture
  source both live under `src/ZenVizor.Capture/Dns/`; mirror that:
  - Parsers + `QuicCrypto` → `src/ZenVizor.Capture/Sni/` (from spike
    `TlsClientHelloParser.cs`, `QuicInitialParser.cs`, `QuicCrypto.cs`,
    `HttpHostParser.cs`).
  - New `ICaptureSource` (PktMon primary + raw-socket fallback) →
    `src/ZenVizor.Capture/Sni/`, mirroring
    `Capture/Dns/DnsCaptureSource.cs` (sibling ETW session, D5).
  - Mapper from parsed `(remote IP → hostname)` to
    `DnsResolutionStore.Record` → mirror
    `Capture/Dns/DnsClientEventMapper.cs`.
  - Store is **unchanged**: `src/ZenVizor.Core/Dns/DnsResolutionStore.cs`.
  - Tests → `tests/ZenVizor.Core.Tests/Sni/`, parallel to the existing
    `tests/ZenVizor.Core.Tests/Dns/` (note: the DNS parser tests live
    in Core.Tests, which already references `ZenVizor.Capture` — there
    is no separate Capture.Tests project).
- **Gold anchor:** keep the RFC 9001 §A.1 key-schedule assertion (spike
  `QuicSelfTest`) as a CI test — it is what rules out a derivation bug
  hiding behind a symmetric encrypt path. Reuse `ClientHelloFactory`
  as the fixture builder for the determinism tests.
- **Spike disposition — done.** `spike/SniSpike/` was deleted
  2026-06-21 once the port landed and its tests went green ("spike
  close" = after 8.6 took what it needed). The parsers + `QuicCrypto`
  ported verbatim; the QUIC crypto was not rewritten. Recoverable from
  git history if ever needed.

**Acceptance criteria — CI (headless)** — **all pass, 2026-06-21**

- [x] Synthetic TLS ClientHello fixtures (TLS 1.2 + TLS 1.3-no-ECH)
      → exact-expected SNI extracted (determinism gate per CLAUDE.md
      "synthetic events assert exact rows"). — `TlsClientHelloParserTests`
- [x] Synthetic QUIC Initial fixture → decrypt + exact-expected SNI;
      exercises the RFC 9001 v1 salt + HKDF + AEAD path. Keeps the
      §A.1 key-schedule vector as a gold anchor. — `QuicInitialParserTests`
- [x] Synthetic HTTP/1.1 request fixture → exact-expected Host. —
      `HttpHostParserTests`
- [x] Malformed / truncated / non-handshake inputs → empty result,
      never throws (mirror `Rfc1035ResponseDecoder`). — across all
      parser test classes
- [x] Per-flow gate: once a flow yields a hostname (or N packets pass
      without one), further packets for that 4-tuple are dropped
      without re-parse. — `SniFlowTrackerTests` + `SniPacketProcessorTests`
- [x] Store wire-up: extracted SNI populates
      `connections.resolved_host` at flush for a fixture whose
      `remote_addr` matches. — `SniCaptureSourceTests` lands the
      `(ip → host)` in `DnsResolutionStore`; the store→flush→`resolved_host`
      join is covered by the existing `AggregatorResolvedHostTests`.

**Acceptance criteria — manual (human QA — real Windows box)** —
**all pass, walked 2026-06-21** (sign-off in `docs/phase-8.6-verification.md`)

- [x] Chrome with default DoH **on**: AppDetail Connections grid
      shows human-readable hostnames for the bulk of `chrome.exe`
      flows (TLS + QUIC). Hit rate materially above the Phase 8
      baseline (near-zero for Chrome).
- [x] QUIC-heavy target (YouTube / Google) renders hostnames,
      confirming the QUIC decrypt path end-to-end.
- [x] **Self-monitoring zero-own-traffic gate** (invariant #1) holds
      with the new capture session active — ZenVizor PIDs still
      attribute zero outbound.
- [x] Performance: idle CPU < 1%, service WS < ~80 MB hold under a
      sustained large HTTPS download (the inbound-bulk stress case).
- [x] Residual ECH gap documented in the UI + Phase 8 verification
      doc, same pattern as the DoH note. — AppDetail "Remote endpoint"
      info popup (`AppDetailPage.xaml`) + `docs/phase-8-verification.md`
      *Known limitations* + the dedicated section in
      `docs/phase-8.6-verification.md`. (Doc-only criterion; complete
      without the real-box walk.)

**Sequencing note:** runs after Phase 8.5 closes and before
Phase 9.6 (re-cut MSI + Burn bundle) — the bundle must pack the 8.6
service binaries, so 8.6 lands first or 9.6 re-cuts. IPC stays at
schema v2: SNI feeds the same `ResolvedHost` field, so no wire bump.

---

## Phase 9 — MVP finalization (1.0.0 ship gate)

**Goal:** Take the feature-complete build from Phase 8 to a v1.0.0 release.
Three threads — binary-size cleanup, ship-blocker polish, version bump —
plus a final re-cut of the MSI + Burn bundle and one last walk of the
manual gates.

### 9.1 — RID-specialize build outputs to win-x64

**Discovered:** Phase 8 review, 2026-06-20.

**The problem:** No csproj sets `<RuntimeIdentifier>` or `<RuntimeIdentifiers>`.
Default .NET behaviour copies *every* RID's native assets from transitive
dependencies (Microsoft.Data.Sqlite, SkiaSharp via LiveCharts2) into the
output directory. Result: the Service ships 31 MB of `runtimes/`
containing `libe_sqlite3.so` (Linux), `libe_sqlite3.dylib` (macOS),
`browser-wasm/`, `linux-mips64/`, `osx-arm64/`, etc. — 23 RIDs total for
a Windows-only Windows Service. The UI ships 52 MB of `runtimes/` with
the same pattern dominated by SkiaSharp natives shipped for osx (~18 MB),
win-arm64 (~12 MB), and win-x86 (~11 MB). Installed footprint is ~210 MB
versus what should be ~130–140 MB.

**Fix path:** Add `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` to
`ZenVizor.Service.csproj`, `ZenVizor.Ui.csproj`, and `ZenVizor.Cli.csproj`.
Keep `SelfContained=false` (the default) so the .NET runtime stays
separate — the Burn bundle still ships it. Verify with `du -sh` on the
build outputs (expect ~30–40 MB drop from each of Service + UI) and a
re-cut of the MSI + bundle on a clean VM.

**Cross-platform portability note:** RID specialization to win-x64 does
NOT foreclose a future macOS/Linux port. A cross-platform ZenVizor
would necessarily be a separate build target with its own RID
(`osx-arm64`, `linux-x64`) because (1) ETW is Windows-only — a Linux
capture engine would use eBPF, a macOS one Endpoint Security /
NetworkExtension; (2) WPF doesn't run on macOS/Linux — the UI would be
Avalonia or MAUI; (3) named-pipe IPC with Windows ACLs would become
Unix domain sockets + POSIX permissions. The native binaries SkiaSharp /
SQLite ship per-RID are picked up at the *publish target's* RID at
build time — a future `osx-arm64` build automatically pulls in
`libHarfBuzzSharp.dylib` + `libe_sqlite3.dylib`. The current all-RIDs
dump is a build-time accident, not portability prep — no Windows user
can execute the Linux `.so` files in today's output.

### 9.2 — Reports page default date → `DateTime.Today`

**Discovered:** Phase 8 review, 2026-06-20.

**The problem:** `src/ZenVizor.Ui/Views/ReportsPage.xaml.cs:28` hardcodes
`InitialDate = new(2026, 6, 8)` and references it ~10× through the file
(date picker default, hero eyebrow, chart axis bounds, empty-state
ticks, anchor captions). The constant was the Phase 5a mockup-screenshot
date; Phase 5b's real aggregator never came back through to remove it,
so a fresh install always opens Reports on June 8, 2026.

**Fix path:** Replace the static `InitialDate` constant with a value
computed at page construction time (`DateTime.Today`). All current
reference sites flow through the same value — date picker default, chart
axis MinLimit/MaxLimit, hero eyebrow, anchor captions. Test fixtures at
`tests/ZenVizor.Integration.Tests/DailyReportCsvWriterTests.cs` pin to
`2026-06-08` as fixture seeds; leave unchanged.

### 9.3 — Anchor-baseline insufficient-history guard (deferred from Phase 5)

The Phase 5 follow-up block above (line ~280) describes this in full.
Phase 9 is the implementation slot: pick treatment (a) placeholder or
(b) warning banner, wire against `DailyReportRepository.LoadAnchorBaseline`
and `LoadUnusualVolume`, persist a per-machine first-run timestamp
(or derive from `MIN(first_seen)` across `apps`). Closes a v1 honesty
gap — without it, fresh installs surface anchor deltas built on partial
baselines and may misrepresent normal usage as anomalous.

### 9.4 — Dead-code-only audit pass

**Scope (light, safe — do NOT do a wholesale comment-strip):**

- Delete `src/ZenVizor.Ui/Views/PlaceholderPage.xaml` + `.xaml.cs` —
  unreferenced anywhere in `src/` (no `: PlaceholderPage` base-class
  usage, no `new PlaceholderPage(...)`, no nav-rail routes). Confirmed
  via `Grep` 2026-06-20.
- Targeted unused-private sweep on the four largest files
  (`ReportsPage.xaml.cs` 1082, `Cli/Program.cs` 1024,
  `AppDetailPage.xaml.cs` 953, `MainWindow.xaml.cs` 858). Analyzer-driven
  (`IDE0051` "remove unused private members" is in the default ruleset
  with `EnforceCodeStyleInBuild=true` already set in `Directory.Build.props`).
- Leave the explanatory comments alone unless clearly stale
  ("Phase 5a only wires…" tags that no longer reflect current state).
  Most of the codebase's comments capture non-obvious WHY (e.g. the
  `SetREMOVE_DATA_FOLDER` scheduling note in `installer/ZenVizor.wxs`,
  the MessagePack pin rationale in `Directory.Packages.props`) and are
  load-bearing.

Audit findings out of scope for Phase 9: no `TODO`/`FIXME`/`HACK`
markers anywhere; logging is restrained (~110 call sites, ~70%
warning/error); `Console.WriteLine` only in `Cli/Program.cs` (it's a
CLI); single `Debug.WriteLine` in `App.xaml.cs:259` is a legit
fallback catch. No further cleanup warranted.

### 9.5 — Version bump 0.1.1 → 1.0.0 + SemVer policy

Update `<Version>1.0.0</Version>` in `Directory.Build.props` (single
source of truth — both WiX projects read `$(Version)`). Establish the
post-MVP versioning policy:

- `1.0.x` — bugfix releases (no behaviour changes that surprise users).
- `1.x.0` — new features (post-MVP modules from PRD §10, Phase 8
  follow-ups, additional alert producers).
- `2.0.0` — breaks in user-visible contracts: IPC schema beyond the
  additive-tolerance rule, DB schema requiring migration, config layout
  changes that don't carry over.
- The internal `IpcSchemaVersion.*` numbers (Settings v3, Alerts v1,
  Query v1/v2, DailyReport v2, ActivitySnapshot v2) keep their own
  per-surface incremental versioning per the additive-tolerance rule —
  orthogonal to product version.

Document the policy in `docs/versioning.md` (new file, short) and link
from `CLAUDE.md`.

### 9.6 — Re-cut MSI + Burn bundle; final manual gates

Last gate before 1.0.0 ship. Re-cut both installer artifacts on top of
the 9.1 / 9.2 / 9.3 / 9.4 / 9.5 changes, then re-run:

- All four Phase 7 manual gates (clean-VM install with + without .NET 10
  pre-installed, uninstall preserves runtime + `%ProgramData%\ZenVizor\`,
  bundle reinstall upgrades cleanly).
- The Phase 6.8b self-monitoring zero-own-traffic invariant gate
  (≥ 1 hour pointed at itself; no ZenVizor PID in `apps` or
  `connections`).
- Full-system pass: attribution sane, live + history + daily report
  reconcile, performance budget holds (idle CPU < 1%, service working
  set < ~80 MB).

Update `README.md` if any user-facing instructions changed (RID
specialization doesn't; version bump may surface in install dialog
chrome).

**Acceptance criteria — CI (headless)**

- [ ] 9.1: Build outputs verified by `du -sh` — Service + UI drop
      to ~17 MB + ~19 MB respectively (down from 48 MB + 71 MB);
      MSI down to ~25–30 MB (from 45 MB).
- [ ] 9.2: Fresh-launch Reports page tests assert the date picker
      reflects `DateTime.Today` (mock the clock for determinism).
- [ ] 9.3: Insufficient-history baseline tests assert the chosen
      treatment fires correctly when baseline rows < anchor window.
- [ ] All existing CI test suites continue to pass.
- [ ] Both MSI + bundle build cleanly on CI; artifacts uploaded.

**Acceptance criteria — manual (human QA — clean Windows VM)**

- [ ] All four Phase 7 manual gates pass against the 1.0.0 bundle.
- [ ] Phase 6.8b self-monitoring zero-own-traffic gate passes against
      the 1.0.0 build.
- [ ] Fresh install + launch: Reports opens on today's date, not
      2026-06-08.
- [ ] `%ProgramFiles%\ZenVizor\` total size ≤ ~140 MB
      (binaries; runtime is shared separately).
- [ ] Add/Remove Programs shows version `1.0.0`.

**Sequencing rationale:** 9.1 first because it changes build output
structure every later step (re-cut, install test) depends on. 9.2 / 9.3
/ 9.4 are independent and parallelizable. 9.5 second-to-last so the
version-bumped MSI is what 9.6 tests. 9.6 is the ship gate — don't
flip the project to `1.0.0` until 9.6 is GREEN.

---

## MVP definition of done

All Phase 0–9 acceptance criteria pass (CI + manual). The product: passively captures up/down traffic; attributes it to process incl. svchost service names and signer/path enrichment; shows a near-live dashboard; stores tiered history with user-defined windows and configurable retention; produces a daily report with CSV/HTML export; raises local alerts via an extensible pipeline; **resolves remote endpoint addresses to hostnames** from passively-observed DNS traffic (Phase 8); **installs via a single `ZenVizorSetup.exe`** that bundles the .NET 10 desktop runtime for offline installability (Phase 7); uninstalls cleanly; runs within the performance budget; and **emits no network traffic of its own**, verified by self-monitoring. Phase 9 finalises the build: RID-specialized binary layout, polish carry-overs, and the version bump to **1.0.0**. The collector contract, alert pipeline, and versioned IPC envelope seams (plus the reserved `devices` table) are in place so the post-MVP modules in PRD §10 can be added without re-architecting.
