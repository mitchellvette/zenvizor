# ZenVizor — Product Requirements Document

**Project name:** ZenVizor (renamed from working title "TitaniRun" on 2026-06-01)
**Document type:** PRD (companion to `zenvizor-sprint-plan.md`)
**Status:** Scoping complete — no open decisions outstanding
**Last updated:** 2026-06-01

> Companion document: the phased milestone/sprint plan with per-phase acceptance criteria lives in **`zenvizor-sprint-plan.md`**.

---

## 1. Summary

ZenVizor is a lightweight, **passive** network monitoring and reporting tool for Windows. It observes inbound/outbound network traffic, attributes it to the originating application or service, stores history locally, and produces daily overview reports plus a near-live activity view. Conceptually it is a pared-down GlassWire focused exclusively on **visibility** — it has no firewall, no blocking, and emits **no network traffic of its own**.

The product exists to give a user insight into **potentially unapproved or extraneous network activity** — malware running as its own process, benign-but-leeching background processes (updaters, telemetry, sync clients), and unexpected bandwidth from otherwise-trusted processes — so the user can ask "why is *X* talking to the network, and that much?"

### 1.1 Founding invariant (non-negotiable)

> **The application generates no network traffic of its own.** All monitoring is local and passive. This is a first-class, *testable* acceptance criterion: the tool, pointed at itself, must report zero outbound activity from its own processes. No feature may violate this in the MVP. Any future feature that would emit traffic (e.g., active network scanning) must live behind a hard, explicitly-labeled boundary and is out of scope here.

---

## 2. Locked technical decisions

These are settled inputs, not up for relitigation.

| Area | Decision |
|---|---|
| Language / runtime | C# 14 / **.NET 10 (LTS)** — current LTS as of Nov 2025, latest patch 10.0.8 (May 2026), supported to Nov 2028 |
| Target framework | `net10.0-windows` for both service and UI |
| UI | **WPF**, native. No Electron/Tauri/webview. |
| UI theming | **WPF-UI** (`Wpf.Ui`, lepoco) v4.x — Fluent, clean light modern look, MIT |
| Charting | **LiveCharts2** — WPF-native, good live-update story |
| Capture engine | **ETW** via `Microsoft.Diagnostics.Tracing.TraceEvent`, provider `Microsoft-Windows-Kernel-Network` |
| PID correction | **IP Helper API** — `GetExtendedTcpTable` / `GetExtendedUdpTable` |
| Service-name resolution | SCM / WMI `Win32_Service` (`QueryServiceStatusProcess`) for svchost hosts |
| Signature/path enrichment | `WinVerifyTrust` (Authenticode), revocation checks **disabled** (`WTD_REVOKE_NONE`) to honor no-network |
| Architecture | Elevated background **Windows Service** (capture) + non-elevated **WPF UI client** (display), separated |
| IPC | **Named pipes** (`NamedPipeServerStream`) + **StreamJsonRpc**, secured by pipe ACLs |
| Storage | Local **SQLite**, service-owned, ACL'd, tiered retention |
| Build / CI | git + GitHub + GitHub Actions |
| Installer | **WiX Toolset** (.msi), `wix build` CLI-drivable |
| Toolchain posture | CLI-drivable and headlessly-testable throughout (built via Claude Code) |

---

## 3. Goals & non-goals

### 3.1 Goals (MVP)
- Continuously, passively capture up/down traffic and attribute it to the originating process.
- Resolve svchost-hosted traffic to **service names**, not just "svchost.exe".
- Enrich each process with **signer/publisher + signature validity + path heuristic** (e.g., unsigned binary running from a temp/user-writable location).
- Provide a **near-live activity view** (small delay/aggregation acceptable) and a **daily overview report**.
- Store history locally with **user-defined query windows** and configurable retention.
- Run quietly in the background with a small footprint and **no network usage of its own**.
- Be installable/uninstallable cleanly via a scriptable .msi.

