# Phase 0 — Manual QA verification

Phase 0 produces a buildable solution, a do-nothing service, a launchable UI, an
in-process IPC contract, and a SQLite migration runner. CI covers the headless
gates; this doc walks through the three **manual** gates from the Sprint Plan.

> Run everything below from an **elevated PowerShell** unless noted. `zvctl`
> commands work from a normal shell since they only consume the pipe as an
> interactive user.

---

## 0. One-time build

```powershell
dotnet build .\ZenVizor.slnx -c Release
dotnet test  .\ZenVizor.slnx -c Release
```

`dotnet test` should report:

- `ZenVizor.Storage.Tests` — 8/8 pass (covers the migration runner CI gate).
- `ZenVizor.Ipc.Tests` — 11/11 pass (covers the in-process IPC version-negotiation CI gate).
- Other test projects: 0 tests (placeholders for later phases).

---

## 1. Service install / start / stop / uninstall + startup log line

> Sprint Plan, Phase 0 manual gate:
> *"Service installs, starts, stops, uninstalls via CLI; writes a startup log line."*

From an **elevated** PowerShell:

```powershell
# Install + start (dev path; the real .msi is a Phase 6 deliverable).
.\scripts\install-dev.ps1
```

Verify the service is running:

```powershell
sc.exe query ZenVizor
# STATE should be: 4  RUNNING
```

Verify the startup log line in **both** sinks:

```powershell
# Event Viewer (Application log) — source "ZenVizor":
Get-EventLog -LogName Application -Source ZenVizor -Newest 3 |
    Format-List TimeGenerated, EntryType, Message

# Serilog rolling file under %ProgramData%\ZenVizor\logs\:
Get-Content "$env:ProgramData\ZenVizor\logs\service-*.log" -Tail 10
```

You should see a line containing:

> `ZenVizor service started. DbPath=...\zenvizor.db Pipe=\\.\pipe\ZenVizor.Ipc.v1`

Confirm stop/uninstall:

```powershell
.\scripts\uninstall-dev.ps1
sc.exe query ZenVizor
# Should report: 1060 — service does not exist
```

The DB at `%ProgramData%\ZenVizor\zenvizor.db` is preserved by default. Pass
`-PurgeData` to `uninstall-dev.ps1` to remove the data directory.

---

## 2. UI launches, tray works, close-to-tray works, Exit quits

> Sprint Plan, Phase 0 manual gate:
> *"UI launches, shows shell + tray icon, close-to-tray works, Exit quits."*

Reinstall the service if you uninstalled it (`.\scripts\install-dev.ps1`).

From a **non-elevated** terminal:

```powershell
dotnet run --project .\src\ZenVizor.Ui\ZenVizor.Ui.csproj -c Release
```

Or run the built EXE directly:

```powershell
& .\src\ZenVizor.Ui\bin\Release\net10.0-windows\ZenVizor.Ui.exe
```

Verify, in order:

- [x] Window appears with ZenVizor title bar and a left-side NavigationView containing **Dashboard, Per-App, History, Reports, Alerts, Settings**.
- [x] A ZenVizor tray icon appears in the system tray (notification area).
- [x] The bottom-bar status shows **"Service: connected (0.1.0, proto 1.0)"** within ~5 seconds (or **"disconnected …"** if the service is not running).
- [x] Clicking the title-bar **X** hides the window but the tray icon remains (close-to-tray).
- [x] Left-clicking the tray icon **restores** the window.
- [x] Right-clicking the tray → **Show ZenVizor** also restores.
- [x] Right-clicking the tray → **Exit** terminates the process (verify with Task Manager — `ZenVizor.Ui` is gone).

---

## 3. `zvctl` round-trips + unauthorized access is rejected

> Sprint Plan, Phase 0 manual gate:
> *"`zvctl ping` round-trips over the real named pipe; unauthorized/unACL'd access is rejected."*

### 3a. Round-trip

```powershell
# Build the CLI once:
dotnet build .\src\ZenVizor.Cli\ZenVizor.Cli.csproj -c Release

# Run it (named output is zvctl.dll/zvctl.exe):
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe ping
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe status
```

`ping` prints a single `pong  (Nms)  server-ts <unix-ms>` line and exits 0.
`status` prints the service metadata block. Exit code 3 means the service
isn't running; exit code 2 means version mismatch.

### 3b. Unauthorized-access negative test

The pipe ACL grants connect rights to SYSTEM, Administrators, and the
**Interactive** group only — i.e. a logged-in user at the console. Anonymous
and Network principals get no rule and are rejected by Windows.

The cleanest local check is to attempt a connection from a non-interactive
context. Two convenient ways:

**Pre-flight — does `psexec.exe` exist on PATH?**

```powershell
Get-Command psexec.exe -ErrorAction SilentlyContinue
```

- If it prints a path → you're set for **Option A** below.
- If it prints nothing → either install Sysinternals (one-time, ~30s) or use
  **Option B** which uses only built-in `schtasks`:
  ```powershell
  winget install --id Microsoft.Sysinternals.PsTools
  # then open a NEW elevated PowerShell so PATH refreshes
  ```

**Option A — psexec as `NT AUTHORITY\NetworkService`:**

```powershell
# Requires Sysinternals psexec on PATH (see pre-flight above).
& psexec.exe -u "NT AUTHORITY\NetworkService" `
    -accepteula -nobanner `
    powershell.exe -Command `
    "& '.\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe' ping"
```

Expected: `zvctl` exits non-zero with an UnauthorizedAccessException or pipe
connect failure (NetworkService is not in the Interactive group).

**Option B — schtasks one-shot as a non-interactive account:**

```powershell
# Run zvctl from the Task Scheduler as NetworkService and inspect the result.
$exe = (Resolve-Path .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe).Path
schtasks /create /tn ZenVizorPipeAclTest /ru "NT AUTHORITY\NetworkService" `
    /tr "`"$exe`" ping" /sc once /st 23:59 /f | Out-Null
schtasks /run /tn ZenVizorPipeAclTest
Start-Sleep -Seconds 2
schtasks /query /tn ZenVizorPipeAclTest /v /fo LIST | Select-String "Last Result"
schtasks /delete /tn ZenVizorPipeAclTest /f | Out-Null
```

The reported "Last Result" should be **non-zero** (the non-interactive account
fails to open the pipe). A zero result means the ACL is too permissive — file
a bug.

---

## Cleanup after QA

```powershell
.\scripts\uninstall-dev.ps1            # keep DB and logs
.\scripts\uninstall-dev.ps1 -PurgeData # full wipe
```

---

## What is NOT verified in Phase 0

Phase 0 doesn't enforce these — they belong to later phases and intentionally
have no implementation yet:

- ETW capture / per-process byte attribution (Phase 1).
- svchost service-name resolution + signer/path enrichment (Phase 2).
- Live activity snapshot data path (Phase 3) — the UI status bar uses
  `GetServiceStatus`, not `GetCurrentActivitySnapshot`.
- The **zero-own-traffic** self-monitoring gate (Phase 3 + Phase 6) — there's
  no capture engine yet, so there's nothing to point at the service.
- Installer .msi (Phase 6) — dev install/uninstall is via `sc.exe` for now.
