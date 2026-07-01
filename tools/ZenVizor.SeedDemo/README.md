# ZenVizor.SeedDemo — dev-only marketing-screenshot seeder

> **DEV-ONLY. This tool never ships.** It is deliberately excluded from
> `ZenVizor.slnx`, the CI matrix, and the installer, and its `.csproj`
> **hard-fails any Release build**. It exists purely to populate a
> *throwaway* SQLite store with fixed, synthetic, privacy-safe data so the
> app can be pointed at it to capture website/marketing screenshots.

It never touches the real `%ProgramData%\ZenVizor\` store: `--data-dir` is
required (there is no default), and the tool refuses to run if that path
resolves to the production data directory.

---

## What it produces

A single throwaway `zenvizor.db` (schema-migrated, then seeded) containing a
fixed synthetic dataset that mirrors what the ZenVizor website hardcodes:

- **5 apps** — `chrome.exe` (Google LLC, signed), `svchost.exe` (Microsoft,
  signed; hosts `Dnscache, BITS, wuauserv`), `OneDrive.exe` (Microsoft,
  signed), `unknown_setup.exe` (unsigned, user-writable path — the risky one),
  `LegacyTool.exe` (Example Corp, invalid signature).
- **Rollups** (`traffic_daily` / `traffic_hourly`) across ~30 days plus
  high-res `traffic_samples` for the last few hours, so the Overview
  drill-downs, per-app history, and the Daily Report all render real curves.
- **9 connections** — WAN endpoints only, using RFC 5737 documentation IPs
  (`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`). The **one** real IP
  is Cloudflare's ECH resolver `162.159.61.3` (matches the website FAQ);
  its `resolved_host` is intentionally NULL to show the ECH "unresolved" case.
- **6 alerts** — one of each rule type (UnsignedFromUserPath, InvalidSignature,
  OutboundHeavy, UnusualDailyVolume, FirstRunWanTalker, LargeDownload),
  rendered with the app's **real** rule-template copy (not website paraphrases).

### Privacy

Everything is synthetic. Identity is `demo` (e.g. `C:\Users\demo\Downloads\`).
No real usernames, machine names, private/LAN IPs, or loopback addresses.
Local-class traffic exists only as aggregate byte counts in the rollup tiers
(to drive the Wan/Local hero split); the `connections` table holds only WAN
documentation IPs.

---

## Prerequisites

- **.NET 10 SDK** — same as the rest of the repo; nothing extra to install.
- **An elevated PowerShell** for the steps that stop/run the service.
- *(Optional)* **`sqlite3.exe`** if you want to inspect the seeded DB directly.
  Install with `winget install SQLite.SQLite` if you don't have it. Not needed
  to seed, run, or tear down.

No other external tooling is required.

---

## Workflow

Run everything from the repo root (`cd C:\dev\zenvizor` first — elevated shells
open in `System32`).

### 1. Seed a throwaway store (non-elevated is fine)

Pick any throwaway directory that is **not** the real store:

```powershell
cd C:\dev\zenvizor
dotnet run --project tools/ZenVizor.SeedDemo -c Debug -- seed --data-dir C:\dev\zenvizor\zv-demo
```

Add `--force` to wipe and reseed an existing demo store at that path.

### 2. Point the service at it and capture

The dev service normally runs under the Service Control Manager as LocalSystem,
so environment variables set in your shell would **not** reach it. Instead, stop
that service and run the built service binary in the foreground from an elevated
shell that has the overrides set — the child process inherits them.

```powershell
# elevated PowerShell, at C:\dev\zenvizor
sc.exe stop ZenVizor                       # free the IPC pipe + real store

$env:ZENVIZOR_DATA_DIR = "C:\dev\zenvizor\zv-demo"
$env:ZENVIZOR_DISABLE_CAPTURE = "1"        # no live ETW capture over the seed

# make sure the Release binary exists (build it while the service is stopped):
dotnet build src/ZenVizor.Service/ZenVizor.Service.csproj -c Release

# run it in the foreground; it opens the seeded store and owns the pipe:
.\src\ZenVizor.Service\bin\Release\net10.0-windows\ZenVizor.Service.exe
```

Leave that window running. In a **separate, non-elevated** shell, launch the UI
the way you normally do (it needs no env vars — it only talks to the pipe):

```powershell
cd C:\dev\zenvizor
dotnet run --project src/ZenVizor.Ui -c Release
```

The DB-backed surfaces — Apps list, per-app drill-downs, Alerts feed, and the
Daily Report — now show the seeded dataset. Capture your screenshots.

> **Note on the Overview's live counters.** The top-of-Overview "right now"
> numbers come from the in-memory capture aggregator, which is intentionally
> disabled here (`ZENVIZOR_DISABLE_CAPTURE=1`), so they read idle/zero. Those
> are composited by hand for marketing; the seed drives every DB-backed view.

### 3. Tear down when finished

Stop the foreground service (Ctrl+C in its window). Then delete the throwaway
store. **Run teardown from an elevated shell** — while running, the service
re-ACLs its data directory to SYSTEM + Administrators only, so a non-elevated
delete is denied:

```powershell
# elevated PowerShell, at C:\dev\zenvizor
dotnet run --project tools/ZenVizor.SeedDemo -c Debug -- teardown --data-dir C:\dev\zenvizor\zv-demo
```

Restart the real service if you want it back: `sc.exe start ZenVizor`.

> If you only seeded and inspected the store (never ran the service against it),
> the directory is still owned by you and teardown works non-elevated.

---

## Command reference

```
seed     --data-dir <throwaway-dir> [--force]   Create + seed a demo store.
teardown --data-dir <throwaway-dir>             Delete the demo store.
help                                            Usage.
```

`--data-dir` is **required** and must not resolve to
`%ProgramData%\ZenVizor\`. `teardown` refuses to delete a directory that has no
`zenvizor.db`, so a mistyped path can't nuke an unrelated tree.

---

## Safety guards (why this can't hurt production)

- **No default `--data-dir`** — the tool can never fall back to the real store.
- **Prod-path refusal** — if `--data-dir` resolves to `%ProgramData%\ZenVizor\`,
  both `seed` and `teardown` abort before touching anything.
- **Teardown sanity gate** — refuses to delete a directory lacking `zenvizor.db`.
- **Release build blocked** — the `.csproj` has a `BeforeTargets="Build"` error
  that hard-fails any `-c Release`, so a stray tree-wide Release build can't
  smuggle this tool into a shippable artifact.
- **Not in the solution** — omitted from `ZenVizor.slnx`, so it never enters
  `dotnet build ZenVizor.slnx`, CI, or the installer.
