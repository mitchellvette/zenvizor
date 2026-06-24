# CLAUDE.md — ZenVizor

Project conventions and standing constraints for Claude Code. Read this before doing work in this repo. The full spec is in `zenvizor-prd.md`; the build sequence and QA gates are in `zenvizor-sprint-plan.md`.

---

## What this project is

ZenVizor is a lightweight, **passive** Windows network monitor/reporter. It attributes up/down network traffic to the originating process/service, stores history locally in SQLite, shows a near-live dashboard, and produces daily reports. It is **not** a firewall — there is no blocking, shaping, or active intervention of any kind.

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

### Intentional design tension — DB ACL vs IPC INTERACTIVE access

The `zenvizor.db` file is ACL'd to **SYSTEM + Administrators only**
(`ProgramDataAcl.EnsureDirectoryWithAcl`). The IPC named pipe is ACL'd
to **SYSTEM + Administrators full + INTERACTIVE read/write**
(`ZenVizorPipeServer.BuildPipeSecurity`) so the non-elevated desktop UI
and `zvctl` can connect. **That means any logged-in interactive user
can read every byte of report data through IPC, while the underlying
`.db` is unreadable to them.** This is intentional, not a leak:

- **The ACL protects the raw file**, not the data. We don't want a
  standard user able to point sqlite3 at the DB and corrupt the schema,
  exhaust disk on the page cache, or trigger a WAL replay race against
  the service. A read-side leak via IPC is not on the same threat
  surface.
- **Invariant 3 ("UI has NO database access") is the gate that lets
  this work.** All data flows through `IZenVizorIpc`; the surface
  validates arguments, sanitizes errors, and bounds query cost. We
  audit ONE surface for data exposure, not two.
- **Without the IPC ACL grant the UI can't run as a standard user.**
  Requiring elevation for the dashboard would push users toward Run As
  Administrator, which is materially worse for everything else.

If a future feature adds data the user shouldn't see (a hypothetical
multi-tenant build, secrets-in-DB), the right move is to gate at the
IPC handler — not to tighten the pipe ACL and break the non-elevated
UI contract.

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
ZenVizor.sln
  src/
    ZenVizor.Service/        # Windows Service host (LocalSystem)
    ZenVizor.Capture/        # ICaptureSource: ETW source + synthetic source
    ZenVizor.Attribution/    # PID correction, svchost resolution, signer/path
    ZenVizor.Core/           # aggregation, rollups, alert pipeline, domain models
    ZenVizor.Storage/        # SQLite, migrations, repositories
    ZenVizor.Ipc.Contracts/  # versioned IPC contract — single source of truth
    ZenVizor.Ipc.Server/     # named-pipe + StreamJsonRpc server
    ZenVizor.Ipc.Client/     # named-pipe + StreamJsonRpc client
    ZenVizor.Ui/             # WPF + WPF-UI + LiveCharts2, system tray
    ZenVizor.Cli/            # zvctl — CLI client for QA/automation
  tests/
    ZenVizor.Core.Tests/
    ZenVizor.Attribution.Tests/
    ZenVizor.Storage.Tests/
    ZenVizor.Ipc.Tests/            # contract tests, no real pipe
    ZenVizor.Integration.Tests/    # pipe round-trips, synthetic end-to-end
  installer/                # NOT YET CREATED — Phase 6 (WiX project lands then)
  .github/workflows/        # CI
```

Runtime data lives under `%ProgramData%\ZenVizor\` (DB + config), ACL'd to SYSTEM + Administrators.

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
# Build (solution file is ZenVizor.slnx — the new SDK-style format, NOT .sln;
# `dotnet build ZenVizor.sln` fails with MSB1009 "Project file does not exist")
dotnet build ZenVizor.slnx -c Release

# Headless tests (these run in CI — must pass before advancing a phase)
dotnet test ZenVizor.slnx -c Release

# Installer (must be CLI-drivable when it exists — Phase 6)
wix build ...   # produces the .msi artifact

# Service control (dev)
sc.exe create / start / stop / delete   # or the provided install scripts

# CLI client for manual/scripted QA
zvctl ping
zvctl snapshot
zvctl <command> ...
```

---

## Testing conventions

- **Headless-first.** All core logic (attribution, aggregation, rollups, retention) must be testable on CI with **no live ETW and no elevation**, driven by the **synthetic `ICaptureSource`** with recorded/handcrafted event streams. Live kernel-ETW is unreliable on GitHub Actions and must not be a CI dependency.
- **IPC contract tests run in-process** (no real pipe). Pipe round-trips go in the integration test project.
- **Storage tests** use a temporary SQLite file, not the real `%ProgramData%` DB.
- **Manual gates are real and required.** Live ETW capture, installer install/uninstall, performance budgets, and the zero-own-traffic self-monitoring check are verified by the human on a real Windows box per the Sprint Plan. Don't mark a phase done on CI alone.
- Determinism: synthetic-event tests must assert **exact** expected rows, not approximate.

---

## Design system: source of truth

ZenVizor has two design-token surfaces — the app and Claude Design mocks — and they must not drift:

- **`src/ZenVizor.Ui/Resources/DesignTokens.xaml`** is canonical **for the app** (and `HighContrast.xaml` for the HC variant).
- **`docs/design/colors_and_type.css`** is canonical **for Claude Design mockups**; `docs/design/SKILL.md` is a thin pointer at that CSS, not a third source.
- The crosswalk in the `colors_and_type.css` header is the bridge: it records every value delta between the two and the migration direction. When you change a token value in either file, update the other and the crosswalk in the same commit.
- `docs/design-system.md` is the human-readable companion to DesignTokens.xaml — keep it in sync when XAML keys change. `docs/claude-design-primer.md` is the paste-into-Claude-Design projection of `colors_and_type.css` — keep it in sync when CSS variables change.

