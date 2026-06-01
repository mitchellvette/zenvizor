# Phase 2 — Manual QA verification

Phase 2 turns the stubbed `apps` columns into actionable identity: real
publisher (when signed), real `signature_status`, real `is_user_writable_path`,
and svchost-PID → hosted-service-name resolution. CI covers the headless gates
(classifier behavior, enricher caching, backfill, sink persistence). This doc
walks through the **five manual gates** that must hold on a real Windows box.

> Run everything below from an **elevated PowerShell**. The service runs
> as LocalSystem and the DB at `C:\ProgramData\TitaniRun\titanirun.db` is
> ACL'd to SYSTEM + Administrators only — a non-elevated shell will get
> "unable to open database file" even if your account is in the
> Administrators group (UAC strips the admin half of the split token from
> unprivileged shells).

---

## Pre-flight dependencies

Per the standing CLAUDE.md behavior, check tools *first* — a missing
dependency mid-validation makes the whole exercise take 5× longer.

```powershell
# Sysinternals sigcheck — used in Gate #2 to cross-verify Authenticode
# verdicts against the value TitaniRun stores.
Get-Command sigcheck.exe -ErrorAction SilentlyContinue
Get-Command sigcheck64.exe -ErrorAction SilentlyContinue
# Both empty? Install:
#   winget install --id Microsoft.Sysinternals.Sigcheck
# Then open a new shell so PATH refreshes.

# Sysinternals accesschk — used in Gate #3 to verify the user-writable-path
# heuristic matches the filesystem's actual ACLs on a path of interest.
# AccessChk is NOT individually packaged in winget; it ships only in the
# full Sysinternals Suite. Two options:
Get-Command accesschk.exe -ErrorAction SilentlyContinue
Get-Command accesschk64.exe -ErrorAction SilentlyContinue
# Option 1 — install the whole Sysinternals Suite (~30 MB, gives you the
# rest of the toolkit too):
#   winget install --id Microsoft.Sysinternals.Suite
# Option 2 — skip accesschk entirely and use built-in icacls / Get-Acl in
# Gate #3 (see the fallback snippet there).

# sqlite3 (Phase 1 dependency, should already be installed)
Get-Command sqlite3.exe -ErrorAction SilentlyContinue
#   winget install --id SQLite.SQLite

# Built-in (no install): sc.exe, Get-CimInstance Win32_Service, PowerShell
# Get-AuthenticodeSignature.
```

A built-in fallback for the sigcheck cross-check (Gate #2):
```powershell
Get-AuthenticodeSignature 'C:\Path\To\some.exe'
```
This covers the same Authenticode verdict — sigcheck is just easier to read at
the command line.

---

## 0. One-time build + reinstall

```powershell
cd C:\dev\titanirun-monitor

dotnet build .\TitaniRun.slnx -c Release
dotnet test  .\TitaniRun.slnx -c Release

# Phase 2 changes the apps-table column population pattern but does not
# require dropping the DB. To exercise the backfill path (Gate #5), keep the
# Phase 1 DB. To start clean, pass -PurgeData.
.\scripts\uninstall-dev.ps1               # leave -PurgeData OFF to test backfill
.\scripts\install-dev.ps1
sc.exe query TitaniRun                    # confirm STATE 4 RUNNING

# Confirm capture is reported active and the service is talking:
& .\src\TitaniRun.Cli\bin\Release\net10.0-windows\trctl.exe status
# Should print: Capture active  : True
```

Test totals to expect from `dotnet test`:

- `TitaniRun.Core.Tests` — 58 pass
- `TitaniRun.Storage.Tests` — 24 pass
- `TitaniRun.Ipc.Tests` — 11 pass
- `TitaniRun.Attribution.Tests` — 30 pass
- `TitaniRun.Integration.Tests` — 4 pass

Let the service run for ~3 minutes before starting the gates so several
flush windows have completed.

```powershell
# Convenience: open a read-only query window against the DB. Must be an
# elevated PowerShell — the DB is ACL'd to SYSTEM + Administrators.
$db = 'C:\ProgramData\TitaniRun\titanirun.db'
sqlite3.exe -readonly $db
```

---

## 1. svchost resolution

> Sprint Plan Phase 2 manual gate:
> *"Real svchost traffic resolves to named services (e.g., Dnscache, Dhcp),
> not bare svchost.exe; multi-service PIDs show the honest list."*

```sql
.headers on
.mode column
SELECT a.image_name,
       ps.pid,
       ps.hosted_services,
       (SELECT COALESCE(SUM(bytes_up + bytes_down), 0)
          FROM traffic_samples s
          WHERE s.session_id = ps.session_id) AS bytes_total
FROM   apps a
JOIN   process_sessions ps USING(app_id)
WHERE  a.image_name = 'svchost.exe'
  AND  ps.end_time IS NULL
ORDER BY bytes_total DESC;
```

**Pass criteria:**

- Several svchost PIDs visible — typically 3–10. Only PIDs that have made
  network traffic since service start show up here; this is NOT the full
  list of svchosts on the system.
- `hosted_services` is populated for every row (NOT NULL, NOT empty).
- **Conditional:** if any row's `hosted_services` contains a comma (a
  multi-service svchost PID), `bytes_total` for that row is a single
  PID-level total — NOT divided across the services. (Invariant #5: honest
  attribution, no fake precision.)

