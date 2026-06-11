# Phase 4 — Manual QA verification

Phase 4 introduces the history-query surface: rollups on the flush path, the
retention/purge job, four new IPC methods (`GetAppList`, `GetAppDetail`,
`GetConnections`, `GetTrafficHistory`), the matching `zvctl` subcommands,
and the Per-App / App detail / History UI pages. CI covers the headless
gates (rollup correctness, retention boundary, query results at each grain,
IPC round-trips). This doc walks the **three manual gates** that must hold
on a real Windows box.

> Run elevated PS for service control + DB queries; non-elevated for UI / CLI
> against the running service. Same pattern as Phases 2 and 3.

---

## Pre-flight dependencies

Phase 4 adds **no new external tool dependencies** beyond Phases 0–3:

```powershell
Get-Command sqlite3.exe -ErrorAction SilentlyContinue
#   winget install --id SQLite.SQLite
Get-Command curl.exe -ErrorAction SilentlyContinue   # ships with Windows
```

---

## 0. One-time build + reinstall

The stale-dev-service caveat from Phase 3 still applies — stop the running
service before rebuilding or the post-build copy step will fail.

```powershell
# Elevated:
Stop-Service ZenVizor -ErrorAction SilentlyContinue

# Non-elevated:
cd C:\dev\zenvizor
dotnet build .\ZenVizor.slnx -c Release
dotnet test  .\ZenVizor.slnx -c Release

# Elevated:
.\scripts\uninstall-dev.ps1
.\scripts\install-dev.ps1
sc.exe query ZenVizor                  # confirm STATE 4 RUNNING

# Non-elevated, smoke:
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe status
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe apps --window 1h
```

Test totals to expect from `dotnet test`:

- `ZenVizor.Core.Tests` — 79 pass (Phase 4 added chart downsampler tests)
- `ZenVizor.Storage.Tests` — 63 pass (Phase 4 added rollup UPSERT, retention, query repo, bucket-overlap regression)
- `ZenVizor.Ipc.Tests` — 17 pass (Phase 4 added query round-trips)
- `ZenVizor.Attribution.Tests` — 48 pass
- `ZenVizor.Integration.Tests` — 5 pass

**Total: 212 pass.**

To exercise Phase 4 you want some history populated. Easiest: leave the
service running for ~10–15 min with a couple of `curl` downloads sprinkled
in. The rolling samples → rollup pipeline lands data in `traffic_samples`,
`traffic_hourly`, and `traffic_daily` at the same flush tick.

If you want to seed quickly, run the attribution script a few times:

```powershell
.\scripts\verify-attribution.ps1 -Iterations 3 -Bytes 50000000
```

That gives you ~3 attributed curl runs to query against.

---

## Gate #1 — Per-app reconciliation

