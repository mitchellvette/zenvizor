# CLAUDE.md — TitaniRun

Project conventions and standing constraints for Claude Code. Read this before doing work in this repo. The full spec is in `titanirun-prd.md`; the build sequence and QA gates are in `titanirun-sprint-plan.md`.

---

## What this project is

TitaniRun is a lightweight, **passive** Windows network monitor/reporter. It attributes up/down network traffic to the originating process/service, stores history locally in SQLite, shows a near-live dashboard, and produces daily reports. It is **not** a firewall — there is no blocking, shaping, or active intervention of any kind.

---

## Non-negotiable invariants

These are not preferences. Do not violate them, and do not "temporarily" violate them to get something working.

1. **The application emits ZERO network traffic of its own.** No telemetry, no update checks, no DNS lookups, no loopback sockets, no "phone home." This is enforced as a test gate (see Sprint Plan, Phases 3 and 6): the tool pointed at itself must report no outbound from its own processes. If a library you're about to add makes network calls, stop and flag it.
2. **IPC is named pipes only.** Never loopback TCP / sockets / gRPC-over-TCP — those traverse the IP stack, get observed by our own capture engine, and lack OS-enforced caller identity. Use `NamedPipeServerStream` + StreamJsonRpc, secured by pipe ACLs.
3. **The UI is non-elevated and has NO database access.** All data reaches the UI over IPC. The SQLite DB is owned exclusively by the service and ACL'd to SYSTEM + Administrators. Never add a code path where the UI opens the DB directly.
4. **No per-event database writes.** Aggregate in memory; flush on the interval (default ~5s into 60s buckets). The hot path must not hit disk per event.
5. **Honest attribution — never fabricate precision.** When a single svchost PID hosts multiple services, list them and report the PID's byte total; do NOT split bytes across co-hosted services. Traffic from injected/host-surfaced code (DLL injection, LOLBins) attributes to the host process — that is a known, documented boundary, not a bug to "fix."
6. **Offline signature verification only.** `WinVerifyTrust` with `WTD_REVOKE_NONE` — revocation checks require network and are forbidden (see invariant 1).

If a requested change appears to conflict with any of these, surface the conflict before implementing.

---

## Tech stack (pinned)

- **Runtime/language:** C# 14 / .NET 10 (LTS). Target `net10.0-windows`.
- **UI:** WPF (native — no web/Electron/webview) + `Wpf.Ui` (lepoco) v4.x for Fluent theming + LiveCharts2 for charts.
- **Capture:** ETW via `Microsoft.Diagnostics.Tracing.TraceEvent`, provider `Microsoft-Windows-Kernel-Network`.
- **PID correction:** IP Helper API — `GetExtendedTcpTable` / `GetExtendedUdpTable`.
- **Service resolution:** SCM / WMI `Win32_Service` (`QueryServiceStatusProcess`).
- **IPC:** named pipes + StreamJsonRpc.
- **Storage:** SQLite.
- **Installer:** WiX Toolset (`.msi`), must be `wix build` CLI-drivable.
- **CI:** GitHub Actions.

Pin dependency versions in the project files. Do not introduce a dependency that makes network calls at runtime.

---

## Repository layout

```
TitaniRun.sln
  src/
    TitaniRun.Service/        # Windows Service host (LocalSystem)
    TitaniRun.Capture/        # ICaptureSource: ETW source + synthetic source
    TitaniRun.Attribution/    # PID correction, svchost resolution, signer/path
    TitaniRun.Core/           # aggregation, rollups, alert pipeline, domain models
    TitaniRun.Storage/        # SQLite, migrations, repositories
    TitaniRun.Ipc.Contracts/  # versioned IPC contract — single source of truth
    TitaniRun.Ipc.Server/     # named-pipe + StreamJsonRpc server
    TitaniRun.Ipc.Client/     # named-pipe + StreamJsonRpc client
    TitaniRun.Ui/             # WPF + WPF-UI + LiveCharts2, system tray
    TitaniRun.Cli/            # trctl — CLI client for QA/automation
  tests/
    TitaniRun.Core.Tests/
    TitaniRun.Attribution.Tests/
    TitaniRun.Storage.Tests/
    TitaniRun.Ipc.Tests/            # contract tests, no real pipe
    TitaniRun.Integration.Tests/    # pipe round-trips, synthetic end-to-end
  installer/
    TitaniRun.Installer/      # WiX project
  .github/workflows/          # CI
```