### Multi-service svchost note

Modern Windows 10/11 defaults to one-service-per-svchost on machines with
more than 3.5 GB of RAM. The classic `Dnscache,NlaSvc,Dhcp` co-hosted
pattern is increasingly rare on desktops. To check whether any
multi-service svchosts exist on your box at all:

```powershell
Get-CimInstance Win32_Service |
    Where-Object { $_.PathName -like '*svchost.exe*' -and $_.State -eq 'Running' } |
    Group-Object ProcessId |
    Where-Object Count -gt 1 |
    ForEach-Object {
        [pscustomobject]@{
            Pid      = $_.Name
            Services = ($_.Group.Name -join ',')
        }
    }
```

Typical results on a modern desktop: a small handful of infrastructure
hosts (e.g. `BFE,mpssvc`; `RpcEptMapper,RpcSs`;
`BrokerInfrastructure,DcomLaunch,PlugPlay,Power,SystemEventsBroker`) that
mostly don't generate network traffic, so they won't appear in our
`process_sessions` table. That means the comma-join code path may not be
exercisable on this box. The CI test
`SessionTrackerTests.TryTrack_SvchostPid_PopulatesHostedServices` covers
it deterministically; if no multi-service row is observed during manual
gate, accept the deferral.

To confirm a multi-service row WAS observed since service start (any
end_time, alive or closed):

```sql
SELECT pid, hosted_services, start_time, end_time
FROM   process_sessions
WHERE  hosted_services LIKE '%,%';
```

If that returns rows, you've witnessed the comma-join firing on real
traffic — full Gate #1 pass. If empty, deferred to CI.

Cross-verify the single-service results against the OS:

```powershell
# Pick a multi-service PID from the SQL output, then:
$pid = 4321  # ← replace
Get-CimInstance Win32_Service |
    Where-Object ProcessId -eq $pid |
    Select-Object Name, DisplayName, State
```

The names returned by `Get-CimInstance` should be the same set TitaniRun
stored in `hosted_services` (order may differ — TitaniRun sorts ordinally).

---

## 2. Signer verification on known-signed apps

> Sprint Plan Phase 2 manual gate:
> *"A known signed app shows its publisher."*

Pick two apps you actually run on this box — embedded-signed apps work best
(Phase 2 covers embedded signatures; the catalog path for Windows system
binaries is a known boundary, see "Known boundaries" below).

Good candidates (all embedded-signed):

- `code.exe` (Visual Studio Code) — publisher *Microsoft Corporation*
- `chrome.exe` — publisher *Google LLC*
- `Discord.exe` — publisher *Discord Inc.*

