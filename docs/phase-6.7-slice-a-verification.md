# Phase 6.7 Slice A — QA verification (InvalidSignature + FirstRunWanTalker)

Phase 6.7 wires the remaining five alert producers per backlog item P4.
This slice (A) lands the two cheapest — `InvalidSignatureRule` and
`FirstRunWanTalkerRule` — both per-WAN-connection event-time rules that
slot into the existing `IAlertEventSink` hook without aggregator or
rollup changes. Slices B + C land Rules 3-5 and the Settings UI card.

CI doesn't gate the running-service end-to-end firing — same shape as
the Phase 6.1 UnsignedFromUserPath gate. The headless paths (rule
predicates, producer dedupe, IPC round-trips) are covered by 433
passing tests; this doc walks the real-box gates the human signs off.

---

## Pre-flight dependencies

```powershell
Get-Command curl.exe -ErrorAction SilentlyContinue   # ships with Windows
Get-Command signtool.exe -ErrorAction SilentlyContinue
# signtool.exe ships with the Windows SDK. If missing:
#   winget install --id Microsoft.WindowsSDK
# OR use Set-AuthenticodeSignature directly (built into PowerShell),
# which the scripts below default to.
```

`Set-AuthenticodeSignature` (built-in PS cmdlet) covers the signing path
without requiring the Windows SDK. signtool is only needed if you want
finer-grained timestamp / hash-algorithm control.

---

## 0. One-time build + reinstall

The new rules ship in `ZenVizor.Service.dll`. Reinstall the dev service
so the running binary picks them up. Each block below assumes the shell
opened in its default directory — every PS block sets its own working
directory rather than relying on the previous one.

```powershell
# === Elevated PS — stop the running service so the rebuild doesn't
#     trip a file lock on bin\Release\ZenVizor.Service.dll. ===
Stop-Service ZenVizor -ErrorAction SilentlyContinue
```

```powershell
# === Non-elevated PS — build + run headless tests. ===
cd C:\dev\zenvizor
dotnet build .\ZenVizor.slnx -c Release
dotnet test  .\ZenVizor.slnx -c Release
```

Test totals to expect: **432 pass.**

```powershell
# === Elevated PS — uninstall + reinstall the dev service. The cd is
#     load-bearing: elevated PS defaults to C:\Windows\System32 so a
#     bare .\scripts\... resolves wrong. ===
cd C:\dev\zenvizor
.\scripts\uninstall-dev.ps1
.\scripts\install-dev.ps1
sc.exe query ZenVizor                  # confirm STATE : 4  RUNNING
```

```powershell
# === Non-elevated PS — smoke the new catalog state. ===
Set-Alias zvctl C:\dev\zenvizor\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe
zvctl alerts catalog                   # 3 wired: UnsignedFromUserPath,
                                       # InvalidSignature, FirstRunWanTalker
```

---

## Gate 1 — InvalidSignatureRule fires on a tampered-after-signing binary

The rule's predicate is `signature_status == "Invalid"`, which
`WinVerifyTrustSignatureVerifier` returns when a binary has an
Authenticode signature that doesn't validate against the trusted root
store. Two paths to that state:

1. **Untrusted self-signed cert** — sign with a cert whose root isn't
   in `TrustedRootCertificationAuthorities`.
2. **Tampered-after-signing** — sign a binary, then modify a byte
   after signing.

Both surface as Invalid. The script below uses path 1 (cleanest, no
binary patching).

**Save as `scripts/qa/trigger-invalid-signature.ps1`:**

```powershell
#requires -RunAsAdministrator
# Phase 6.7 Slice A QA — InvalidSignatureRule
#
# 1. Create a self-signed code-signing cert in CurrentUser\My (NOT added
#    to TrustedRoot, so the signed binary won't validate).
# 2. Clone curl.exe to %TEMP% under a fresh name.
# 3. Sign the clone with the untrusted cert.
# 4. Run the clone with a WAN URL; the connection fires the alert.
#
# Cleanup: delete the temp binary + cert when you're done. The cert
# lives in CurrentUser\My; Remove-Item Cert:\CurrentUser\My\<thumbprint>.

$ErrorActionPreference = 'Stop'

$cert = New-SelfSignedCertificate `
    -Subject "CN=ZenVizor QA Invalid-Sig" `
    -Type CodeSigningCert `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddDays(7)
Write-Host "Self-signed cert thumbprint: $($cert.Thumbprint)"

$tempDir = $env:TEMP
$source  = "$env:WINDIR\System32\curl.exe"
$dest    = Join-Path $tempDir ("zenvizor-invalid-{0}.exe" -f (Get-Random))
Copy-Item -Path $source -Destination $dest -Force
Write-Host "Cloned curl.exe to: $dest"

Set-AuthenticodeSignature -FilePath $dest -Certificate $cert | Out-Null
$sig = Get-AuthenticodeSignature -FilePath $dest
Write-Host "Signature status: $($sig.Status)"
# Expected: NotTrusted (the chain doesn't lead to a trusted root).
# ZenVizor's verifier classifies this as 'Invalid'.