### 3.2 Non-goals / explicit out-of-scope (MVP)
- **No firewall, blocking, or traffic shaping.** Visibility only.
- **No traffic of the tool's own.** (See §1.1.)
- **No attribution of injected/host-surfaced code.** Traffic from DLL injection, process hollowing, or living-off-the-land binaries (`rundll32`, `regsvr32`, `mshta`, `powershell`) attributes to the **host process**, not the injected payload. ETW kernel-network + the connection table cannot see inside a process; no tool in this class can without kernel callbacks / stack-walking / threat-intel ETW (a different, much larger project). This is a known boundary, not a defect. The tool still adds value here by surfacing *anomalous bandwidth from a host process* ("why is this process using so much?").
- **No exact byte-splitting across services genuinely co-hosted in one PID.** When several services share one svchost PID, we present the byte total for the PID plus the list of hosted services, honestly labeled — we do not fabricate a per-service split. (On Win10 1703+ with >3.5 GB RAM most services get their own PID, so this is usually unambiguous anyway.)
- **No real-time certificate revocation checking** (revocation requires network; we verify signature validity offline).
- **No active network/device scanning, reverse DNS, or any egress** (deferred behind a hard boundary; see §10).
- **No PDF export** (deferred indefinitely; CSV/HTML only).

---

## 4. Personas & primary use

- **The hands-on power user / admin (primary):** runs the tool in the background, checks the dashboard occasionally, reviews the daily report, and investigates anything that looks off by drilling into a process's connections and history.
- **Usage mode:** "background tool I check in on," **not** "app I sit inside all day." The UI optimizes for fast answers to "what's talking, how much, and is that expected?"

---

## 5. Architecture

### 5.1 Component overview

```
+-------------------------------------------------------------+
|  ZenVizor Service   (LocalSystem, elevated)                |
|                                                             |
|  ETW Session (Kernel-Network)                               |
|        |                                                    |
|   ICaptureSource  <----[ real ETW | synthetic/recorded ]    |
|        |                                                    |
|   Attribution pipeline                                      |
|     - PID correction (IP Helper TCP/UDP tables)             |
|     - svchost -> service-name resolution (SCM/WMI)          |
|     - process identity + signer/path enrichment             |
|        |                                                    |
|   In-memory rolling aggregate (current window)              |
|        |                                                    |
|   Flush (~5s) ----> SQLite (ProgramData, ACL'd)             |
|        |                  ^                                  |
|   Rollup jobs (hourly/daily) + retention/purge             |
|        |                                                    |
|   Alert pipeline (IMonitor observations -> Alert entities)  |
|        |                                                    |
|   Named-pipe IPC server (StreamJsonRpc, pipe ACL)           |
+----------------------------|--------------------------------+
                             |  named pipe (no IP stack)
+----------------------------|--------------------------------+
|  ZenVizor UI   (non-elevated, interactive user)            |
|                                                             |
|  IPC client (StreamJsonRpc)                                 |
|   - live snapshot (pull/poll or server push)                |
|   - history/report queries                                  |
|   - alerts feed (push)                                      |
|   - settings / control                                      |
|                                                             |
|  WPF + WPF-UI views, LiveCharts2, system tray               |
+-------------------------------------------------------------+
```

### 5.2 Service (capture + data owner)
- Runs as **LocalSystem**, **auto-start configurable** (default on; a user who wants ultra-fast boot can disable startup — see Settings).
- Owns the **only** read/write access to the SQLite DB (UI never touches the DB directly).
- Hosts the ETW kernel session (requires elevation), the attribution pipeline, the in-memory rolling aggregate, the flush/rollup/retention jobs, the alert pipeline, and the named-pipe server.
- Designed so the entire attribution/aggregation core runs against an `ICaptureSource` abstraction, allowing **synthetic/recorded events** for headless tests (live kernel-ETW is unreliable-to-impossible on CI runners).

