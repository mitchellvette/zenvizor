# Epic B (1.2.0) — verification guide

Phase-level QA for the Alert noise + gating epic. Run these gates once,
after every UI lever is in place, on a real Windows box. Unit tests
cover the invariants (609 → 617 tests pass on this branch) but the
gates below verify the end-to-end behaviour a user experiences.

The epic ships **complete** — every gate must pass before 1.2.0 tags.

## Prerequisites — install these first

Pre-flight the tool list so no mid-QA context switch is needed:

- **Elevated PowerShell.** All scripts under `.\scripts\` require it.
  A fresh session from Windows Terminal → *Windows PowerShell (Admin)*
  drops you into `C:\WINDOWS\System32`, so every recipe below leads
  with a `Set-Location` to the repo root.
- **sqlite3.exe** for peeking at `%ProgramData%\ZenVizor\zenvizor.db`
  (ACL'd to SYSTEM + Administrators — the elevated shell reads it
  fine). Check first, install if missing:
  ```powershell
  if (-not (Get-Command sqlite3.exe -ErrorAction SilentlyContinue)) {
      winget install SQLite.SQLite
  }
  ```
- No other tooling needed — no psexec / accesschk / signtool / wix at
  this slice. The bundle-install path is a separate VM gate, tracked
  in [`reference_vm_install_testing`](../../reference_vm_install_testing.md)
  and only exercised at ship time.

## Recipe used by every gate — clean-state setup

Every gate that talks about a "fresh install" starts from this state.
The dev service runs from `bin\Release\net10.0-windows\` so a clean
rebuild + reregister is equivalent to a fresh MSI install for the
purposes of these gates. If you want the MSI bundle path too, that's
the VM gate — this recipe is faster for iterative QA.

```powershell
Set-Location C:\dev\zenvizor
net stop ZenVizor 2>$null                          # ignore if already stopped
dotnet build .\src\ZenVizor.Service\ZenVizor.Service.csproj -c Release
dotnet build .\src\ZenVizor.Ui\ZenVizor.Ui.csproj -c Release
.\scripts\uninstall-dev.ps1 -PurgeData
.\scripts\install-dev.ps1 -NoBuild
Start-Sleep -Seconds 8                             # let StartAsync finish
```

## Gate 1 — Install epoch + setup-scan seed land on first start

**What this proves.** Slices 1b + 1c wire up correctly: the install
epoch key is written once, and the setup-scan seeds every running
process image with `first_seen = install_epoch`.

Run the clean-state recipe. Then:

```powershell
Get-Content C:\ProgramData\ZenVizor\logs\service-*.log -Tail 200 |
    Select-String -Pattern "install epoch|BaselineAppSeeder|toast preferences"
```

**Expected — three lines, no `[WRN]`:**

- `[INF] Baseline install epoch initialized: <13-digit-ms>`
- `[INF] BaselineAppSeeder: enumerated N distinct running images.`
- `[INF] BaselineAppSeeder: inserted N baseline apps rows (install epoch <ms>).`

`N` is your box's running-process count (typically 80–200). Both `N`
values on the enumerated + inserted lines should be equal or within a
handful — the small delta comes from protected processes the enricher
couldn't stat.

Then confirm the DB matches. The correct filter is
`first_seen = install_epoch` — filtering by `first_seen = last_seen`
undercounts because any seeded app that opens a WAN connection in the
seconds between the seed and the query has its `last_seen` bumped by
the aggregator's flush, breaking the equality.

```powershell
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
$epoch = sqlite3.exe -readonly $db `
    "SELECT value FROM settings WHERE key = 'baseline.install_epoch_ms';"
sqlite3.exe -readonly -header -column $db `
    "SELECT COUNT(*) AS seeded FROM apps WHERE first_seen = $epoch;"
sqlite3.exe -readonly -header -column $db `
    "SELECT key, value FROM settings WHERE key LIKE 'baseline.%';"
```

**Expected:**

- `seeded` matches the log's `inserted N baseline apps rows` count exactly.
- Both `baseline.install_epoch_ms` and `baseline.setup_scan_done = '1'`
  present.

## Gate 2 — Day-one toast flood is silent

**What this proves.** Slices 1a + 1b + 1c cooperate: no
`FirstRunWanTalker` alert is *raised* for any pre-existing app in the
48 h post-install settling window. Because the seeded apps' `first_seen`
values all sit inside that window, the raise-gate rejects them upstream
of the surfacing gate — so both the toast AND the feed row stay silent.

**Important expectation-setting:** on a truly-fresh 1.2.0 install, "no
alerts at all appear" is EXACTLY the expected outcome for a machine
without unsigned-from-user-path binaries. That's a *correct* result,
not a broken pipeline. To distinguish "gate working" from "pipeline
broken" you need one of the two positive verifications below.

### 2a — Positive verification via the alerts table

After the clean-state recipe, launch the UI:

