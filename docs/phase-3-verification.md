# Phase 3 — Manual QA verification

Phase 3 makes the dashboard *do something*: the UI shows near-live per-app
rates over the named pipe, `zvctl snapshot` shows the same data, and the
in-memory rolling window is the single source of truth (no SQLite touch on the
snapshot path). CI covers the headless gates (envelope round-trip, rate math,
"no SQLite read" guard). This doc walks the **three manual gates** that must
hold on a real Windows box.

> Run everything below from an **elevated PowerShell** unless the step is
> explicitly marked non-elevated. The service runs as LocalSystem and the DB
> at `C:\ProgramData\ZenVizor\zenvizor.db` is ACL'd to SYSTEM +
> Administrators only — a non-elevated shell gets "unable to open database
> file" even if your account is in the Administrators group (UAC strips the
> admin half of the split token from unprivileged shells).

---

## Pre-flight dependencies

Per the standing CLAUDE.md behavior, check tools *first* — a missing
dependency mid-validation makes the whole exercise take 5× longer.

Phase 3 adds **no new external tool dependencies** beyond what Phases 0–2
already require:

```powershell
# sqlite3 — used in Gate #2 to query the DB for ZenVizor-process traffic rows.
Get-Command sqlite3.exe -ErrorAction SilentlyContinue
#   winget install --id SQLite.SQLite

# curl — used in Gate #1 to generate a known-magnitude download. Ships with
# Windows 10+ as curl.exe (NOT the PowerShell `curl` alias to Invoke-WebRequest).
Get-Command curl.exe -ErrorAction SilentlyContinue

# Built-in (no install): sc.exe, Get-Process, dotnet, Get-Item.
```

---

## 0. One-time build + reinstall

> **Note on a known state issue:** the previous TitaniRun → ZenVizor rename
> (commit `cca5e3e`) moved the working tree from `C:\dev\titanirun` to
> `C:\dev\zenvizor`. If a dev service was registered before the rename, its
> SCM record may still point at the old path. `uninstall-dev.ps1` should
> catch this, but if `sc.exe query ZenVizor` shows the service in a weird
> state below, force a clean reset before continuing:
>
> ```powershell
> Stop-Service ZenVizor -Force -ErrorAction SilentlyContinue
> sc.exe delete ZenVizor
> ```

```powershell
cd C:\dev\zenvizor

dotnet build .\ZenVizor.slnx -c Release
dotnet test  .\ZenVizor.slnx -c Release

# Phase 3 makes no schema changes, so the existing DB is fine. Pass -PurgeData
# if you want to start clean and watch the warming-up banner appear.
.\scripts\uninstall-dev.ps1
.\scripts\install-dev.ps1
sc.exe query ZenVizor                  # confirm STATE 4 RUNNING

# Confirm IPC is healthy and the new snapshot endpoint responds:
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe status
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe snapshot
```

Test totals to expect from `dotnet test`:

- `ZenVizor.Core.Tests` — 71 pass (+13 Phase 3: rolling window + per-app rollup)
- `ZenVizor.Storage.Tests` — 24 pass
- `ZenVizor.Ipc.Tests` — 13 pass (+2 Phase 3: envelope round-trip)
- `ZenVizor.Attribution.Tests` — 40 pass (+10 Phase 3: process lifecycle resolver)
- `ZenVizor.Integration.Tests` — 5 pass (+1 Phase 3: snapshot-path no-SQLite guard)

**Total: 153 pass.**

Let the service run for **at least 10 s** before starting Gate #1 so a flush
has completed and the dashboard isn't in the warming-up state.

---

## Gate #1 — Attribution reliability for short-lived processes

**Why this gate matters.** Earlier in Phase 3 we found a serious bug: a fast
curl that finished in &lt;1 s would sometimes attribute correctly and sometimes
be **silently dropped entirely** — no row in the snapshot, no bytes in the
DB, nothing in the dashboard. Root cause: the image resolver only knew about
a PID once it called `Process.GetProcessById`, which fails after process
exit. ETW delivers network events in batches with hundreds of ms of latency,
so for sub-second processes the first observation routinely arrived *after*
the process exited and the entire attribution failed.

The fix (`ProcessLifecycleResolver`) populates the image cache from kernel
ETW process-start events, before any network event for that PID is delivered.
This gate verifies the fix holds across many runs without juggling terminals.

### Automated runner (one shell)

```powershell
.\scripts\verify-attribution.ps1
```

Default: 5 iterations of a 50 MB curl download, 6 s apart. The script:

1. Snapshots `ObservationsSeen` and `ObservationsUnattributed` before each curl.
2. Runs curl to `NUL`.
3. Re-snapshots and computes the delta.
4. Pulls `zvctl snapshot --json` and confirms `curl.exe` appears with at least
   80 % of the expected byte count.
5. Reports per-iteration PASS/FAIL and a final summary; exit code 1 on any fail.

**Pass criteria (per iteration):**

