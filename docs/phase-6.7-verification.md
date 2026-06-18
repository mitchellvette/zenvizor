# Phase 6.7 — Manual QA verification

Phase 6.7 closes the pre-MVP backlog item P4 (wire all six alert
producers) and ships user-tunable thresholds for the three Phase 6.7
rules. The interim slice-A and slice-B verification docs in this
folder are superseded by this one.

After this phase passes QA, the alert catalog has six wired
producers; users can tune three thresholds from Settings → Alerts; and
the QA debug hook `zvctl alerts run-rollup-rules-now` lets the
rollup-source rule fire on demand without waiting for the next UTC
midnight.

---

## Pre-flight dependencies

```powershell
Get-Command curl.exe -ErrorAction SilentlyContinue   # ships with Windows
Get-Command sqlite3.exe -ErrorAction SilentlyContinue
# sqlite3.exe is needed by the unusual-daily-volume seed script. If
# missing: winget install --id SQLite.SQLite
```

Both QA scripts run from `scripts\qa\`. Several need administrator
elevation (the invalid-signature trigger and the unusual-volume seed
write to elevated surfaces).

---

## 0. One-time build + reinstall

```powershell
# === Elevated PS — stop the running service before rebuild. ===
Stop-Service ZenVizor -ErrorAction SilentlyContinue
```

```powershell
# === Non-elevated PS — build + tests. ===
cd C:\dev\zenvizor
dotnet build .\ZenVizor.slnx -c Release
dotnet test  .\ZenVizor.slnx -c Release
```

Test totals to expect: **459 pass.**

```powershell
# === Elevated PS — reinstall the dev service. ===
cd C:\dev\zenvizor
.\scripts\uninstall-dev.ps1
.\scripts\install-dev.ps1
sc.exe query ZenVizor                  # confirm STATE : 4  RUNNING
```

```powershell
# === Non-elevated PS — smoke the new catalog state. ===
Set-Alias zvctl C:\dev\zenvizor\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe
zvctl alerts catalog                   # 6 producer-wired
```

The catalog output should show six rows, all marked `wired`.

---

## Gate 1 — Settings page: Alerts threshold card

Open the UI and navigate to **Settings → Alerts**.

Below the "Show desktop notifications" toggle (and the
"Send test notification" button when the toggle is on), a hairline
separator divides the notification group from the threshold rows.
Below the separator: an "Alert thresholds" subheading with three
rows:

| Setting | Default | Range |
|---|---|---|
| Large-download threshold | 50 MB | 1–1024 |
| Outbound-heavy floor | 10 MB | 1–1024 |
| Unusual-volume sensitivity | 2.5 | 1.0–10.0 |

Each row shows label + description + a NumberBox aligned right.

Edit one of the values (e.g. drop the Large-download threshold to 10
MB). The NumberBox debounces edits at 500 ms — settle the value, watch
for any error banner. None should appear.

Confirm the value persists through a restart:

```powershell
# Elevated PS:
Restart-Service ZenVizor
```

Reload Settings; the value you set should still be there.

---

## Gate 2 — InvalidSignatureRule (Critical · Capture · 24h)

```powershell
# Elevated PS:
cd C:\dev\zenvizor
.\scripts\qa\trigger-invalid-signature.ps1
```

The script creates a short-lived self-signed cert, signs a clone of
`curl.exe`, runs it against `https://1.1.1.1/cdn-cgi/trace`.

**Verify:**
- Alerts page within ~10–15 s: Severity Critical, Type
  `InvalidSignature`, Title `Program with invalid signature talking to
  the network: zenvizor-invalid-<N>.exe`. Detail names the cert
  subject + image path.
- `zvctl alerts list --type InvalidSignature` returns the row.
- Re-run the script within 24 h. The existing alert's detail's
  connection count advances; no new row.

**Cleanup:**
```powershell
Remove-Item $env:TEMP\zenvizor-invalid-*.exe -Force
Remove-Item Cert:\CurrentUser\My\<thumbprint>
```

---

## Gate 3 — FirstRunWanTalkerRule (Info · Capture · forever)

```powershell
# Non-elevated PS:
cd C:\dev\zenvizor
.\scripts\qa\trigger-first-run-wan.ps1
```

The script copies `curl.exe` to a fresh randomized name in `%TEMP%`,
runs it against `https://1.1.1.1/cdn-cgi/trace`. The new image path
gets a new `apps` row with `first_seen = now`, clearing the rule's
60 s gate.

**Verify:**
- Alerts page within ~10–15 s: Severity Info, Type
  `FirstRunWanTalker`, detail names the first-seen timestamp + N s
  delta between first-seen and first connection.
- Re-run the SAME script: no new alert (cooldown is effectively
  permanent per app — first run only happens once per `apps` row).
- Generate a fresh randomized name and re-run: a new alert.

**Cleanup:**
```powershell
Remove-Item $env:TEMP\zenvizor-firstrun-*.exe -Force
```

---

## Gate 4 — LargeDownloadRule (Info · Capture · 24h)

```powershell
# Non-elevated PS:
curl.exe --silent --output NUL https://speed.cloudflare.com/__down?bytes=104857600
```

100 MB from Cloudflare's `__down` endpoint — clears the default 50 MB
threshold within the 60 s window comfortably on any non-dial-up
connection.

