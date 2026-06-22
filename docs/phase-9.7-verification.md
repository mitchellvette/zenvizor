# Phase 9.7 verification — re-cut MSI + Burn bundle; final manual gates

**Status:** in progress.
**Companion doc:** `docs/zenvizor-sprint-plan.md` Phase 9.7 (acceptance criteria + scope).
**Procedure source for gates 1–4:** `docs/phase-7-verification.md`. This doc
records the run against the **1.0.0** artifacts; gate scripts are not
duplicated.
**Test environment:** VirtualBox VM `ZenVizor-Phase7` (snapshots `Base` + `WithDotNet10`) for installer gates; host machine (the dev box) for the self-monitoring zero-own-traffic gate and the full-system pass.

---

## Part A — headless rebuild + CI acceptance

Re-cut on top of HEAD (`145d141` "MVP ship-prep: 1.0.0 version + GPL-3.0-or-later + installed README"). All 9.1–9.c changes are committed.

### Build + test

```
dotnet clean    ZenVizor.slnx -c Release
dotnet build    ZenVizor.slnx -c Release    # 0 warnings, 0 errors
dotnet test     ZenVizor.slnx -c Release --no-build
# Integration  59  |  Core 248  |  Attribution 69  |  IPC 61  |  Storage 140
# Total       577  passing, 0 failed, 0 skipped
```

### Bin sizes (9.1 acceptance)

| Project | Actual | Phase 9.1 target | Notes |
|---------|--------|------------------|-------|
| `ZenVizor.Service` | **19 MB** | ~17 MB | +2 MB vs projection; acceptable |
| `ZenVizor.Ui`      | **32 MB** | ~19 MB | +13 MB vs projection; dominated by `libSkiaSharp.dll` (11 MB), `Wpf.Ui.dll` (5.9 MB), `OpenTK.dll` (5.7 MB) — all win-x64 only, no RID bloat |
| `ZenVizor.Cli`     | **3.9 MB** | n/a | not in original projection |

Meaningful reduction held: UI dropped from **71 MB → 32 MB** (the 9.1 win). No `runtimes/` subdirectories present anywhere — RID-pin enforced.

### MSI smoke checks (`installer/bin/x64/Release/ZenVizor.msi`)

- Size: **16.4 MB** (target 25–30 MB; under budget)
- `ProductVersion` = `1.0.0`
- `UpgradeCode` = `{DAB3A65D-8347-44EE-8946-B8CD57474539}` (locked across 0.1.1 → 1.0.0; MajorUpgrade path will work)
- `Manufacturer` = `ZenVizor` (see decision DEC-1 below)
- File table contains all four ship docs: `LICENSE.txt`, `TRADEMARK.txt`, `NOTICES.txt`, `README.txt`

### Bundle smoke checks (`installer/Bundle/bin/x64/Release/ZenVizorSetup.exe`)

- Size: **77.2 MB** (≈ 16 MB MSI + 60 MB runtime payload + Burn UX)
- `License.rtf` updated to GPL summary; no "All rights reserved" left
- Runtime payload pin: `windowsdesktop-runtime-10.0.8-win-x64.exe`, SHA512 verified at build

### Gate-4 staging

For the upgrade-in-place gate, a **0.1.1**-stamped bundle was rebuilt off HEAD by temporarily editing `<Version>` in `Directory.Build.props`, building, archiving, then restoring `1.0.0`. Working tree was clean after.

| File | Size | Purpose |
|------|------|---------|
| `installer/Bundle/bin/x64/Release/ZenVizorSetup.exe`       | 77.2 MB | canonical 1.0.0 (= ZenVizorSetup-1.0.0.exe) |
| `installer/Bundle/bin/x64/Release/ZenVizorSetup-1.0.0.exe` | 77.2 MB | explicit 1.0.0 archive |
| `installer/Bundle/bin/x64/Release/ZenVizorSetup-0.1.1.exe` | 77.2 MB | Gate-4 "old version" stamp |

Cosmetic note: the 0.1.1 stamp contains 1.0.0-era runtime code (GPL headers, RID-pin, README). Gate 4 tests upgrade mechanics (ARP entry replacement, service swap, DB preservation), not content diff — content equivalence is irrelevant for the gate.

### CI workflow

`.github/workflows/ci.yml` builds the same MSI + bundle artifacts on `windows-latest` per push to `main`. The RID-bloat regression guard (added in 9.1) is in place. No workflow changes needed.

### Decisions surfaced during Part A

**DEC-1 — MSI `Manufacturer` stays `ZenVizor`** *(resolved 2026-06-22, option 1)*

The Phase 9.7 acceptance line "Add/Remove Programs entry credits 'Mitchell Gray' (via the `<Copyright>` flowing into the MSI metadata)" assumed Mitchell Gray would surface in ARP. In practice `<Copyright>` from `Directory.Build.props` flows into the per-file `FileVersionInfo` (right-click → Properties → Details) but does **not** flow into MSI `Manufacturer` — that's a separate WiX `Package`-level attribute. ARP **Publisher** column reads from `Manufacturer`.

