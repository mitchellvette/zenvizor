# ZenVizor

A lightweight, **passive** Windows network monitor. It attributes outbound
and inbound traffic to the originating process and (for `svchost`) the
specific hosted service, stores history locally in SQLite, shows a
near-live dashboard, and produces daily reports. It is **not** a
firewall — there is no blocking, shaping, or active intervention of any
kind.

ZenVizor emits **zero network traffic of its own**: no telemetry, no
update checks, no DNS lookups, no "phone home." The tool pointed at
itself reports no outbound from its own processes; this is enforced as
a manual gate at every MVP phase boundary.

> **Status:** pre-release. Working toward the 1.0 MVP per
> [`docs/zenvizor-sprint-plan.md`](docs/zenvizor-sprint-plan.md). The
> installer below is functional but not yet through its full manual
> acceptance pass.

---

## Install (end users)

1. Download `ZenVizorSetup.exe` (the canonical install artifact —
   produced by every push to `main`, see CI artifacts).
2. Double-click and follow the prompts. The bundle:
   - Detects whether the .NET 10 Desktop Runtime is already installed.
   - Installs it (silently) if missing — the runtime payload is
     embedded for offline installability; no second download required.
   - Installs the ZenVizor MSI underneath, which registers the
     `ZenVizor` Windows Service, creates the data directory, and
     installs the UI + the `zvctl` CLI.
3. Launch ZenVizor from the Start menu, or run `zvctl ping` from any
   PowerShell session.

### What gets installed where

| Path                                   | Contents                              |
| -------------------------------------- | ------------------------------------- |
| `%ProgramFiles%\ZenVizor\Service\`     | Service binaries                      |
| `%ProgramFiles%\ZenVizor\Ui\`          | WPF dashboard                         |
| `%ProgramFiles%\ZenVizor\Cli\`         | `zvctl.exe` (added to system PATH)    |
| `%ProgramData%\ZenVizor\`              | SQLite DB + config (SYSTEM + Admins only) |
| Start menu shortcut to `ZenVizor.Ui.exe` | UI entry point                      |

The Windows Service runs as `LocalSystem`. The data directory ACL is
locked to SYSTEM + Administrators — standard users access report data
only through the named-pipe IPC the UI uses; they cannot read the
SQLite file directly. This is intentional; the rationale lives in
[`CLAUDE.md`](CLAUDE.md) under "Intentional design tension."

### System requirements

- Windows 10 or 11, x64.
- ~150 MB free disk for binaries; the SQLite DB grows over time
  per the configured retention policy (defaults documented in the
  [PRD](docs/zenvizor-prd.md) §7.9).
- Administrator privileges for the install (the service runs as
  LocalSystem). Day-to-day UI use is non-elevated.

---

## Uninstall

Add/Remove Programs (Settings → Apps → Installed apps → ZenVizor →
Uninstall) removes the service, the binaries, and the Start menu
entry. The `%ProgramData%\ZenVizor\` directory — i.e., the history
database — is **preserved by default**, so a reinstall picks up where
the previous install left off.

To wipe the data directory as part of uninstall, run from an elevated
PowerShell:

```powershell
msiexec /x "{put product code here}" REMOVE_DATA=1 /qn
```

(Or, for a development build, see `scripts\uninstall-dev.ps1
-PurgeData` below.)

The bundled .NET 10 Desktop Runtime is **left in place** on
uninstall — it is a shared component and may be used by other
applications.

---

## Build from source (developers)

Tooling required:

- .NET 10 SDK (pinned via `global.json`).
- WiX Toolset 6.0.1 (installed via `dotnet tool install --global wix
  --version 6.0.1`); the relevant wixext extensions install
  automatically as PackageReferences. See the licensing note below.
- For the bootstrapper bundle: an internet connection on first build
  (the build target downloads the .NET 10 Desktop Runtime EXE into
  `installer/Bundle/payloads/` and verifies its SHA512 against the
  pin in `Directory.Build.props`; subsequent builds use the cached
  payload).

Build + test:

```powershell
cd C:\dev\zenvizor
dotnet build ZenVizor.slnx -c Release
dotnet test ZenVizor.slnx -c Release
```

Build the installer artifacts:

```powershell
cd C:\dev\zenvizor
dotnet build installer/ZenVizor.wixproj -c Release            # MSI only
dotnet build installer/Bundle/ZenVizor.Bundle.wixproj -c Release   # MSI + Burn bundle
```

Outputs land in `installer\bin\x64\Release\ZenVizor.msi` and
`installer\Bundle\bin\x64\Release\ZenVizorSetup.exe`.

### Dev install (skip the MSI, register the Release build directly)

For iterative development, the service is typically registered
directly from the build output rather than via the MSI. Run from an
**elevated** PowerShell:

```powershell
cd C:\dev\zenvizor
.\scripts\install-dev.ps1
```

To remove the dev install and leave history intact:

```powershell
cd C:\dev\zenvizor
.\scripts\uninstall-dev.ps1
```

To remove the dev install AND wipe the data directory:

```powershell
cd C:\dev\zenvizor
.\scripts\uninstall-dev.ps1 -PurgeData
```

> The `cd` is load-bearing — elevated PowerShell sessions default to
> `C:\Windows\System32`, and the dev scripts resolve repo-relative
> paths from the current working directory.

---

## Documentation

- [`docs/zenvizor-prd.md`](docs/zenvizor-prd.md) — full product spec.
- [`docs/zenvizor-sprint-plan.md`](docs/zenvizor-sprint-plan.md) —
  phased build plan with CI + manual acceptance criteria.
- [`docs/threat-model-limits.md`](docs/threat-model-limits.md) — what
  ZenVizor can and cannot detect, and why.
- [`docs/alerts-catalog.md`](docs/alerts-catalog.md) — the alert
  types ZenVizor raises and when.
- [`docs/design-system.md`](docs/design-system.md) — UI design tokens
  and visual language.
- [`CLAUDE.md`](CLAUDE.md) — project invariants and conventions.

---

## Licensing

ZenVizor is licensed under the **GNU General Public License v3.0 or
later** (GPL-3.0-or-later). The full license text is in
[`LICENSE`](LICENSE). The license includes an additional permission
under GPL-3.0 Section 7 that allows ZenVizor to be combined and
distributed with the WiX Toolset installer runtime; see the clause at
the end of `LICENSE` for details.

**ZenVizor™** and the ZenVizor logo are common-law trademarks of
Mitchell Gray and are **not** licensed under the GPL. The trademark
policy is in [`TRADEMARK.md`](TRADEMARK.md). Forks of the source code
are welcome under the GPL; they must be redistributed under a
different name.

Third-party components incorporated into ZenVizor and into the
installer bundle are listed in [`NOTICES.md`](NOTICES.md), each under
its own license.

### WiX Toolset (build-time)

ZenVizor's build depends on the **WiX Toolset 6.0.1** for installer
packaging. The WiX 6.0.x binary packages ship under the **Open Source
Maintenance Fee Agreement (OSMF)**, which applies only to
**revenue-generating use** of the WiX software. ZenVizor's current
non-revenue posture is exempt from the OSMF; the donateware path
introduces a question that must be resolved before the donateware
launch. See the full findings in
[`docs/licensing-wix-osmf.md`](docs/licensing-wix-osmf.md) — that doc
covers ZenVizor's relationship to WiX as a toolchain consumer, which
is orthogonal to ZenVizor's own outbound GPL-3.0-or-later license.