**Verify:**
- Alerts page within ~10–15 s: Severity Info, Type `LargeDownload`,
  Title `Large download by curl.exe`, detail names the byte amount,
  the remote IP:port, and the contributing PID.
- `zvctl alerts list --type LargeDownload` returns the row.
- Tune the threshold via Settings → Alerts. Drop to 10 MB,
  dismiss the existing alert, then re-trigger with a 20 MB pull:
  ```powershell
  curl.exe --silent --output NUL https://speed.cloudflare.com/__down?bytes=20971520
  ```
  The lowered threshold fires on the smaller pull.

---

## Gate 5 — OutboundHeavyRule (Warning · Capture · 24h)

```powershell
# Non-elevated PS:
cd C:\dev\zenvizor
.\scripts\qa\trigger-outbound-heavy.ps1
```

POSTs ~15 MB to httpbin.org. Outbound dominates inbound (~7500:1) and
clears the 10 MB floor.

**Verify:**
- Alerts page within ~10–15 s: Severity Warning, Type `OutboundHeavy`,
  Title `Outbound-heavy app: curl.exe`, detail names the upload total,
  the ratio, and the contributing PID(s).
- Tune the floor via Settings → Alerts. Bump to 100 MB; re-trigger
  with the default 15 MB payload — no new alert. Re-run with
  `-PayloadMb 110` — fires.

**Cleanup:**
```powershell
Remove-Item $env:TEMP\zenvizor-outbound-payload.bin -Force
```

---

## Gate 6 — UnusualDailyVolumeRule (Warning · Rollup · 24h)

This rule is daily-rollup-sourced — without the debug
`run-rollup-rules-now` hook, you'd have to wait 14 days for a real
baseline + 1 day for a spike. The seed script does both synthetically.

```powershell
# === Non-elevated PS — make sure chrome.exe exists in apps. ===
# Open Chrome, visit a few sites for ~30 s. Skip if Chrome is your
# daily driver already.
```

```powershell
# === Elevated PS — seed + force evaluation. ===
cd C:\dev\zenvizor
.\scripts\qa\seed-unusual-volume.ps1
```

The script inserts 14 baseline rows at 100 MB/day for chrome.exe and
one spike row for yesterday at 300 MB. Default k=2.5 → threshold 250
MB → 300 MB clears it, delta 200 MB clears the hardcoded 50 MB floor.
Then it calls `zvctl alerts run-rollup-rules-now` to bypass the
date-roll gate.

**Verify:**
- Alerts page immediately: Severity Warning, Type
  `UnusualDailyVolume`, Title `Unusual daily traffic: chrome.exe`,
  detail names yesterday's date (UTC), the byte amount, the
  multiplier ratio (3.0×), and the typical-baseline phrase.
- `zvctl alerts list --type UnusualDailyVolume` returns the row.
- Re-running `zvctl alerts run-rollup-rules-now` within 24 h does not
  raise a second alert (per-app cooldown holds even after a forced
  re-evaluation).
- Tune the sensitivity to 4.0 via Settings → Alerts. Re-seed with
  `-SpikeMb 350`; that's below 4.0 × 100 = 400 MB → no new alert.

**Cleanup:**
```powershell
cd C:\dev\zenvizor
.\scripts\qa\seed-unusual-volume.ps1 -Cleanup
```

---

## Negative checks

- **Signed binary, ordinary path:** Run `curl.exe --silent --output
  NUL https://1.1.1.1/cdn-cgi/trace` from System32. No
  `InvalidSignature` or `FirstRunWanTalker` alert (existing apps row,
  signature is trusted).
- **Small download (below threshold):**
  `curl.exe --silent --output NUL https://speed.cloudflare.com/__down?bytes=10485760`
  (10 MB). No `LargeDownload` alert.
- **Balanced traffic:** A normal Chrome browsing session has roughly
  balanced up/down ratio. No `OutboundHeavy` alert.
- **App with < 14 baseline days:** A freshly-installed app won't have
  enough rows in `traffic_daily` for the rule to evaluate. No
  `UnusualDailyVolume` alert until it accumulates 14 days of history.

---

## Pass criteria

- `zvctl alerts catalog` reports **6 producer-wired**.
- Settings → Alerts shows the threshold card with three NumberBoxes
  hydrated to the persisted values.
- Threshold edits debounce-save without an error banner; values
  survive a service restart.
- All six gates above produce the expected alert with the expected
  title + detail copy.
- All four negative checks come back clean.
- Tuning a threshold via Settings affects subsequent rule evaluations
  on the next flush (no service restart needed).
- The dismiss-and-cooldown contract holds for every rule.
- 459 headless tests pass.

**Cleanup after the gate:**

```powershell
# Non-elevated PS:
zvctl alerts list --state active     # note any test alerts
# Dismiss them through the UI or via:
zvctl alerts dismiss <id>
```

The synthetic baseline rows seeded for UnusualDailyVolume should be
cleaned via the `-Cleanup` flag on `seed-unusual-volume.ps1` —
otherwise the rule's baseline contains your synthetic numbers for the
next 15 days, skewing real evaluations.

---

## Retiring the slice docs

`docs/phase-6.7-slice-a-verification.md` covered Slice A (S1 + Rules
1 & 2 + Settings infra). It's superseded by Gates 2 and 3 above. Slice
B was committed without a doc per the
`feedback_verification_doc_granularity` memory. Both are consolidated
in this phase-level doc; the slice-a doc can be deleted whenever the
git history reference is no longer needed.