**Decision:** keep `Manufacturer = "ZenVizor"`. Brand-as-publisher is the conventional pattern for distributed software (VS Code → "Microsoft Corporation", Audacity → "Audacity Team"); per-file `<Copyright>` continues to credit Mitchell Gray in file properties + the bundle's License panel, which is sufficient attribution. The Phase 9.7 acceptance text in the sprint plan over-specified the surface — credit reaches the user, just via a different control than ARP Publisher. Update the acceptance criterion text to reflect this (drop "via ARP Publisher" framing) as a side-task before the v1.0.0 tag.

---

## Part B — VM installer gates

Run sequence on `ZenVizor-Phase7` (snapshots `Base`, `WithDotNet10`). Procedure per `docs/phase-7-verification.md`; same scripts, against the 1.0.0 bundle in `Z:\` (auto-mapped to `installer\Bundle\bin\x64\Release\` on host).

| Gate | Snapshot | Status |
|------|----------|--------|
| 1 — clean Win11, no .NET 10 (chained runtime + MSI install) | `Base` | **PASS** (2026-06-22; finding F1) |
| 2 — clean Win11 with .NET 10 pre-installed (detect, skip runtime) | `WithDotNet10` | **PASS** (2026-06-22) |
| 3A — default uninstall via Settings → Apps (preserves data) | (post-G1 or G2) | **PASS** (2026-06-22) |
| 3B — `ZenVizorSetup.exe /uninstall REMOVE_DATA=1 /quiet` (wipes data) | (post-reinstall) | **PASS** (2026-06-22) |
| 4 — upgrade-in-place 0.1.1 → 1.0.0 (single ARP entry, service swap, DB preserved) | `WithDotNet10` | **PASS** (2026-06-22) |

### 1.0.0-specific spot-checks layered onto the gate runs

- [ ] Fresh launch → Reports page opens on today's date (not 2026-06-08) — Phase 9.2.
- [ ] Add/Remove Programs entry shows **DisplayVersion `1.0.0`**.
- [ ] Start menu under ZenVizor shows two entries: **ZenVizor** + **ZenVizor — Read Me**; the Read Me opens `README.txt` in Notepad — Phase 9.b.
- [ ] `%ProgramFiles%\ZenVizor\` root contains `LICENSE.txt`, `TRADEMARK.txt`, `NOTICES.txt`, `README.txt` — Phase 9.c + 9.b.
- [ ] Bundle install panel shows the GPL summary text, not "All rights reserved" — Phase 9.c.
- [ ] `%ProgramFiles%\ZenVizor\` total size ≤ ~140 MB — Phase 9.7 acceptance.
- [ ] On a Chrome session with mixed CDN traffic: App Detail Connections grid surfaces one emphasized row per unresolved high-byte IP, not per-port duplicates — Phase 9.5.

---

## Part C — Host self-monitoring (Phase 6.8b zero-own-traffic invariant) — **PASS** (2026-06-22)

Runs on the host (dev machine) using `scripts/install-dev.ps1` — same runtime code as the bundle install, much more meaningful traffic surface.

**Procedure:**

1. Ensure host has the current `main` build running (`.\scripts\install-dev.ps1` if the service isn't already up against this commit).
2. Let it run with normal browsing/work load for **≥ 1 hour**.
3. Query the DB (elevated PowerShell, since `%ProgramData%\ZenVizor\zenvizor.db` is ACL'd to SYSTEM + Administrators).

Aggregate byte totals per ZenVizor-owned image, summed across all traffic sample buckets:

```powershell
$db = "$env:ProgramData\ZenVizor\zenvizor.db"
sqlite3.exe -readonly -header -column $db "SELECT a.image_name, COALESCE(SUM(ts.bytes_up),0) AS total_up, COALESCE(SUM(ts.bytes_down),0) AS total_down FROM apps a LEFT JOIN process_sessions ps ON ps.app_id = a.app_id LEFT JOIN traffic_samples ts ON ts.session_id = ps.session_id WHERE a.image_name LIKE 'ZenVizor%' OR a.image_name LIKE 'zvctl%' GROUP BY a.image_name ORDER BY a.image_name"
```

Count of `connections` rows attributed to any ZenVizor process:

```powershell
sqlite3.exe -readonly -header -column $db "SELECT COUNT(*) AS zenvizor_conn_rows FROM connections c JOIN process_sessions ps ON c.session_id = ps.session_id JOIN apps a ON ps.app_id = a.app_id WHERE a.image_name LIKE 'ZenVizor%' OR a.image_name LIKE 'zvctl%'"
```

**Pass marks:**

- [ ] First query: either no rows OR rows for `ZenVizor.Service.exe` / `ZenVizor.Ui.exe` / `zvctl.exe` with `total_up = 0` AND `total_down = 0`. (Image presence in `apps` is fine — the table dedupes processes; non-zero byte counts are the failure mode.)
- [ ] Second query: `zenvizor_conn_rows = 0`.

---

## Part D — Full-system pass — **PASS** (2026-06-22)

Ran on host alongside Part C.

- [x] **D1 — Attribution sane:** browser / editor / svchost drills all surfaced sensible publisher + signer state where available; svchost row listed multiple hosted services.
- [x] **D2 — Live ↔ history ↔ daily report reconcile:** Dashboard live totals tracked the History rollup and the daily-report per-app numbers within acceptable variance.
- [x] **D3 — Performance budget:**
  - [x] Idle CPU **0.31%** (target < 1%). Sample command:
    ```powershell
    (Get-Counter '\Process(ZenVizor.Service)\% Processor Time' -SampleInterval 1 -MaxSamples 30).CounterSamples.CookedValue | Measure-Object -Average | Select-Object -ExpandProperty Average
    ```
  - [x] Service active private working set **42 MB** (target ~80 MB). Sample command:
    ```powershell
    [math]::Round((Get-Counter "\Process(ZenVizor.Service)\Working Set - Private").CounterSamples.CookedValue / 1MB, 1)
    ```
    Or read "Memory (active private working set)" from Task Manager → Details → ZenVizor.Service.exe.

**D3 measurement note:** `Get-Process .WorkingSet64` returns *total* working set, which double-counts shared pages (the .NET 10 CoreCLR DLLs, native sqlite/TraceEvent code, mapped views). For a service-cost reading, use **private** working set — the perf counter `\Process(<name>)\Working Set - Private` exposes the same number Task Manager shows as "Active private working set." For ZenVizor.Service on a typical desktop, total WS reads ~110 MB while private WS reads ~42 MB; the latter is the honest cost-to-host measurement and the one the budget tracks.

---

## Findings landed during Phase 9.7

### F2 — Ship blocker: AppDetail Connections grid byte-column sort regression (9.5)

**Surfaced:** Part D D1/D2 visual walk, 2026-06-22.

**Symptom:** Sorting the Connections grid Up or Down column produced a lexicographic order of the formatted byte string ("3 KB" outranked "200 MB" because `'3'` > `'2'`). Reproduced on Chrome where a 832 MB Down endpoint sorted below 95.2 KB.

**Root cause:** `EndpointGroupViewModel` (introduced by Phase 9.5) exposed only formatted `UpText` / `DownText` strings; the underlying `long` byte values were consumed by `PerAppPage.FormatBytes` in the constructor and discarded. The XAML Up/Down columns at `AppDetailPage.xaml:983` and `:991` bound to `UpText` / `DownText` with no `SortMemberPath`, so WPF's `DataGrid` sorted by the bound property — a string. The `PerAppPage` and `ReportsPage` Up/Down columns get this right (`SortMemberPath="BytesUp"` / `"BytesDown"`, with an explanatory comment in both files); Phase 9.5's row-VM rewrite dropped both halves of that pattern.

**Fix:**

- `EndpointGroupViewModel` now exposes `BytesUp` and `BytesDown` (`long`) alongside the formatted text properties.
- `AppDetailPage.xaml` Up/Down columns gained `SortMemberPath="BytesUp"` and `SortMemberPath="BytesDown"`, plus an inline comment mirroring the `PerAppPage` precedent so the lesson survives the next rewrite.
- New regression-firewall test in `tests/ZenVizor.Integration.Tests/EndpointGroupViewModelSortSurfaceTests.cs` references `BytesUp` / `BytesDown` by name (compile-time guard) and demonstrates the lexical-vs-numeric divergence on representative values.

**Validation:**

- Full test suite: 579 tests, 0 failures (+2 new sort-surface tests).
- MSI + canonical 1.0.0 bundle re-cut on top of the fix.
- VM Gate 1 re-run against the fixed bundle — install/service-start/ARP-version all clean.
- Host visual: 832 MB Down endpoint now sorts to the top in DESC order; lexical inversion gone.

### F1 — .NET Desktop Runtime ARP-row count dropped 2 → 1

**Surfaced:** Gate 1, 2026-06-22.

**Observation:** Phase 7's Gate 1 walk-through (2026-06-20) saw two ARP entries for the .NET 10.0.8 Desktop Runtime (one framework, one host) after the bundle's chained install. Phase 9.7's Gate 1 run against the same Base snapshot with the same SHA-pinned runtime payload sees only **one** row matching `Windows Desktop Runtime` under the standard `$_.SystemComponent -ne 1` filter. ZenVizor ARP entry is unchanged (one row, `DisplayVersion 1.0.0`).

**Significance:** none for gate acceptance. The runtime is installed (proven by `sc.exe query ZenVizor` → RUNNING — the service can't load without it). The shape of MS's ARP registration is outside our control. Most likely MS changed `SystemComponent=1` on one of the two MSIs in their EXE wrapper between Phase 7 and this re-cut. No change action required on ZenVizor's side.

**Action:** documented; no code change.

---

## Sign-off

- [ ] All Part B gates pass against the 1.0.0 bundle.
- [ ] Part C zero-own-traffic invariant gate passes.
- [ ] Part D full-system pass passes.
- [ ] DEC-1 resolved (option 1 or option 2; either chosen and recorded).
- [ ] `README.md` "Status: pre-release" line flipped to "1.0.0".
- [ ] `v1.0.0` tag created on the HEAD that passed.
- [ ] Pushed to origin (explicit user approval at push time).