### UI conventions worth honouring without re-litigating

- **Popover-style UI larger than a tiny anchored menu uses an in-page `<Grid>` overlay, NOT `<Popup>`.** `<Popup>` with `AllowsTransparency=True` lives in its own HWND that flies outside the window on smaller sizes, and nested popups (a CalendarDatePicker inside a Popup) leak clicks through z-order quirks. Backdrop dismiss requires a matched MouseDown + MouseUp on the backdrop (partial events don't count) — see `docs/design-system.md` §9 "Modal / popover overlay pattern" for the canonical recipe and reference implementation. Small anchored menus (the existing `InfoPopup` / `AnchorDatePopup`) can stay on `<Popup>`; the rule applies to new larger surfaces.

---

## Performance budget (enforce, don't drift)

- Idle CPU **< 1%** on a typical desktop.
- Service working set **< ~80 MB**.
- No per-event DB writes; signature verification cached per app (never per event).

---

## Workflow expectations

- **Work phase by phase** per `zenvizor-sprint-plan.md`. Each phase has CI and manual acceptance criteria; do not start a later phase before the current one's criteria pass.
- **The human personally QAs each milestone.** Produce the artifacts and the means to verify them (CLI commands, test output, clear "how to check" notes) at each phase boundary.
- **Surface tool dependencies up front, before the user runs the validation.** When a manual gate's commands require an external tool that isn't built into Windows (e.g. `psexec`, `accesschk`, `wix`, `signtool`), check whether it's installed *first* and tell the user how to get it (winget command preferred) *before* they try to run the steps. This applies to verification docs (`docs/phase-*-verification.md`) and to any inline instructions in chat — anything the user is about to copy/paste. If a fallback that uses only built-in tools exists, mention it alongside the primary path. Reason: a missing dependency mid-validation forces the user to context-switch into install/troubleshoot, and the validation ends up taking 5x as long as it should.
- When something is ambiguous or a request conflicts with an invariant above, **stop and ask** rather than guessing.
- Keep `ZenVizor.Ipc.Contracts` the single source of truth for the IPC surface; service, UI, and `zvctl` all depend on it.
- Update the PRD/Sprint Plan if a decision changes — don't let code and docs drift.

---

## Versioning

Product SemVer policy and the orthogonal IPC schema version rules are in `docs/versioning.md`. The single source of truth for the product version is `<Version>` in `Directory.Build.props`; both WiX projects pick it up through `$(var.Version)`.

---

## Licensing

ZenVizor is licensed **GPL-3.0-or-later** (see `LICENSE` at repo root). The LICENSE includes a §7 additional permission for the WiX Toolset installer-runtime combination. "ZenVizor" + the ZenVizor logo are common-law trademarks of Mitchell Gray and are NOT licensed under the GPL (see `TRADEMARK.md`). Third-party dependencies and their licenses are tabulated in `NOTICES.md`. Every `.cs` file under `src/` and `tests/` carries `// SPDX-License-Identifier: GPL-3.0-or-later` on line 1 — preserve that header when editing existing files and add it to new files. The WiX OSMF question is orthogonal and remains documented separately in `docs/licensing-wix-osmf.md`.

---

## Terminal / PowerShell gotchas (when proposing copy-paste commands)

Reproducible paste-into-PowerShell failures we've hit on this project. Don't hand the user a command that triggers one of these.

- **Never propose multi-line PowerShell here-strings (`@'…'@`) for the user to copy-paste into an interactive terminal.** The closing `'@` must land at column 0 on its own fresh input line. Real-world paste behavior (Windows Terminal, rendered markdown vs. raw `.md`, IDE selection sources) routinely drops or indents that token, leaving PowerShell stuck in a `>>` continuation prompt the user can't escape without Ctrl+C. This has bitten us at least twice (Phase 2 gate walkthrough, 2026-06-01).
  - **Prefer:** single-line invocations with the SQL or payload as a double-quoted argument. For `sqlite3.exe`, use CLI flags (`-readonly -header -column`) instead of the dot-commands you'd type at the `sqlite>` prompt — e.g. `sqlite3.exe -readonly -header -column $db "SELECT ..."` covers `.headers on` + `.mode column` + the query in one line.
  - **If multi-line input is genuinely required**, stage it via a one-line `Set-Content` from an array of single-line strings (e.g. `'line1','line2' | Set-Content path.sql`), then point the tool at the file (`sqlite3.exe ... ".read path.sql"`).
- **Markdown ` ```sql ` blocks paste differently than ` ```powershell ` blocks** depending on whether the source is the rendered IDE preview or the raw `.md` source. When the user is going to copy from the doc, the doc should already wrap the SQL in a PowerShell-callable form. Don't rely on the user knowing which surface to copy from.
- **Don't assume the user is in an elevated shell** just because they were a minute ago — sessions get reopened. Whenever a command needs admin (e.g. reading `C:\ProgramData\ZenVizor\zenvizor.db`, which is ACL'd to SYSTEM + Administrators only), say so explicitly in the same message that contains the command.

---

## Naming note

The project is **ZenVizor** (renamed 2026-06-01 from the working title "TitaniRun"). All identifiers are consistent: solution/project names (`ZenVizor.*`), the `%ProgramData%\ZenVizor\` data dir, the `zvctl` CLI, the Windows Service name (`ZenVizor`), the named pipe (`ZenVizor.Ipc.v1`), and (future) installer identifiers. If you find a stray `TitaniRun` / `titanirun` / `trctl` reference in source, scripts, or docs, treat it as a rename miss and fix it.