### 5.3 UI client (display only)
- Runs **non-elevated**, as the interactive user, with a **system-tray** presence.
- Holds **no privileges** and **no DB access** — every piece of data comes over IPC. This keeps the sensitive history DB locked to SYSTEM/admins and keeps the UI a "dumb terminal."
- Close-to-tray by default; explicit **Exit** in the tray menu; optional toast on alerts.

### 5.4 The service/UI privilege split & why named pipes
- The IPC endpoint is an **elevated, security-adjacent** surface. Named pipes carry **OS-enforced caller identity** (pipe ACLs + server-side impersonation), which is the clean way to police the SYSTEM↔non-elevated boundary — no hand-rolled auth.
- Loopback TCP was rejected for two reasons: (1) it would be **observed by our own capture engine** (a network monitor reporting its own localhost IPC is both embarrassing and a permanent self-filtering chore), and (2) loopback has no inherent caller identity, forcing a custom auth handshake on a security-sensitive endpoint.
- CLI-testability is preserved without loopback: the IPC is defined as a message **contract** exercised by in-process contract tests (no real pipe needed), plus a small companion CLI (`zvctl`) that opens the pipe for manual/integration QA.

### 5.5 Solution / project layout (proposed)

```
ZenVizor.sln
  src/
    ZenVizor.Service/        # Windows Service host (LocalSystem)
    ZenVizor.Capture/        # ICaptureSource, ETW source, synthetic source
    ZenVizor.Attribution/    # PID correction, svchost resolution, signer/path
    ZenVizor.Core/           # aggregation, rollups, alert pipeline, domain models
    ZenVizor.Storage/        # SQLite, migrations, query/repository layer
    ZenVizor.Ipc.Contracts/  # versioned IPC contract (shared by service + UI + CLI)
    ZenVizor.Ipc.Server/     # named-pipe + StreamJsonRpc server
    ZenVizor.Ipc.Client/     # named-pipe + StreamJsonRpc client
    ZenVizor.Ui/             # WPF + WPF-UI + LiveCharts2, tray
    ZenVizor.Cli/            # zvctl — CLI client for QA/automation
  tests/
    ZenVizor.Core.Tests/
    ZenVizor.Attribution.Tests/
    ZenVizor.Storage.Tests/
    ZenVizor.Ipc.Tests/      # contract tests, no real pipe
    ZenVizor.Integration.Tests/  # pipe round-trips, synthetic-source end-to-end
  installer/
    ZenVizor.Installer/      # WiX project
  .github/workflows/          # GitHub Actions CI
```

---

## 6. The three structural seams (built now, justified on their own merits)

These are designed in from the start because they are good architecture regardless of whether the post-MVP modules ever land — and expensive to retrofit. **No egress seam is built** (see §10).

1. **Collector/monitor contract — `IMonitor`.** Lifecycle (`Start`/`Stop`) + emits typed observations. The capture engine is the *first* `IMonitor`. Every future passive watcher (hosts-file, proxy, ARP-cache) is just another implementation, no core changes.
2. **Alert/event pipeline + `Alert` entity.** "Unsigned process from temp is making connections," and (later) "new device appeared," "hosts file changed," "duplicate MAC in ARP table," "proxy setting changed" are all **alerts** sharing nothing with byte-counting. A small `Alert` entity + an alert message over IPC + a UI feed pane lets the whole future device/file/proxy cluster light up without touching the core. **MVP wires one real first customer:** the unsigned-binary-from-user-writable-path-making-connections alert, derivable purely from data we already have, no new monitor and no network.
3. **Type-versioned IPC envelope + extensible storage.** Every IPC message carries a type discriminator + schema version; the storage layer tolerates new entity tables (e.g., a future `devices` table) without reworking the flow/process schema. The `devices` table is **reserved** in the schema (defined, not populated) so the later device cluster slots in cleanly.

---

## 7. Data model (SQLite)