Generate some traffic from at least one (open the app, let it phone home,
load a webpage). Wait for ~10 s so two flush windows complete.

```sql
SELECT image_name, publisher, signature_status, is_user_writable_path
FROM   apps
WHERE  image_name IN ('code.exe', 'Code.exe', 'chrome.exe', 'Discord.exe')
ORDER BY image_name;
```

**Pass criteria for each row:**

- `publisher` is NOT NULL and matches the expected vendor.
- `signature_status` = `'Signed'`.
- `is_user_writable_path` matches the actual install location (Code installed
  per-user under `%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe` → `1`;
  installed system-wide under `C:\Program Files\Microsoft VS Code\Code.exe`
  → `0`).

> **`is_user_writable_path=1` is common for legitimate signed apps.** Chrome,
> VS Code, Discord, Slack, Spotify, and many other modern desktop apps default
> to per-user installs under `%LOCALAPPDATA%`. The flag means "this binary
> lives somewhere the user can overwrite without admin" — that's a *necessary*
> precondition for the Phase 6 alert but not sufficient on its own. The alert
> only fires on `is_user_writable_path=1` **AND** `signature_status IN
> ('Unsigned', 'Invalid')`. Don't treat a `1` in this column as suspicious in
> isolation.

Cross-verify the signature verdict against the OS:

```powershell
sigcheck.exe -nobanner -n -q "C:\Path\To\Code.exe"
# Or, built-in:
Get-AuthenticodeSignature "C:\Path\To\Code.exe" | Format-List Status, SignerCertificate
```

The verdicts should agree. If TitaniRun says `Signed` and sigcheck says
`Signed`, the gate passes for this binary.

### Known boundaries (NOT a Phase 2 failure)

- **Catalog-signed Windows system binaries** (svchost.exe, explorer.exe,
  notepad.exe, …) currently report as `Unsigned`. Phase 2 verifies *embedded*
  Authenticode signatures only; catalog signatures live in
  `C:\Windows\System32\CatRoot\` and need a separate verification pass.
  This is documented and acceptable because those binaries live in
  `C:\Windows` → `is_user_writable_path = 0`, so the Phase 6 alert
  combination cannot misfire. If you want to confirm a Windows binary IS
  actually catalog-signed, `sigcheck` will report it. Adding catalog support
  is a follow-up, not a Phase 2 requirement.

---

## 3. Unsigned binary from a user-writable path

> Sprint Plan Phase 2 manual gate:
> *"An unsigned binary run from %TEMP% shows Unsigned + user-writable flag."*

We need an unsigned PE in a user-writable path that makes a network
connection. **Don't** try the obvious "copy `C:\Windows\System32\curl.exe`
to `%TEMP%`" — Windows 10/11 ships curl.exe with an **embedded**
Authenticode signature (publisher `Microsoft 3rd Party Application
Component`), and copying preserves the embedded signature. The copy still
verifies as Signed; the test would silently fail open. We verified this
during the Phase 2 walkthrough on 2026-06-01.

**Working approach — compile a tiny unsigned PE inline via PowerShell
`Add-Type`.** This produces a real Authenticode-unsigned executable (strong
naming is separate from Authenticode, so the output has no
`WIN_CERTIFICATE` blob and `WinVerifyTrust` returns `TRUST_E_NOSIGNATURE`).
The binary downloads 10 MB from Cloudflare then sleeps 20 s so the resolver
has plenty of time to observe + map the PID.

```powershell
$src = 'class P { static void Main() { new System.Net.WebClient().DownloadData("https://speed.cloudflare.com/__down?bytes=10000000"); System.Threading.Thread.Sleep(20000); } }'
$dest = Join-Path $env:TEMP 'trtest-unsigned.exe'
Add-Type -TypeDefinition $src -OutputAssembly $dest -OutputType ConsoleApplication
& $dest
"trtest-unsigned exit code: $LASTEXITCODE"
```

That blocks for ~25 s. Then let two flush windows commit:

```powershell
Start-Sleep -Seconds 10
```

Then query:

```powershell
sqlite3.exe -readonly -header -column $db "SELECT image_path, image_name, publisher, signature_status, is_user_writable_path FROM apps WHERE image_name = 'trtest-unsigned.exe';"
```

**Pass criteria for the returned row:**

- `image_path` ends in `\AppData\Local\Temp\trtest-unsigned.exe`.
- `signature_status` = `'Unsigned'` (or `'Invalid'` — both trip Phase 6).
- `is_user_writable_path` = `1`.
- `publisher` is NULL (column appears empty).

If you also want to spot-check it landed in `process_sessions` correctly:

```powershell
sqlite3.exe -readonly -header -column $db "SELECT ps.pid, ps.start_time, ps.end_time, a.image_name FROM process_sessions ps JOIN apps a ON a.app_id = ps.app_id WHERE a.image_name = 'trtest-unsigned.exe';"
```

### Cleanup

```powershell
Remove-Item "$env:TEMP\trtest-unsigned.exe" -ErrorAction SilentlyContinue
```

Sanity-check the path classifier against the actual filesystem ACLs:

```powershell
# Pick the path from the SQL output, then:
$p = "$env:TEMP\trtest-curl.exe"
accesschk.exe -nobanner $env:USERNAME $p
# Lines starting with 'RW' confirm the current user can write the file.
```

If you didn't install Sysinternals Suite, use the built-in fallback —
`icacls` shows the raw ACL, and Get-Acl filters to your user:

```powershell
$p = "$env:TEMP\trtest-curl.exe"
icacls $p
# Or, more focused:
(Get-Acl $p).Access |
    Where-Object {
        $_.IdentityReference -match [Regex]::Escape($env:USERNAME) -or
        $_.IdentityReference -eq 'BUILTIN\Users'
    } |
    Format-Table IdentityReference, FileSystemRights, AccessControlType -AutoSize
