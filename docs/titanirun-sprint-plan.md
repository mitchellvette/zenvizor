# TitaniRun — Sprint / Milestone Plan

**Working title:** TitaniRun
**Document type:** Phased build plan (companion to `titanirun-prd.md`)
**Status:** Scoping complete
**Last updated:** 2026-05-30

> Full product spec — features, data model, architecture, IPC contract, data model, out-of-scope boundaries — lives in **`titanirun-prd.md`**. This file is the build sequence and the QA gates.

---

## Standalone context (load-bearing facts repeated for use without the PRD)

- **What it is:** lightweight, **passive** Windows network monitor/reporter. Attributes up/down traffic to processes/services, stores history in SQLite, shows a near-live dashboard + daily reports. No firewall, no blocking.
- **Founding invariant (non-negotiable):** the app emits **zero network traffic of its own**. This is a *test gate* (Phases 3 and 6): point the tool at itself → it must report no outbound from its own processes. Any feature that would break this is out of scope.
- **Stack:** C# 14 / .NET 10 (`net10.0-windows`); WPF + WPF-UI + LiveCharts2; ETW via `Microsoft.Diagnostics.Tracing.TraceEvent` (`Microsoft-Windows-Kernel-Network`); IP Helper (`GetExtendedTcpTable`/`GetExtendedUdpTable`) for PID correction; SQLite; named pipes + StreamJsonRpc for IPC; WiX `.msi`; GitHub Actions CI.
- **Architecture:** elevated **Windows Service** (LocalSystem) does capture + owns the DB; non-elevated **WPF UI** displays data; they talk only over a **named pipe**. UI has no DB access.
- **CLI:** `trctl` is the companion CLI client used for scripted/manual QA throughout.
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
- `trctl` skeleton that connects and calls a `Ping`/`GetServiceStatus` stub.

**Acceptance criteria — CI (headless)**

- [x] `dotnet build` and `dotnet test` pass in Actions on a clean checkout.
- [x] Migration runner creates a DB with all tables; a test asserts schema matches expected.
- [x] IPC contract test: client and server negotiate version in-process.

**Acceptance criteria — manual (your QA)**

- [x] Service installs, starts, stops, uninstalls via CLI; writes a startup log line.
- [x] UI launches, shows shell + tray icon, close-to-tray works, Exit quits.
- [x] `trctl ping` round-trips over the real named pipe; unauthorized/unACL'd access is rejected.

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
- `trctl` gains `snapshot` command.

**Acceptance criteria — CI (headless)**

- [ ] Contract tests: snapshot request/response and envelope versioning round-trip in-process.
- [ ] Snapshot is served from the in-memory aggregate (test asserts no SQLite read on the snapshot path).

**Acceptance criteria — manual (your QA)**

- [ ] Generate live traffic; the dashboard reflects it with only minor delay/aggregation; rates match reality within tolerance.
- [ ] **Self-monitoring check:** with the tool running and the UI open, the tool reports **zero outbound** from its own service/UI processes (named-pipe IPC produces no network rows). *This is the founding-invariant gate.*
- [ ] `trctl snapshot` returns the same data the UI shows.

---

## Phase 4 — History tiers, query surface, per-app drill-down

**Goal:** Rollups, retention, the full query/reporting IPC surface, and the per-app → connections → history navigation.

**Scope**

- Hourly/daily rollup jobs from `traffic_samples`; retention/purge jobs per PRD §7.9.
- Query IPC: `GetAppList`, `GetAppDetail`, `GetConnections`, `GetTrafficHistory` (grain selection).
- UI: Per-App breakdown, App detail (connections + history), History/timeline with **user-defined window**.

**Acceptance criteria — CI (headless)**

- [ ] Rollup correctness: sample fixtures roll up to exact hourly/daily totals.
- [ ] Retention: rows older than configured windows are purged; newer retained; rollups preserved per policy.
- [ ] User-defined-window query over fixtures returns exact expected totals at each grain.

**Acceptance criteria — manual (your QA)**

- [ ] Per-app list totals reconcile with the daily numbers and with the live view over the same window.
- [ ] Drill app → connections shows correct endpoints with local/WAN + protocol; drill → history series matches.
- [ ] Changing the query window updates results correctly; large windows stay responsive (served from rollups).

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

---

## Phase 6 — Alert pipeline (first customer), Settings, tray polish, installer

**Goal:** Wire the alert seam with one real alert, finish Settings (incl. configurable autostart) and tray, and ship a clean .msi. This is the MVP-complete gate.

**Scope**

- **Seam #2:** alert pipeline + `Alert` entity + `GetAlerts`/`AcknowledgeAlert` + `AlertRaised` push + Alerts feed UI + optional toast.
- **First real alert:** *unsigned binary from a user-writable path making network connections* (purely local, no new monitor, no network).
- Settings: **autostart toggle** (service start mode incl. "off" for fast-boot users), retention windows, purge history, flush/bucket intervals, toast toggle, theme.
- Tray polish; close-to-tray/Exit finalized.
- **WiX .msi:** installs/registers the service with the chosen start mode, installs the UI, sets DB ACLs and `%ProgramData%\TitaniRun\` layout; clean uninstall; `wix build` CLI-drivable in CI.

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

## MVP definition of done

All Phase 0–6 acceptance criteria pass (CI + manual). The product: passively captures up/down traffic; attributes it to process incl. svchost service names and signer/path enrichment; shows a near-live dashboard; stores tiered history with user-defined windows and configurable retention; produces a daily report with CSV/HTML export; raises local alerts via an extensible pipeline; installs/uninstalls cleanly via .msi; runs within the performance budget; and **emits no network traffic of its own**, verified by self-monitoring. The collector contract, alert pipeline, and versioned IPC envelope seams (plus the reserved `devices` table) are in place so the post-MVP modules in PRD §10 can be added without re-architecting.