**Pass criteria:** for a chosen app + window, the totals reported by `zvctl
apps`, `zvctl app <id>`, and `zvctl snapshot` all agree (modulo the live
snapshot's partial accumulator).

```powershell
# Pick a recent window and list apps. The leftmost column is the app id.
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe apps --window 1h --top 5
```

Grab an id from the `Id` column (e.g. `1`). Either type it directly:

```powershell
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe app 1 --window 1h
```

…or set it as a PS variable first if you want to reuse it across commands:

```powershell
$appId = 1   # replace with the id you chose
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe app $appId --window 1h
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe connections $appId --window 1h
```

The `app` command's Totals line should equal the `apps` row's Up/Down for
the same app and same window (exact equality — both are summing the same
underlying samples).

Cross-check vs. the live snapshot (5–10 s window, much smaller):

```powershell
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe snapshot
```

The snapshot's bytes for any app should be ≤ the corresponding 1-hour
history bytes from `zvctl apps --window 1h` (snapshot window is a subset).

Also worth: walk multiple grains on the same window to confirm the auto-grain
rule fires as documented (`≤24h → Samples`, `>24h ≤30d → Hourly`, `>30d → Daily`).

```powershell
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe history --window 1h
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe history --window 7d
& .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe history --window 90d
```

The summary line of each should report `grain=Samples`, `Hourly`, and
`Daily` respectively.

---

## Gate #2 — Drill-down navigation (UI)

**Pass criteria:** PerApp → AppDetail navigation works; the AppDetail page
shows a non-empty chart, connections, and recent sessions consistent with
the data CLI returns.

```powershell
# Non-elevated:
dotnet run --project src\ZenVizor.Ui -c Release
```

1. Open the Per-App page from the nav rail.
2. Verify the window picker default is **Last 24 hours**. Change to **Last 1
   hour** — table refreshes; rows ranked by total bytes.
3. Double-click any row → App detail page loads with the app's identity in
   the header.
4. Verify three sections:
   - **Traffic over time** chart shows non-zero data when bytes exist.
   - **Connections** grid lists endpoints with protocol, address, port, Up/Down.
   - **Recent sessions** grid lists session rows; svchost entries show
     hosted services in the rightmost column.
5. Change the Window dropdown. The page re-fetches; the chart subtitle and
   `Grain:` line in the summary update to reflect the newly resolved tier
   (auto-rule: ≤24h → Samples, ≤30d → Hourly, >30d → Daily). Status banner
   surfaces any IPC errors.
6. Click "< Per-App" to navigate back; the Per-App grid still has its
   previous state.

The History page should also load:

1. Default Last 24h / Auto grain — chart renders with Up + Down series.
2. Summary line shows `Grain: Samples` (or Hourly for a 7d window).

---

## Gate #3 — Large-window query responsiveness (rollup tier)

**Pass criteria:** a 90-day history query returns in well under a second
because the daily rollup tier is hit; a forced `--grain samples` query for
the same span takes noticeably longer (and may be empty given the 30-day
samples retention window, which is also expected behavior).

Easiest way to populate something synthetic: seed a fixture via sqlite3
directly.

```powershell
# Elevated. Insert 365 daily rollup rows for app_id 1 (must exist).
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
sqlite3.exe $db "SELECT MIN(app_id) FROM apps;"   # confirm app_id 1 exists
sqlite3.exe $db "INSERT OR REPLACE INTO traffic_daily (app_id, bucket_start, remote_class, bytes_up, bytes_down) SELECT 1, (((strftime('%s','now')*1000) / 86400000) * 86400000) - (n * 86400000), 'Wan', 1024 * n, 4096 * n FROM (WITH RECURSIVE c(n) AS (SELECT 0 UNION ALL SELECT n+1 FROM c WHERE n < 365) SELECT n FROM c);"
```

Then in non-elevated PS:

```powershell
Measure-Command {
    & .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe history --window 90d --json | Out-Null
}
```

Should report `TotalMilliseconds` under a few hundred ms. Compare to:

```powershell
Measure-Command {
    & .\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe history --window 90d --grain samples --json | Out-Null
}
```

The samples-grain query scans `traffic_samples` (typically 0 rows for that
range due to retention) and is fast in the empty case; if you have a lot
of samples seeded it'll be noticeably slower. The point is to confirm the
two paths return distinct data shapes and the auto-grain default picks the
cheap path.

After running, clean up the synthetic rows if you want a tidy DB:

```powershell
# Elevated
sqlite3.exe 'C:\ProgramData\ZenVizor\zenvizor.db' "DELETE FROM traffic_daily WHERE bucket_start < (strftime('%s','now')*1000 - 30*86400000);"
```

---

## Closing the phase

Once all three gates pass:

1. Phase 4 boxes in `docs/zenvizor-sprint-plan.md` are already checked off.
2. Commit on `main` with a descriptive message.
3. CI should remain green on `windows-latest`.

Next up: the **UI design / polish interlude** (per Phase-3 decision —
between Phase 4 and Phase 5). Phase 5 is the daily-report + CSV/HTML export.