```powershell
Set-Location C:\dev\zenvizor
Start-Process .\src\ZenVizor.Ui\bin\Release\net10.0-windows\ZenVizor.Ui.exe
```

Use the machine normally for 2–3 minutes so plenty of apps talk. Then
query the alerts table:

```powershell
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
sqlite3.exe -readonly -header -column $db `
    "SELECT type, severity, COUNT(*) AS n FROM alerts GROUP BY type, severity ORDER BY n DESC;"
```

**Expected:**

- **Zero rows** with `type = 'FirstRunWanTalker'`. The baseline gate
  rejects the raise before insert, so nothing lands in the table at
  all — this is stronger than "no toast" (which could just mean the
  surfacing gate blocked it).
- Zero or few rows of any other type (unsigned-from-user-path,
  invalid-signature) unless your machine actually has qualifying
  binaries.
- Nav-rail Alerts badge count matches the row count: 0 badge when 0
  rows. If the badge shows a non-zero count with 0 rows, that's a
  drift bug in the badge cache (not this epic).

### 2b — Counter-test: prove the gate is what's suppressing

The most rigorous check — temporarily nuke the baseline epoch, restart,
and observe the flood return. This proves the gate is *causally
responsible* for the silence, not just a coincidental absence of
qualifying traffic.

```powershell
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
net stop ZenVizor
sqlite3.exe $db "DELETE FROM settings WHERE key = 'baseline.install_epoch_ms';"
sqlite3.exe $db "DELETE FROM settings WHERE key = 'baseline.setup_scan_done';"
sqlite3.exe $db "DELETE FROM apps;"          # remove seeded rows too
net start ZenVizor
Start-Sleep -Seconds 45                       # give apps time to talk
sqlite3.exe -readonly -header -column $db `
    "SELECT COUNT(*) FROM alerts WHERE type = 'FirstRunWanTalker';"
```

**Expected in this counter-test only:** dozens of `FirstRunWanTalker`
rows appear — the flood the epic exists to correct. Then re-run the
clean-state recipe to restore the gate for subsequent tests.

## Gate 3 — Genuine post-baseline first-run still fires

**What this proves.** The baseline gate isn't a permanent disable — an
app installed AFTER the 48 h settling window still trips
`FirstRunWanTalker` normally. Since waiting 48 hours isn't practical,
force the condition by backdating the install epoch.

```powershell
$db = 'C:\ProgramData\ZenVizor\zenvizor.db'
net stop ZenVizor
# Backdate the install epoch to 49 hours ago and clear all apps so any
# newly-observed process gets a fresh first_seen well past
# install_epoch + 48 h. Not touching setup_scan_done — the guard is
# still set, so no re-seed.
$fakeEpoch = [DateTimeOffset]::UtcNow.AddHours(-49).ToUnixTimeMilliseconds()
sqlite3.exe $db "UPDATE settings SET value = $fakeEpoch WHERE key = 'baseline.install_epoch_ms';"
sqlite3.exe $db "DELETE FROM apps;"
net start ZenVizor
Start-Sleep -Seconds 60      # apps talk; new apps.first_seen = now
sqlite3.exe -readonly -header -column $db `
    "SELECT type, severity, title FROM alerts WHERE type = 'FirstRunWanTalker' ORDER BY created_at DESC LIMIT 10;"
