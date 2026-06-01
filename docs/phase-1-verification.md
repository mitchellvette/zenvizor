# Phase 1 — Manual QA verification

Phase 1 wires the real ETW capture source, IP Helper PID correction, and the
flushing aggregator into the service. CI covers the four headless gates from
the Sprint Plan (synthetic-event-driven exact-row tests, PID correction,
IPv4+IPv6 classification, session reuse). This doc walks through the **three
manual gates** that must hold on a real Windows box.

> Run everything below from an **elevated PowerShell** unless noted. The
> service runs as LocalSystem; the QA SQL queries can run as the logged-in
> user (the DB is ACL'd to SYSTEM + Administrators).

---

## Pre-flight dependencies

Per the standing CLAUDE.md behavior, check tools *first* so you don't hit a
missing-dependency wall halfway through:

```powershell
# Sysinternals Process Monitor — required for Gate #3 (no per-event writes).
Get-Command procmon.exe -ErrorAction SilentlyContinue
Get-Command procmon64.exe -ErrorAction SilentlyContinue
# Both empty? Install:
#   winget install --id Microsoft.Sysinternals.ProcessMonitor
# Then open a new shell so PATH refreshes.

# Resource Monitor + Performance Monitor — built-in (resmon.exe / perfmon.exe), no install needed.
```

---

## 0. One-time build + reinstall

```powershell
cd C:\dev\titanirun-monitor

dotnet build .\TitaniRun.slnx -c Release
dotnet test  .\TitaniRun.slnx -c Release

# Phase 1 service includes the ETW capture engine. Reinstall and start.
.\scripts\uninstall-dev.ps1 -PurgeData    # clean slate so attribution starts fresh
.\scripts\install-dev.ps1
sc.exe query TitaniRun                    # confirm STATE 4 RUNNING

# Confirm capture is reported active:
& .\src\TitaniRun.Cli\bin\Release\net10.0-windows\trctl.exe status
# Should print: Capture active  : True
```

Test totals to expect from `dotnet test`:

- `TitaniRun.Core.Tests` — 48 pass
- `TitaniRun.Storage.Tests` — 16 pass
- `TitaniRun.Ipc.Tests` — 11 pass
- `TitaniRun.Attribution.Tests` — 8 pass
- `TitaniRun.Integration.Tests` — 3 pass

---

## 1. Known-traffic attribution test

> Sprint Plan Phase 1 manual gate:
> *"On a real box, generate known traffic from a known process; the DB
> attributes bytes to the correct PID within tolerance, and totals are sane
> vs. Resource Monitor."*

The cleanest way to drive a known-byte transfer is to download a file with a
known `Content-Length` and watch the corresponding `connections` row grow.

### 1a. Generate the traffic

In a **non-elevated** PowerShell, pick a small file with a stable size from a
public mirror you trust:

```powershell
# Hetzner's speed-test files have been stable HTTPS endpoints for years.
# 10 MB is the sweet spot: big enough that TCP/TLS overhead is a small % of total,
# small enough to complete in seconds.
$url  = "https://speed.hetzner.de/10MB.bin"   # exactly 10,485,760 bytes
$dest = "$env:TEMP\dlbench.bin"

# Confirm the expected size before downloading:
(Invoke-WebRequest -Method Head $url).Headers.'Content-Length'

# Note the PowerShell PID — this is the process we'll attribute to.
$PID
Invoke-WebRequest $url -OutFile $dest
(Get-Item $dest).Length    # actual bytes written to disk
```

Note the **PID** that PowerShell prints — you'll use it as the filter in 1b.

### 1b. Query the DB for what TitaniRun attributed

Wait ~10 seconds after the download completes so the next flush tick lands.
Then query the DB (read-only access; ACL'd to SYSTEM + Administrators — run
this as an Admin):

```powershell
$db = "$env:ProgramData\TitaniRun\titanirun.db"

# Sanity check the DB exists and the schema is in place.
Test-Path $db                           # True
sqlite3.exe $db "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
# Expect 10 tables: alerts, apps, connections, devices, process_sessions,
# schema_migrations, settings, traffic_daily, traffic_hourly, traffic_samples.

# Sum bytes attributed to the specific PowerShell PID's session(s).
# Use a single-line SQL string — PowerShell's here-string `"@` terminator
# MUST sit at column 0, which makes them brittle when pasted into shells that
# add leading whitespace. Single-line is the lowest-friction form.
$pid18716 = <THE-PID-FROM-1A>
sqlite3.exe -cmd ".mode column" -cmd ".headers on" $db "SELECT a.image_name, ps.pid, SUM(s.bytes_up) AS bytes_up, SUM(s.bytes_down) AS bytes_down, COUNT(s.sample_id) AS samples, MIN(s.bucket_start) AS first_bucket, MAX(s.bucket_start) AS last_bucket FROM process_sessions ps JOIN apps a ON a.app_id=ps.app_id JOIN traffic_samples s ON s.session_id=ps.session_id WHERE ps.pid=$pid18716 GROUP BY a.image_name, ps.pid;"

# If the PID-specific query is empty (the PowerShell process exited before
# attribution), broaden to top-10 talkers to confirm capture is producing rows:
sqlite3.exe -cmd ".mode column" -cmd ".headers on" $db "SELECT a.image_name, ps.pid, SUM(s.bytes_up)+SUM(s.bytes_down) AS total_bytes FROM process_sessions ps JOIN apps a ON a.app_id=ps.app_id JOIN traffic_samples s ON s.session_id=ps.session_id GROUP BY a.image_name, ps.pid ORDER BY total_bytes DESC LIMIT 10;"
```

> If you'd rather use a multi-line here-string for readability, the closing
> `"@` **must be at column 0** — leading whitespace makes PowerShell keep
> consuming input forever. Press **Ctrl+C** if you find yourself stuck at
> a `>>` continuation prompt.

Don't have `sqlite3.exe`? Pre-flight:

```powershell
Get-Command sqlite3.exe -ErrorAction SilentlyContinue
# Empty? Install:
#   winget install --id SQLite.SQLite
# Or use the .NET API; ad-hoc queries are easier with the CLI though.
```

### 1c. Pass criteria

- `bytes_down` for the PowerShell PID should be **within ~5%** of the
  on-disk file size from `(Get-Item $dest).Length`. (Headers + TCP overhead
  account for the small overshoot; HTTPS handshake bytes for any
  undershoot.)
- `image_name` should be `powershell.exe` (or `pwsh.exe` if you used PS 7+).
- `bytes_up` should be **small** (request headers only).
- The same totals should reconcile with **Resource Monitor → Network →
  Processes with Network Activity** over the same time window. Order-of-
  magnitude match is what matters; exact equality is not expected because
  Resource Monitor includes some kernel framing.

If the row is missing or attributes to a different PID, that's a real
attribution bug.

---

## 2. Performance budget

> Sprint Plan Phase 1 manual gate:
> *"Idle CPU < 1%, service working set < ~80 MB under light load."*

### 2a. Idle CPU

Stop generating traffic. Wait ~30 seconds. Then:

```powershell
# Sample TitaniRun.Service for 60s, report avg % CPU and avg working-set MB.
$samples = 1..60 | ForEach-Object {
    $p = Get-Process TitaniRun.Service -ErrorAction SilentlyContinue
    if (-not $p) { return }
    [pscustomobject]@{
        Cpu = $p.CPU                                # cumulative seconds
        Ws  = [Math]::Round($p.WorkingSet64 / 1MB, 1)
    }
    Start-Sleep -Seconds 1
}

# Convert cumulative CPU to per-second deltas:
$deltaCpu = for ($i = 1; $i -lt $samples.Count; $i++) {
    ($samples[$i].Cpu - $samples[$i - 1].Cpu)
}
$avgCpuPct = ([Math]::Round((($deltaCpu | Measure-Object -Average).Average) * 100, 2))
$avgWsMB   = [Math]::Round(($samples.Ws | Measure-Object -Average).Average, 1)

"Avg CPU %: $avgCpuPct  |  Avg WS MB: $avgWsMB"
```

### 2b. Pass criteria

- **Avg CPU % < 1.0** sustained.
- **Avg WS MB < 80**.

If CPU is high during the measurement window, check that no traffic is being
generated (close browsers, pause background syncs) and that the IP Helper
poll interval is still at its default 1s.

---

## 3. No per-event DB writes

> Sprint Plan Phase 1 manual gate:
> *"No per-event DB writes observed (writes occur on the flush tick)."*

The invariant is **architectural**, not a fixed event count. The hot path
(`TrafficAggregator.Observe`) must not produce any disk I/O; all writes happen
inside the single transaction the `SqliteFlushSink` issues per flush tick. The
**absolute** write count scales with how much traffic the tick has to persist
(number of active PIDs, distinct endpoints, new-session opens), so quoting a
fixed pass number would be meaningless. Instead, assert four structural
properties and one scaling property.

### 3a. Capture a 60-second window

1. Launch `procmon.exe` (elevated).
2. **Filter → Filter** (Ctrl+L), set:
   - Process Name → `is` → `TitaniRun.Service.exe` → Include
   - Path → `contains` → `titanirun.db` → Include
   - Operation → `is` → `WriteFile` → Include
3. Press **OK** and **Clear the display** (Ctrl+X).
4. Generate steady but modest traffic for 60 seconds (browser tab streaming a
   video, or refreshing a news site).
5. After 60 s, **stop capture** (Ctrl+E).
6. Open **Tools → File Summary**. Note the per-file write counts and bytes.

### 3b. Pass criteria — structural

| Check                                | Expected                                    | Why                                                                                                                         |
| ------------------------------------ | ------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Writes to `titanirun.db-wal`         | The overwhelming majority of writes         | WAL is where transactional change pages land; this confirms transactions are short and not bypassing the journal.           |
| Writes to `titanirun.db` (main file) | Zero or a small number (WAL checkpoints)    | Each non-zero entry here is one auto-checkpoint. Expect zero in a 60s quiet window; a handful is fine under sustained load. |
| Avg write size                       | ≈ 4,096 bytes (one SQLite page)             | If you see many <100-byte writes, statements are being committed individually instead of batched.                           |
| Total writes / 12 (flushes/min)      | Pages-per-flush in the low tens (e.g. 5–30) | Each flush transaction writes its dirty page set in one go. Pages-per-flush scales with batch size, not with event rate.    |

### 3c. Pass criteria — scaling

The architectural property only matters if writes **don't track event count**.
Confirm by burst-loading the system and re-measuring:

```powershell
# Restart Process Monitor capture (Ctrl+X then start a fresh 60s window),
# then in a second PowerShell hammer downloads:
1..30 | ForEach-Object {
    Invoke-WebRequest "https://cachefly.cachefly.net/10mb.test" -OutFile $null -UseBasicParsing | Out-Null
}
```

That burst produces tens of thousands of ETW network events. The pass condition:

- Total WriteFile count should **rise modestly** (because each flush
  transaction is bigger — more endpoint upserts, more samples), to something
  on the order of 1.5–3× the quiet count.
- It must NOT scale with event count. If you see ≥ 10,000 WriteFile events
  in a 60s burst window, the hot path is hitting disk — file a bug.

### 3d. If you want a rough sanity number

For reference only — this is your system, not a pass criterion: a typical
busy-but-not-heaving desktop (browser + a couple chat apps + background
services) produces **~200–400 WriteFile/min** in this configuration, all to
`.db-wal`, averaging ~4 KB each. Under download burst the number rises to
**~300–500/min** but does not scale with event rate.

---

## Cleanup

```powershell
.\scripts\uninstall-dev.ps1            # keep history
.\scripts\uninstall-dev.ps1 -PurgeData # full wipe
```

---

## What is NOT verified in Phase 1

- **svchost service-name resolution** (Phase 2) — svchost rows will show the
  bare `svchost.exe` name + no `hosted_services` value for now.
- **Signer/publisher + signature_status** (Phase 2) — every app row has
  `publisher = NULL`, `signature_status = "Unchecked"`.
- **The live activity snapshot IPC path** (Phase 3) — the UI's bottom bar
  still uses `GetServiceStatus` from Phase 0; per-app live rates land in P3.
- **Zero-own-traffic self-monitoring** (Phase 3 + 6) — Phase 3 wires the
  self-monitoring assertion. For Phase 1, you can spot-check by running
  `trctl status` and observing that no IPC RPCs show up as outbound
  network traffic in the DB (named pipes don't traverse the IP stack).