Write-Host "Making WAN connection from the signed-but-untrusted binary..."
& $dest --silent --output NUL https://1.1.1.1/cdn-cgi/trace
Write-Host "Done. The alert should land within ~10 s (one flush cycle)."
```

**Verification:**

1. Stage the script and run it from an elevated PowerShell. Watch for
   "Signature status: NotTrusted" in the output.
2. **In the UI:** Alerts page should show a new row within ~10 s:
   - Severity: Critical
   - Type: `InvalidSignature`
   - Title: `Program with invalid signature talking to the network: zenvizor-invalid-<N>.exe`
   - Detail: lists `Signer: ZenVizor QA Invalid-Sig`, image path, first
     connection timestamp, connection count.
3. **Via CLI:**
   ```powershell
   zvctl alerts list --type InvalidSignature
   ```
   The row appears in `--state active`.
4. **Confirm cooldown:** Run the script again within 24 h. The existing
   alert's `Connections so far: N` count should advance (re-fire absorbed
   by the producer's dedupe state machine), no new row.
5. **Dismiss + verify:**
   ```powershell
   zvctl alerts dismiss <id>
   ```
   Re-run the script within 24 h — no new alert (cooldown). After 24 h
   it re-arms.

**Cleanup:**
```powershell
Remove-Item $env:TEMP\zenvizor-invalid-*.exe -Force
Remove-Item Cert:\CurrentUser\My\<thumbprint>
```

---

## Gate 2 — FirstRunWanTalkerRule fires on a freshly-installed binary

The rule's predicate is `(flushTime - apps.first_seen) ≤ 60 s` AND
a WAN connection happened. `apps.first_seen` is set on INSERT when the
attribution pipeline first sees an `(image_path, publisher)` combination.
Copy any existing exe to a brand-new path and the resulting `apps`
row will have `first_seen = now`.

**Save as `scripts/qa/trigger-first-run-wan.ps1`:**

```powershell
# Phase 6.7 Slice A QA — FirstRunWanTalkerRule
#
# Copies curl.exe to a fresh randomized filename in %TEMP%, runs it
# against a WAN URL within the rule's 60s window. Because the new image
# path has never been seen before, the attribution pipeline INSERTs a
# fresh apps row with first_seen=now, and the rule's predicate clears.
#
# No elevation required. Run from any user shell.

$ErrorActionPreference = 'Stop'

$source = "$env:WINDIR\System32\curl.exe"
$dest   = Join-Path $env:TEMP ("zenvizor-firstrun-{0}.exe" -f (Get-Random))
Copy-Item -Path $source -Destination $dest -Force
Write-Host "Cloned curl.exe to: $dest"

Write-Host "Making WAN connection from the freshly-installed binary..."
& $dest --silent --output NUL https://1.1.1.1/cdn-cgi/trace
Write-Host "Done. The alert should land within ~10 s (one flush cycle)."
```

**Verification:**

1. Run the script. No elevation required.
2. **In the UI:** Alerts page should show a new row within ~10 s:
   - Severity: Info
   - Type: `FirstRunWanTalker`
   - Title: `Newly-installed program reached the network: zenvizor-firstrun-<N>.exe`
   - Detail: `<imageName> was first observed at <ts> and opened its
     first network connection at <ts> (<N> s after first observed).`
3. **Via CLI:**
   ```powershell
   zvctl alerts list --type FirstRunWanTalker
   ```
4. **Confirm one-shot behavior:** Run the SAME script (same temp file
   stays in place) again. No new alert — the cooldown is effectively
   permanent per app, by design. The alert is "first run," not "every
   run."
5. **Confirm fresh-path re-fires:** Run a copy of the script that
   generates a new randomized filename. A fresh `apps` row → fresh
   first_seen → fresh alert.

**Cleanup:**
```powershell
Remove-Item $env:TEMP\zenvizor-firstrun-*.exe -Force
```

---

## Negative checks (both rules)

These confirm the rules don't fire when they shouldn't — same shape as
the existing Phase 6.1 negative gate for `UnsignedFromUserPath`.

- **Signed binary, ordinary path:** Run `curl.exe --silent --output
  NUL https://1.1.1.1/cdn-cgi/trace` directly (no copy, no re-sign).
  No `InvalidSignature` or `FirstRunWanTalker` alert (existing apps
  row, signature is trusted).
- **First-run signed binary:** Copy `curl.exe` to a new path WITHOUT
  re-signing, then run it. A `FirstRunWanTalker` alert SHOULD fire
  (first-seen + WAN). No `InvalidSignature` alert (signature still
  valid).
- **Long-running app new connection:** Continue using your normal
  Chrome / Slack / etc. Those apps have `first_seen` ages of hours/
  days, outside the 60 s window. No `FirstRunWanTalker` alert.

---

## Pass criteria

- `zvctl alerts catalog` shows three `wired` producers
  (UnsignedFromUserPath, InvalidSignature, FirstRunWanTalker).
- Both gates fire on the trigger script and produce alerts matching
  the title + detail copy above.
- Both rules clear the negative checks.
- Dismissing an alert from either rule and re-running the trigger
  within the cooldown window does NOT raise a new alert.
- 432 headless tests pass.

**Next slices:**
- **B:** S2 (ITrafficStateLookup + aggregator changes) + Rules 3 & 4
  (LargeDownload + OutboundHeavy) + multi-PID detail enrichment.
- **C:** S3 (date-roll gate + debug RunRollupRulesNow IPC) +
  Rule 5 (UnusualDailyVolume) + UI Settings card + final catalog flip.