Runtime data lives under `%ProgramData%\TitaniRun\` (DB + config), ACL'd to SYSTEM + Administrators.

---

## The three architectural seams (keep them clean)

Build these as specified; they exist so post-MVP modules slot in without re-architecting. Do not collapse or shortcut them.

1. **`IMonitor`** — collector lifecycle (`Start`/`Stop`) emitting typed observations. Capture is the first implementation; future passive watchers (hosts-file, proxy, ARP-cache) are just more implementations.
2. **Alert pipeline + `Alert` entity** — generic raise/acknowledge path over IPC with a UI feed. MVP wires exactly one real alert (unsigned binary from a user-writable path making connections).
3. **Versioned IPC envelope** — every message carries a type discriminator + schema version; storage tolerates new entity tables. The `devices` table is **reserved** (defined, not populated).

**Do NOT build a network-egress seam/hook.** Active-probe features (Network Scanner, Evil Twin active detection) are out of scope and, if ever added, live in an isolated explicitly-network module exempt from the self-monitoring assertion. The right preparation is a clean boundary, not a speculative hook.

---

## Build, test, run

> Use the actual scripts/targets as they land; the commands below are the intended shape.

```bash
# Build
dotnet build TitaniRun.sln -c Release

# Headless tests (these run in CI — must pass before advancing a phase)
dotnet test TitaniRun.sln -c Release

# Installer (must be CLI-drivable)
wix build ...   # produces the .msi artifact

# Service control (dev)
sc.exe create / start / stop / delete   # or the provided install scripts

# CLI client for manual/scripted QA
trctl ping
trctl snapshot
trctl <command> ...
```

---

## Testing conventions

- **Headless-first.** All core logic (attribution, aggregation, rollups, retention) must be testable on CI with **no live ETW and no elevation**, driven by the **synthetic `ICaptureSource`** with recorded/handcrafted event streams. Live kernel-ETW is unreliable on GitHub Actions and must not be a CI dependency.
- **IPC contract tests run in-process** (no real pipe). Pipe round-trips go in the integration test project.
- **Storage tests** use a temporary SQLite file, not the real `%ProgramData%` DB.
- **Manual gates are real and required.** Live ETW capture, installer install/uninstall, performance budgets, and the zero-own-traffic self-monitoring check are verified by the human on a real Windows box per the Sprint Plan. Don't mark a phase done on CI alone.
- Determinism: synthetic-event tests must assert **exact** expected rows, not approximate.

---

## Performance budget (enforce, don't drift)

- Idle CPU **< 1%** on a typical desktop.
- Service working set **< ~80 MB**.
- No per-event DB writes; signature verification cached per app (never per event).

---

## Workflow expectations

- **Work phase by phase** per `titanirun-sprint-plan.md`. Each phase has CI and manual acceptance criteria; do not start a later phase before the current one's criteria pass.
- **The human personally QAs each milestone.** Produce the artifacts and the means to verify them (CLI commands, test output, clear "how to check" notes) at each phase boundary.
- When something is ambiguous or a request conflicts with an invariant above, **stop and ask** rather than guessing.
- Keep `TitaniRun.Ipc.Contracts` the single source of truth for the IPC surface; service, UI, and `trctl` all depend on it.
- Update the PRD/Sprint Plan if a decision changes — don't let code and docs drift.

---

## Naming note

"TitaniRun" is a working title. If it changes, it affects: solution/project names (`TitaniRun.*`), the `%ProgramData%\TitaniRun\` path, the `trctl` CLI name, the service name, and installer identifiers. Keep these consistent.