# FileSystemRights containing 'Write', 'Modify', or 'FullControl' means
# the current user can write the file.
```

If the ACL check shows the user has write access but TitaniRun stored
`is_user_writable_path = 0`, the gate has failed (false negative in the
heuristic). Investigate `UserWritablePathClassifier.EnumerateDefaultPrefixes`.

---

## 4. CPU budget — enrichment caching is working

> Sprint Plan Phase 2 manual gate:
> *"Enrichment does NOT raise idle CPU above budget (caching verified — no
> repeated WinVerifyTrust per event)."*

The Phase 1 idle CPU gate (~1%) must still hold with enrichment enabled. If
the cache isn't working, we'd see WinVerifyTrust running per event instead
of per binary version — measurable as a CPU spike.

```powershell
# Sample the service over 60 seconds; report mean and 95th-percentile CPU.
$proc = Get-Process -Name TitaniRun.Service -ErrorAction Stop
$samples = @()
for ($i = 0; $i -lt 60; $i++) {
    $samples += (Get-Counter "\Process(TitaniRun.Service)\% Processor Time" -SampleInterval 1 -MaxSamples 1).CounterSamples.CookedValue
}
$samples | Measure-Object -Average -Maximum |
    Select-Object @{N='avg_cpu_pct'; E={[math]::Round($_.Average, 2)}},
                  @{N='max_cpu_pct'; E={[math]::Round($_.Maximum, 2)}}