- `curl.exe` appears in the snapshot Apps[].
- `curl.exe` BytesDownTotal ≥ 80 % of the configured size.
- `ObservationsUnattributed` did not increase across the curl run.

**Pass criteria (overall):** every iteration passes. `Passes: 5/5`. **No "5/5 except
that one" outcomes — the previous bug was statistical, the gate must be
deterministic.**

### Variations to try

```powershell
# More iterations, longer download:
.\scripts\verify-attribution.ps1 -Iterations 10 -Bytes 100000000

# Very fast small downloads (most likely to trip the original bug):
.\scripts\verify-attribution.ps1 -Iterations 20 -Bytes 5000000 -DelayMs 3000
```

If any of these fail, the lifecycle resolver isn't holding up — capture the
output and treat it as a regression of the original bug.

### Visual confirmation (optional, separate from the gate)

After the script passes, you can launch the UI to confirm the chart and
list update sensibly during a curl. The UI is **not** part of the formal
gate any more — the gate now lives in `verify-attribution.ps1` because the
UI's 2 s poll can't deterministically prove "every event attributed."

```powershell
dotnet run --project src\ZenVizor.Ui -c Release
# Then run any of the curls above in another shell; chart should spike.
```

**Troubleshooting:**

- `zvctl status` fails: service isn't running. `Stop-Service ZenVizor;
  .\scripts\install-dev.ps1` to reinstall.
- Script reports "curl bytes_down < 80 %": ETW lost a meaningful fraction
  of events — investigate the kernel buffer (`logman query "NT Kernel
  Logger"` and provider state).
- Script reports "unattributed observations grew by N": some PIDs are
  still escaping the resolver. Check service logs for
  `ProcessLifecycleResolver primed with N running processes` at startup and
  for any `Win32 fallback Process.GetProcessById(...) failed` warnings.

---

## Gate #2 — Self-monitoring (founding invariant)

**This is the founding-invariant gate.** ZenVizor must emit zero outbound
traffic of its own. If it shows up in its own data, that's a regression we
need to see — *not* something to hide by filtering out our own PIDs (per
Phase 3 plan Q8).

1. Let the service + UI + (optionally) `zvctl` polling sit idle for **60 s**.
2. From an **elevated** PowerShell, query the DB directly. Use the
   single-line `sqlite3.exe` form below — per CLAUDE.md terminal gotchas,
   multi-line PowerShell here-strings (`@'…'@`) frequently break on paste
   into interactive PS:

```powershell
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
sqlite3.exe -readonly -header -column $db "SELECT a.image_name, COUNT(*) AS samples, SUM(s.bytes_up+s.bytes_down) AS bytes FROM apps a JOIN process_sessions ps USING(app_id) JOIN traffic_samples s USING(session_id) WHERE a.image_name IN ('ZenVizor.Service.exe','ZenVizor.Ui.exe','zvctl.exe') GROUP BY a.image_name;"
```

3. Cross-check the live snapshot:

```powershell
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe snapshot --all
```

**Pass criteria:**

- SQL query returns **zero rows**, OR returns rows with **zero bytes**
  (typically: no rows at all).
- `zvctl snapshot --all` does not list any `ZenVizor.Service.exe`,
  `ZenVizor.Ui.exe`, or `zvctl.exe` row.

**If either of these lights up: STOP and investigate.** Look at the row's
`bytes`, `connections` (`SELECT * FROM connections WHERE app_id = ...`), and
the surrounding `traffic_samples` — every byte attributed to a ZenVizor
process means something is talking to the network that shouldn't. Possible
culprits: a library that does revocation lookups, a logger that fanned out
to a remote sink, telemetry someone re-introduced.

---

## Gate #3 — No SQLite read on snapshot path (spot-check)

Mostly a CI gate (the integration test
`SnapshotPathDoesNotReadSqliteTests.TakeActivitySnapshot_NeverOpensASqliteConnection`
fails the build if `TakeActivitySnapshot()` opens a connection). A real-box
spot-check is still cheap:

```powershell
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
$before = (Get-Item $db).LastWriteTime
1..50 | ForEach-Object { & .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe snapshot --json | Out-Null }
$after = (Get-Item $db).LastWriteTime
"DB mtime delta: $($after - $before)"
```

**Pass criteria:** delta is zero or near-zero. Some non-zero delta is
acceptable if a 5 s flush tick happens to fall during the 50-call loop —
what we're confirming is the *snapshot path* doesn't itself open the DB.
The CI test is the canonical evidence; this is just a sanity check.

---

## Closing the phase

Once all three gates pass:

1. Check off the Phase 3 boxes in `docs/zenvizor-sprint-plan.md`.
2. Commit the changes on `main` with a descriptive message (Phase 3 covers
   the rolling-window snapshot, the IPC envelope, the `zvctl snapshot`
   command, and the dashboard wiring).
3. Push to remote; confirm CI is green on `windows-latest`.

Phase 4 — history/rollup query surface (`GetAppList`, `GetAppDetail`,
`GetConnections`, `GetTrafficHistory`) — picks up from here.