All timestamps are UTC (stored as integer Unix-ms or ISO-8601 text — implementer's choice, consistent throughout). Byte counts are 64-bit. The schema is migration-managed with a `schema_migrations` table.

### 7.1 `apps` — process identity (deduplicated)
| column | type | notes |
|---|---|---|
| app_id | INTEGER PK | |
| image_path | TEXT | full path, normalized |
| image_name | TEXT | filename |
| publisher | TEXT NULL | Authenticode signer subject, null if unsigned |
| signature_status | TEXT | `Signed` \| `Unsigned` \| `Invalid` \| `Unchecked` |
| is_user_writable_path | INTEGER | heuristic: image under temp/AppData/user-writable (1/0) |
| first_seen | INTEGER | |
| last_seen | INTEGER | |

Unique on `(image_path, publisher)`.

### 7.2 `process_sessions` — a running PID instance
| column | type | notes |
|---|---|---|
| session_id | INTEGER PK | |
| app_id | INTEGER FK -> apps | |
| pid | INTEGER | |
| start_time | INTEGER | |
| end_time | INTEGER NULL | null while alive |
| hosted_services | TEXT NULL | comma-separated service names for svchost hosts |

PIDs are reused by the OS; the session row is what binds a traffic record to a specific process lifetime + its resolved services at that time.

### 7.3 `traffic_samples` — high-res tier (hot path)
| column | type | notes |
|---|---|---|
| sample_id | INTEGER PK | |
| session_id | INTEGER FK -> process_sessions | |
| bucket_start | INTEGER | aligned bucket (default 60s) |
| bytes_up | INTEGER | |
| bytes_down | INTEGER | |
| remote_class | TEXT | `Local` \| `Wan` (handles IPv4 RFC1918 + IPv6 fe80::/10, fc00::/7) |

Written by the flush job (default every ~5s, accumulated into the aligned bucket). Index on `(bucket_start)` and `(session_id, bucket_start)`.

### 7.4 `connections` — drill-down detail (aggregated per endpoint)
| column | type | notes |
|---|---|---|
| connection_id | INTEGER PK | |
| session_id | INTEGER FK | |
| protocol | TEXT | `TCP` \| `UDP` |
| remote_addr | TEXT | IPv4 or IPv6 |
| remote_port | INTEGER | |
| remote_class | TEXT | `Local` \| `Wan` |
| resolved_host | TEXT NULL | populated by the Phase 8 passive-DNS observer for OS-resolver traffic; null when the app uses DoH / an in-app resolver — see §10 endpoint-visibility note |
| bytes_up | INTEGER | running total |
| bytes_down | INTEGER | running total |
| first_seen | INTEGER | |
| last_seen | INTEGER | |

Supports "drill into an app → its connections." Aggregated per `(session_id, protocol, remote_addr, remote_port)`.

### 7.5 `traffic_hourly` / `traffic_daily` — rollup tiers
| column | type | notes |
|---|---|---|
| id | INTEGER PK | |
| app_id | INTEGER FK -> apps | rolled up by app (not session) |
| bucket_start | INTEGER | hour- or day-aligned |
| bytes_up | INTEGER | |
| bytes_down | INTEGER | |
| remote_class | TEXT | `Local` \| `Wan` |

Produced by rollup jobs from `traffic_samples`. These back the daily report and long-window history efficiently.

### 7.6 `alerts`
| column | type | notes |
|---|---|---|
| alert_id | INTEGER PK | |
| type | TEXT | e.g., `UnsignedFromUserPath` (extensible) |
| severity | TEXT | `Info` \| `Warning` \| `Critical` |
| created_at | INTEGER | |
| source_monitor | TEXT | which `IMonitor`/component raised it |
| entity_kind | TEXT | e.g., `App`, `Session`, (future) `Device`, `File` |
| entity_ref | TEXT | id/key of the referenced entity |
| title | TEXT | |
| detail | TEXT | |
| acknowledged_at | INTEGER NULL | |

### 7.7 `devices` — **reserved (defined, not populated in MVP)**
| column | type | notes |
|---|---|---|
| device_id | INTEGER PK | |
| mac | TEXT | |
| ip | TEXT | |
| interface | TEXT | |
| hostname | TEXT NULL | |
| first_seen | INTEGER | |
| last_seen | INTEGER | |
| is_known | INTEGER | user-acknowledged (1/0) |

### 7.8 `settings` — key/value config
| column | type | notes |
|---|---|---|
| key | TEXT PK | |
| value | TEXT | |

Seeded defaults: retention windows, autostart flag mirror, flush/bucket intervals, toast-on-alert toggle.

### 7.9 Retention defaults (all user-configurable)
- `traffic_samples` (high-res): **30 days**
- `connections`: **30 days**
- `traffic_hourly`: **90 days**
- `traffic_daily`: **1 year**
- `alerts`: kept until acknowledged + 90 days, configurable

### 7.10 Storage location & ACL
- DB lives under **`%ProgramData%\ZenVizor\`**, ACL'd to **SYSTEM + Administrators** read/write, **no access for standard users**. This is sensitive data (a record of everywhere every app connected), and it is the reason the UI must route all queries through the service.

---

## 8. Attribution pipeline (the hard part)

Order of operations per observed flow:

1. **ETW kernel-network event** gives bytes + addresses + a PID. On the *receive* path the PID can be wrong/missing (DPC/arbitrary context).
2. **IP Helper correction:** periodic `GetExtendedTcpTable`/`GetExtendedUdpTable` snapshots provide the authoritative `(local endpoint) -> owning PID` map. This is a **correction layer** over the ETW PID, not redundancy — it fixes the receive-path ambiguity.
3. **Process identity:** resolve PID → image path/name via session table; dedupe into `apps`.
4. **svchost service resolution:** if the image is a service host, enumerate services for that PID (`QueryServiceStatusProcess` / WMI `Win32_Service`) and store `hosted_services` on the session. If a single PID genuinely hosts multiple services, store the list and **do not** split bytes among them.
5. **Signer/path enrichment:** `WinVerifyTrust` (offline, `WTD_REVOKE_NONE`) → `signature_status` + `publisher`; path heuristic → `is_user_writable_path`. Cached per `app_id` (cheap, not per-event).
6. **Remote classification:** classify `remote_addr` as `Local` vs `Wan` from address ranges (IPv4 RFC1918/loopback/link-local; IPv6 loopback `::1`, link-local `fe80::/10`, ULA `fc00::/7`). No network call.
7. **Aggregate** into the in-memory rolling window keyed by session; flush to `traffic_samples` + update `connections` on the flush tick.

**IPv6 is in scope throughout** — ETW kernel-network covers v4 and v6; ignoring v6 would create exactly the blind spot the tool exists to remove.

---

## 9. IPC contract (named pipe + StreamJsonRpc)

Single named pipe, server in service, secured by ACL granting connect to interactive users while the server impersonates/validates the caller. All messages flow through a **versioned envelope** (schema version + type discriminator). Two distinct data paths — **live snapshot** vs **history/query** — because they have different freshness and cost profiles.

### 9.1 Methods (client → server)
- **Live:** `GetCurrentActivitySnapshot()` → per-app current rates (bytes/s up/down) over the rolling window. (UI polls this at a modest cadence, or subscribes to push; the service serves it from the in-memory aggregate, **not** by hammering SQLite.)
- **History/query:**
  - `GetAppList(window, filter)` → apps with totals over the window
  - `GetAppDetail(appId, window)` → time series + summary for one app
  - `GetConnections(appId | sessionId, window)` → endpoint drill-down
  - `GetTrafficHistory(window, grain, filter)` → series at sample/hourly/daily grain
- **Reports:** `GetDailyReport(date)` → structured daily overview payload
- **Alerts:** `GetAlerts(filter)`, `AcknowledgeAlert(alertId)`
- **Control/settings:** `GetServiceStatus()`, `GetSettings()`, `UpdateSettings(...)`, `PurgeHistory(before)`

### 9.2 Notifications (server → client, push)
- `AlertRaised(alert)` — drives the alerts feed + optional toast
- `ActivityTick(snapshot)` — optional push of the live snapshot (alternative to client polling)

### 9.3 Versioning
Envelope carries `schema_version`. Server rejects/negotiates incompatible versions. The `Ipc.Contracts` assembly is the single shared source of truth across service, UI, and `zvctl`.

---

## 10. Post-MVP module roadmap (mapped to seams) — **NOT in MVP scope**

Captured here so MVP scaffolding stays aligned; none of these are built in the sprint plan.

| Nice-to-have | Cluster | Emits traffic? | Lands via |
|---|---|---|---|
| Bandwidth/Usage History (user-defined window) | Core extension | No | Already in core (tiered store + query) |
| Summarized Activity While Idle | Core extension | No | Core + a report variant |
| System File Monitor (hosts / lmhosts) | Passive local watcher | No | `IMonitor` + alert pipeline |
| Proxy Settings Monitor | Passive local watcher | No | `IMonitor` + alert pipeline |
| New Device Connection | Passive device watcher | No (reads ARP/neighbor table) | `IMonitor` + `Alert` + `devices` table |
| Device List Monitor | Passive device watcher | No | `IMonitor` + `devices` table |
| ARP Spoofing Detection | Passive device watcher | No (`GetIpNetTable2`) | `IMonitor` + alert pipeline |
| Passive DNS name resolution | Capture extension | No (sniffs DNS responses via `Microsoft-Windows-DNS-Client` ETW) | second `ICaptureSource`; fills `connections.resolved_host`. **Shipped in Phase 8. Known coverage gap: DoH-using and in-app-resolver apps (notably Chrome by default) bypass the Windows resolver entirely and are structurally invisible to this provider.** |
| Endpoint visibility for DoH / in-app resolvers | Capture extension (investigation → ships pre-MVP) | No (passive packet inspection — TLS SNI / QUIC SNI / HTTP Host) | **Phase 8.5 spike complete (2026-06-21): ships pre-MVP at full coverage via Phase 8.6**. Passive only; cleared the invariant #1 audit (self-monitoring lens empty), so the active-probe boundary below does not move. Residual structural gap: ECH-enabled origins. |
| Network Scanner | **Active probe** | **YES** | **Hard-bounded, isolated module — breaks §1.1; separate effort** |
| Evil Twin Detection (active parts) | **Active probe** | **YES** | **Hard-bounded, isolated module — breaks §1.1; separate effort** |

The active-probe items get a **clean boundary, not a pre-built hook** — the right preparation is keeping the MVP core provably passive (self-monitoring asserts zero own-traffic). When they arrive they live in an isolated, explicitly-network module (plausibly a separate process), exempt from the self-monitoring assertion.

**Endpoint visibility — Phase 8 + Phase 8.5 split:** the Phase 8 DNS observer recovers hostnames for the OS-resolver share of traffic. That share is smaller than it looks because modern browsers ship DoH on by default. Phase 8.5 was the pre-MVP investigation that decided whether and how to recover the remaining share via passive packet inspection (TLS / QUIC SNI, HTTP Host); it **completed 2026-06-21 with a ship-pre-MVP-at-full-coverage decision** (implementation scoped as Phase 8.6 in `docs/zenvizor-sprint-plan.md`). The hard constraint is unchanged — every candidate technique must clear the invariant #1 audit before it lands in the MVP, and the chosen technique did (receive-only substrate, pure-compute crypto, empty self-monitoring lens).

### 10.1 Release / distribution follow-ups (post-MVP infra, not product features)

Not modules in the §10 sense — release-engineering ergonomics surfaced during the Phase 9.7 ship. Tracked here so they don't drift.

- **CI tag-triggered Release publication.** The current `.github/workflows/ci.yml` builds the MSI + bundle on every push to `main` and uploads them as 90-day-retention workflow artifacts. Pushing a `vX.Y.Z` tag does **not** auto-publish a GitHub Release with attached binaries — the v1.0.0 Release was created manually via `gh release create`. Add a tag-triggered workflow (`on: push: tags: ['v*']`) that rebuilds the bundle, computes a SHA256, and attaches both the bundle and the MSI to a Release named from the tag. Make a tag the only release-publishing trigger so the artifact story stays in lockstep with version intent. Trigger to do this work: any 1.0.x patch release that would otherwise need a second manual `gh release create`.

---

## 11. UI / information architecture

### 11.1 Primary views (navigation spine)
1. **Dashboard / Current Activity** *(default landing)* — near-live per-app up/down rates (LiveCharts2), top talkers now. Small delay/aggregation acceptable.
2. **Per-App breakdown** — apps ranked by traffic over the selected window; signer/path/signature surfaced; svchost rows show hosted services.
3. **App detail** — drill from an app into its **connections** (endpoints, local/WAN, protocol, bytes) and its **history** time series.
4. **Daily Report** — in-app daily overview for a chosen date.
5. **History / timeline** — user-defined window queries at appropriate grain.
6. **Alerts** — feed of raised alerts; acknowledge.
7. **Settings** — autostart toggle, retention windows, purge history, flush/bucket intervals, toast-on-alert, theme.

Default drill path: **app → its connections → its history.**

### 11.2 Tray & lifecycle
- Lives in the system tray; close-to-tray by default; explicit **Exit** in tray menu; optional toast on alert.

### 11.3 Look & accessibility
- **WPF-UI** Fluent theming, clean light modern look (not stock Windows). DPI/multi-monitor handled by WPF's vector rendering. **Standard accessibility** only (no special mandate beyond WPF defaults).

### 11.4 Export
- Daily report exports to **CSV and HTML**; in-app view always available. **No PDF** (deferred indefinitely).

---

## 12. Non-functional requirements

| Requirement | Target (acceptance-testable) |
|---|---|
| Idle CPU | **< 1%** on a typical desktop at idle |
| Service working set | **< ~80 MB** active **private** working set (Task Manager column, or `\Process(<name>)\Working Set - Private` perf counter — *not* `Get-Process .WorkingSet64`, which double-counts shared CLR / native pages and overreads by ~2.5×) |
| DB write pattern | **No per-event writes** — in-memory aggregation flushed on a fixed interval (default ~5s) |
| Own network usage | **Zero** — tool pointed at itself reports no outbound from its own processes |
| Startup | Service auto-start **user-configurable** (default on) |
| Headless testability | Core attribution/aggregation runs against `ICaptureSource` with synthetic/recorded events in CI |
| Privilege boundary | UI runs non-elevated; DB ACL'd to SYSTEM/Admins; all UI data via IPC |

---

## 13. Security & privacy

- History DB is sensitive; ACL'd to SYSTEM/Administrators, stored in `%ProgramData%`.
- UI is unprivileged and has no DB access; IPC is the only data path.
- Named-pipe ACL restricts callers; server validates caller identity.
- Signature verification is **offline** (no revocation network call).
- User-facing **purge history** and configurable retention windows for data minimization.

---

## 14. Testing strategy

- **Unit/integration (CI, headless):** attribution + aggregation + rollups + retention driven by the **synthetic `ICaptureSource`** with recorded/handcrafted event streams. Storage tests against a temp SQLite file. IPC **contract tests** in-process (no real pipe). Pipe **round-trip** integration tests where the runner allows.
- **Manual smoke (real box, you QA):** live ETW capture, live activity pane vs. real traffic, performance counters, installer install/uninstall, self-monitoring zero-own-traffic check. Live-ETW is *not* reliably runnable on GitHub Actions, so it is a manual gate by design.
- **`zvctl` CLI** drives the service for scripted/manual verification at every phase.

---

## 15. Open decisions

**None.** All structural decisions, the UI block, and version pins are resolved. Two implementation-level defaults locked for the implementer (changeable, low-cost): StreamJsonRpc as the RPC layer; ~5s flush interval into 60s buckets.

---

*Phased build plan with per-phase acceptance criteria: see `zenvizor-sprint-plan.md`.*