```

Divide by `[Environment]::ProcessorCount` if `% Processor Time` reports
across all cores (PerfMon's convention varies). Treat the budget as
< 1% normalized.

**Pass criteria:**

- Average CPU < 1% over the 60-second window.
- Service working set (Task Manager) < ~80 MB.

If average CPU jumps above ~5% during the gate window, dump the per-app
verification count to confirm the cache is doing its job. The Phase 6
runtime won't expose this directly; for now, the headless cache test
(`AppEnricherTests.Enrich_SameBinary_VerifierCalledOnce`) is the
canonical evidence.

---

## 5. Backfill of historical `Unchecked` rows

> Phase 2 Q10 — *"users will install Phase 2 on top of a running Phase 1
> service with weeks of history. If we don't backfill, every historical app
> shows as Unchecked until the process happens to be observed again."*

This gate is intentionally run *without* `-PurgeData` so the existing
Phase 1 DB has rows with `signature_status = 'Unchecked'` and `publisher
IS NULL`. Backfill should sweep them on first Phase 2 start.

Check the service's most recent start sequence. The Serilog file sink at
`C:\ProgramData\TitaniRun\logs\service-<date>.log` is the source of truth
(set up in `src/TitaniRun.Service/Program.cs`). The Event Log sink is
secondary and may fail silently on first install if event-source creation
hits a permissions wall.

```powershell
Get-ChildItem "C:\ProgramData\TitaniRun\logs\service-*.log" |
    ForEach-Object { Get-Content $_.FullName | Select-String 'Enrichment backfill' }
```

Or, via the Windows Event Log (uses `FilterHashtable` because `-LogName`
and `-ProviderName` are in different parameter sets and can't be combined
directly):

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='TitaniRun'} `
    -MaxEvents 100 -ErrorAction SilentlyContinue |
    Where-Object Message -Match 'Enrichment backfill' |
    Select-Object TimeCreated, Message
```

You should see two log lines from `EnrichmentBackfill`:

```
Enrichment backfill starting: N apps with signature_status='Unchecked'.
Enrichment backfill done. Updated=N1 Skipped=N2.
```

If you instead see a single line saying `"Enrichment backfill: no Unchecked
apps rows."`, your DB was effectively fresh at first Phase 2 start — the
backfill-on-historical-data path didn't have anything to do. **This is not
a failure.** It means `uninstall-dev.ps1 -PurgeData` was run somewhere, or
this is the first install. The backfill code path is fully covered by the
five headless `EnrichmentBackfillTests` (idempotency, batch-boundary, the
update-vs-skip distinction, etc.). To exercise it in vivo, run a Phase 1
build first to populate Phase-1-shape rows, then reinstall with Phase 2 —
or accept the CI deferral.

Then verify the current apps-table state — this is the real gate, regardless
of whether the work happened via backfill or live session-open enrichment:

```powershell
sqlite3.exe -readonly -header -column $db "SELECT signature_status, COUNT(*) AS n FROM apps GROUP BY signature_status;"
```

**Pass criteria:**

- `Signed` / `Unsigned` / `Invalid` together cover the vast majority of rows
  (~85%+).
- `Unchecked` count is small. **A small residual is expected.** Per
  `AppEnricher.TryReadCacheKey`, any binary the enricher can't stat (file
  missing, permission denied, name-only resolution) returns Unchecked
  without caching. Typical sources, all legitimate:
  - **PPL processes** — `lsass`, `MsMpEng`, `MpDefenderCoreService`,
    sometimes a `svchost` instance. Windows denies `MainModule.FileName`
    to non-Antimalware-Light callers, so `RealProcessImageResolver` falls
    back to `process.ProcessName` (bare name, no `.exe`). The enricher
    then can't stat the bare name and returns Unchecked. This is honest
    "we can't tell" per invariant #5 — not a bug.
  - **Kernel pseudo-image** — System / PID 4 has `image_path = "(kernel)"`
    which doesn't resolve.
  - **Catalog-signed Windows system binaries** — `svchost.exe`,
    `explorer.exe`, etc. Phase 2 supports embedded Authenticode only; see
    "Known boundaries" in Gate #2.
- No duplicates:
  ```powershell
  sqlite3.exe -readonly -header -column $db "SELECT image_path, publisher, COUNT(*) AS n FROM apps GROUP BY image_path, IFNULL(publisher, '') HAVING COUNT(*) > 1;"
  ```
  Should return zero rows.

---

## Done

If all five gates pass, mark the Phase 2 boxes in
`docs/titanirun-sprint-plan.md` and proceed to Phase 3 (live IPC for the
UI).