```

**Expected:** at least a few `FirstRunWanTalker` rows — the rule fires
normally for the "new" apps (their `first_seen` is right now, well
after the fake epoch + 48 h). Then re-run the clean-state recipe to
restore. Also unit-covered by
`FirstRunWanTalkerRuleTests.TryEvaluate_AppPastBaselineWindow_FiresNormally`
+ `AlertProducerTests.PostBaseline_GenuineFirstRun_RaisesNormally`.

## Gate 4 — Critical still fires inside the baseline window

**What this proves.** The gate touches only the Info first-run signal;
`UnsignedFromUserPathRule` fires throughout. This is the "roadmap
lock" line 94 of the epic doc — a dropper installed 10 minutes
post-install must still be caught.

**Manual repro is deliberately not scripted here.** Triggering
UnsignedFromUserPath requires an unsigned binary in a user-writable
path making a WAN connection — either building a small unsigned
console app and dropping it in `%LOCALAPPDATA%\Temp\` or grabbing an
unsigned utility from GitHub Releases (many are). That's fine as an
ad-hoc test, but scripting it in a verification doc would embed a
compile-and-run flow that decays fast. This gate ships **unit-tests
only** for CI-gated correctness
(`AlertProducerTests.CriticalUnsignedFromUserPath_StillFiresInsideBaselineWindow`);
run the manual repro at your discretion before ship if you want
belt-and-suspenders coverage against a real dropper.

## Gate 5 — Per-severity toggles route toasts correctly

**What this proves.** Slice 2 works end-to-end: the three toggles in
Settings each control exactly one severity, AND the same per-severity
gate governs the Send Test Notification button (it fires one toast
per enabled severity, none per disabled severity).

Launch the UI and navigate to Settings → Alerts. Confirm the section
now shows three peer rows (Critical / Warning / Info) plus the "Send
test notification" button.

**5a — Baseline positive test.** Turn all three severities ON. Click
**Send test notification**.

- **Expected:** three toasts fire in sequence (~700 ms apart), each
  titled `ZenVizor: Critical test`, `ZenVizor: Warning test`, and
  `ZenVizor: Info test`. Icons differ per severity.

**5b — Per-severity gate isolation.** For each severity in
`{Critical, Warning, Info}`:

1. Turn on ONLY that severity; leave the other two off.
2. Click **Send test notification**.
3. **Expected:** exactly one toast fires, titled with the enabled
   severity. Nothing else appears.

**5c — All-off silence.** Turn all three OFF. Confirm the button
disappears entirely (its `Visibility` binds to the derived
`ToastOnAlert` = OR of the three). This is the visible signal that no
toasts will fire — no click needed.

If Gate 5a fires the wrong count of toasts, the wiring on
`SetToastPreferences` + `ShowTestToast(severity)` in
`MainWindow.xaml.cs` is off. Cross-check the round-trip unit test
`SettingsContractTests.GetSettings_PerSeverityFields_RoundTripAcrossPipe`
— if that's green and this gate fails, the bug is in the UI cache
reseeding, not the IPC.

## Gate 6 — Legacy `toast.on_alert` intent honoured on upgrade

**What this proves.** A 1.1.x user with `toast.on_alert = '1'` who
upgrades to 1.2.0 doesn't silently lose notifications. This is the
user-agreed decision from Epic B open-decisions #4.

**Note:** on a *dev* install the legacy row is present regardless
(the initial migration seeds it), so this gate exercises the same
code path a real upgrade would hit. Fully covered by
`ToastPreferenceMigrationTests` (5 cases). Manual reproduction:

```powershell
# Simulate: user with all-toasts-on upgrades. First clear the
# per-severity keys (as if they didn't exist yet), leave
# toast.on_alert = '1', restart the service.
net stop ZenVizor
sqlite3.exe C:\ProgramData\ZenVizor\zenvizor.db `
    "DELETE FROM settings WHERE key LIKE 'toast.on_alert.%';"
net start ZenVizor
Start-Sleep -Seconds 5
sqlite3.exe -readonly -header -column C:\ProgramData\ZenVizor\zenvizor.db `
    "SELECT key, value FROM settings WHERE key LIKE 'toast.on_alert%';"
```

**Expected:** the three per-severity keys land back at `'1'` — the
migration honoured the legacy master. Repeat with `toast.on_alert = '0'`
first and confirm they all land at `'0'`.

## Gate 7 — Self-monitoring: seed emits no traffic

**What this proves.** Invariant #1 (zero own network) holds — the
setup-scan enumerates local processes only, no sockets or DNS or
probes.

Run the clean-state recipe. After the seed lands (gate 1), inside
the UI go to *Per-app* and filter for the ZenVizor process. The seed
is one-shot at startup, so any WAN traffic from ZenVizor's own PID in
the first 60 s of the run would be visible.

**Expected:** no outbound bytes attributed to `ZenVizor.Service.exe`.
Any outbound at all against invariant #1 is a fail — investigate
before shipping.

## Gate 8 — Seed is idempotent across service restarts

**What this proves.** `baseline.setup_scan_done` guards the seed. A
subsequent service start doesn't re-enumerate + re-insert.

```powershell
Set-Location C:\dev\zenvizor
# After a first start with the clean-state recipe:
$before = sqlite3.exe -readonly C:\ProgramData\ZenVizor\zenvizor.db `
    "SELECT COUNT(*) FROM apps;"
net stop ZenVizor
net start ZenVizor
Start-Sleep -Seconds 8
$after = sqlite3.exe -readonly C:\ProgramData\ZenVizor\zenvizor.db `
    "SELECT COUNT(*) FROM apps;"
Write-Host "apps count before=$before, after=$after"
Get-Content C:\ProgramData\ZenVizor\logs\service-*.log -Tail 100 |
    Select-String -Pattern "BaselineAppSeeder"
```

**Expected:**

- `before == after`. Restart didn't duplicate any rows.
- The log after the restart shows **no** `BaselineAppSeeder:
  enumerated / inserted` lines — the guard short-circuited the seed
  entirely.
- Second `Baseline install epoch initialized:` line is also **absent**
  — the epoch is written once, never overwritten.

## Sign-off

All 8 gates green → Epic B is ready to tag. Bump
`Directory.Build.props` `<Version>` to `1.2.0`, add release notes under
`docs/release-notes/v1.2.0.md`, and follow the prep-commit convention
from `9e59286` / `0e8996e`.
