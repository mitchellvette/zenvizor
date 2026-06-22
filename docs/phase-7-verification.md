# Phase 7 verification — Burn bootstrapper bundle

**Status:** All four manual gates passed 2026-06-20 against bundle v0.1.1.
**Companion doc:** `docs/zenvizor-sprint-plan.md` Phase 7 (acceptance criteria + scope).
**Test environment:** VirtualBox 7.2.10 VM, Win11 Enterprise 25H2 eval ISO
(`assets/26200.6584.250915-1905.25h2_ge_release_svc_refresh_CLIENTENTERPRISEEVAL_OEMRET_x64FRE_en-us.iso`).
Per the project's "verification docs at phase level, not slice" rule, this is
the single Phase 7 verification doc — all four gate walkthroughs live here.

---

## Why VBox instead of Windows Sandbox

The brief specified Sandbox. On the dev machine (Win11 Home 10.0.26200),
Windows Sandbox is **not available**: `Containers-DisposableClientVM` isn't
in the optional-features catalog and `Enable-WindowsOptionalFeature` returns
"feature name is unknown." Sandbox is gated by a hidden OEM image flag that
varies per Home install — this machine didn't get the flag. Fallback chosen
was VirtualBox + Win11 Enterprise eval ISO (90-day eval, no Microsoft
account required). Functionally equivalent to Sandbox for these gates;
snapshots take the place of Sandbox's auto-reset.

If a future contributor has Sandbox available, the same gates should run
fine there — the `.wsb` file isn't authored because the dev box can't use
it. Pattern would be a single `.wsb` mapping `installer\Bundle\bin\x64\Release\`
read-only and leaving the user at the desktop to double-click manually.

---

## One-time VM setup (already done; re-do only if VM is destroyed)

### Tools

- **VirtualBox 7.2.10** (`winget install --id Oracle.VirtualBox -e
  --accept-source-agreements --accept-package-agreements`). 7.x is required
  for vTPM (Win11 prereq); 7.2+ specifically for the `modifynvram
  secureboot` sub-command shape we use.
- **Windows 11 Enterprise eval ISO** —
  `microsoft.com/en-us/evalcenter/download-windows-11-enterprise`. Local
  copy at `assets/26200....iso`.

### VM creation

The script below produces a Win11-compatible VM with vTPM, EFI, 4 GB RAM,
2 cores, 64 GB dynamic VDI, NAT networking, bidirectional clipboard, and
the install ISO attached.

```powershell
$vbox = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$iso  = "C:\dev\zenvizor\assets\26200.6584.250915-1905.25h2_ge_release_svc_refresh_CLIENTENTERPRISEEVAL_OEMRET_x64FRE_en-us.iso"
$vm   = "ZenVizor-Phase7"

& $vbox createvm --name $vm --ostype "Windows11_64" --register
& $vbox modifyvm $vm --cpus 2 --memory 4096 --vram 128 --firmware efi --graphicscontroller vmsvga --audio-driver none --nic1 nat --clipboard-mode bidirectional --draganddrop bidirectional
& $vbox modifyvm $vm --tpm-type 2.0

$vmDir = (& $vbox showvminfo $vm --machinereadable | Select-String '^CfgFile=' | ForEach-Object { ($_ -split '"')[1] }) | Split-Path -Parent
$disk  = Join-Path $vmDir "$vm.vdi"
& $vbox createmedium disk --filename $disk --size 65536 --format VDI

& $vbox storagectl $vm --name "SATA" --add sata --controller IntelAhci --portcount 1
& $vbox storagectl $vm --name "IDE"  --add ide  --controller PIIX4
& $vbox storageattach $vm --storagectl "SATA" --port 0 --device 0 --type hdd --medium $disk
& $vbox storageattach $vm --storagectl "IDE"  --port 1 --device 0 --type dvddrive --medium $iso

# EFI boot priority: DVD-only for first boot. NVRAM reset ensures the new order takes effect.
& $vbox modifynvram $vm inituefivarstore
& $vbox modifyvm $vm --boot1 dvd --boot2 none --boot3 none --boot4 none

& $vbox startvm $vm
```

### Gotchas discovered while building this VM

These are real traps that cost real time during the first walk-through.
They're documented here so the next person doesn't repeat them.

1. **Graphics controller MUST be `vmsvga`, not `vboxsvga`.** With
   `vboxsvga` on VBox 7.2.10, the EFI firmware posts cleanly (the
   `VBox.log` shows full DXE driver loading and HyperV-style guest OS
   reporting) but **the framebuffer never reaches the host VM window** —
   you get a literal empty black window. `vmsvga` renders correctly.
   The script above already uses `vmsvga`.

2. **Win11 EFI boot order needs explicit setup.** Without
   `modifyvm --boot1 dvd`, EFI tries to boot the empty .vdi first, fails
   silently, and you get a black window. After the OS is installed, the
   priority should be flipped back: `modifyvm --boot1 disk --boot2 dvd`
   (or just remove the DVD attachment).

3. **Secure-boot is optional for Win11 Enterprise eval install.** Enterprise
   eval is more permissive than retail; install succeeds without
   `modifynvram secureboot --enable`. If a future test needs it:
   `modifynvram <vm> inituefivarstore` then `modifynvram <vm>
   enrollmssignatures` then `modifynvram <vm> secureboot --enable`.

4. **Host display sleep kills running VMs.** A 3-minute idle blanked the
   host display, suspended the VM, and Windows OOBE froze with a 190-second
   `TM: Giving up catch-up` lag visible in `VBox.log`. Disable display +
   system sleep on the host while a VM is mid-install:

   ```powershell
   powercfg /change standby-timeout-ac 0
   powercfg /change monitor-timeout-ac 0
   ```

   Restore preferred values after the testing window closes.

5. **VBox 7.x Host Key default is Right Ctrl.** Many keyboards (laptops
   especially) only have a left Ctrl. Rebind via VirtualBox Manager →
   File → Preferences → Input → Virtual Machine → Host Key Combination,
   pick something always-present (Right Alt, Scroll Lock, F12).
   Without rebinding, you can't release input from the VM window.

6. **Hyper-V Platform on Home is a false lead for Sandbox.** Enabling
   `HypervisorPlatform` does not make Sandbox available on Win11 Home
   builds that lack the OEM Sandbox flag. After this confirmation, you
   can disable it (`Disable-WindowsOptionalFeature -Online -FeatureName
   "HypervisorPlatform" -NoRestart` + `bcdedit /set hypervisorlaunchtype
   off`), but you do not have to — VBox can coexist with WHPX-on if its
   graphics controller is correct (see gotcha #1).

### Windows install

Standard OOBE. For modern Win11 builds that force Microsoft account
sign-in:

- During the network step, if forced into "Add a Microsoft account":
  press **Shift+F10** to open a command prompt over OOBE and run
  `start ms-cxh:localonly`. This opens the local-account creation dialog
  directly. (`oobe\bypassnro` was closed in newer builds; `ms-cxh:localonly`
  still works.)
- Use a consistent local username (e.g. `tester`) so commands in this
  doc reference predictable paths.
- All privacy toggles → Off. Diagnostic data → Required (the minimum
  Enterprise allows).

### Guest Additions + shared folder

```powershell
# From host PS, after a clean Windows install:
& $vbox controlvm $vm acpipowerbutton    # let Windows shutdown cleanly
# Wait ~30s, then:

$bundleDir = "C:\dev\zenvizor\installer\Bundle\bin\x64\Release"
& $vbox sharedfolder add $vm --name "zenvizor-bundle" --hostpath $bundleDir --readonly --automount --auto-mount-point "Z:"

& $vbox startvm $vm
```

Inside the VM after boot: **Devices → Insert Guest Additions CD image…**
from the VM menu, then run `VBoxWindowsAdditions-amd64.exe` as admin,
reboot the guest. After reboot, `Z:\` automounts on every boot and
`ZenVizorSetup.exe` is visible in File Explorer.

### Snapshots

Two persistent reference points, both live (RAM included for instant
restore):

```powershell
# Snapshot 1: clean Win11 + GAdds + Z:\ share, no .NET 10. Gate 1 precondition.
& $vbox snapshot $vm take "Base" --description "Clean Win11 Enterprise eval + Guest Additions + Z share; no .NET 10. Gate 1 precondition."

# Inside VM: download + install the runtime
# (elevated PS — see runtime install block below)

# Snapshot 2: same as Base but with the .NET 10 runtime pre-installed. Gate 2 precondition.
& $vbox snapshot $vm take "WithDotNet10" --description "Clean Win11 + GAdds + Z share + .NET 10.0.8 Desktop Runtime. Gate 2 precondition."
```

Runtime install block (run inside VM, elevated PowerShell):

```powershell
$url  = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.8/windowsdesktop-runtime-10.0.8-win-x64.exe"
$file = "$env:USERPROFILE\Downloads\windowsdesktop-runtime-10.0.8-win-x64.exe"
Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing

$expected = "8dde7d1fe5d1934d386c01ac208e1a9debc1afa2448c5404e969c3bdee36b2dbaa9cff999452bd26181659f27f3eeffe200ac223c26a2196dc563bef0536ca1e"
$actual = (Get-FileHash -Algorithm SHA512 $file).Hash.ToLower()
if ($actual -ne $expected) { throw "SHA512 mismatch" }

Start-Process -FilePath $file -ArgumentList "/install","/quiet","/norestart" -Wait
$env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")
dotnet --list-runtimes
```

After the snapshots exist, every gate run starts with:

```powershell
& $vbox controlvm $vm poweroff
Start-Sleep -Seconds 2
& $vbox snapshot $vm restore "Base"      # or "WithDotNet10"
& $vbox startvm $vm
```

VM resumes from snapshot's RAM state (~2s) — no boot wait.

---

## Findings landed during Phase 7 testing

These are wxs/bundle changes that landed *because* the manual gates
exposed problems. All committed against bundle v0.1.1.

### Finding 1 — MsiPackage `Visible="no"` to hide duplicate ARP entry

**Discovered:** Gate 1, first run.

**Symptom:** Settings → Apps showed two `ZenVizor 0.1.0` rows after
install: one from the Burn bundle, one from the inner MSI registering
itself. End-user UX nightmare — no way to tell which to click for
uninstall, and clicking the wrong one orphans the other.

**Fix:** Set `Visible="no"` on the `<MsiPackage>` element in
`installer/Bundle/ZenVizor.Bundle.wxs`. Burn injects
`ARPSYSTEMCOMPONENT=1` into the MSI install command line, which
Windows interprets as "system component" and hides from the Settings
UI. The MSI's ARP key still exists in the registry (and shows in
unfiltered `Get-ItemProperty` queries), but the user-facing surface
shows the single bundle entry as intended.

### Finding 2 — REMOVE_DATA wxs sequencing bug

**Discovered:** Gate 3 Step 3, on the first `REMOVE_DATA=1` test.

**Symptom:** `msiexec /x {ProductCode} REMOVE_DATA=1` (and equivalently
the bundle's uninstall path before the passthrough was added) did NOT
wipe `%ProgramData%\ZenVizor\`. The MSI log showed
`WixRemoveFoldersEx: Error 0x80070057: Missing folder property:
REMOVE_DATA_FOLDER` followed by `Skipping action: SetREMOVE_DATA_FOLDER
(condition is false)` — `WixRemoveFoldersEx` ran *before*
`SetREMOVE_DATA_FOLDER`, so the property the custom action needed was
never set.

**Root cause:** The MSI wxs scheduled `SetREMOVE_DATA_FOLDER` with
`Before="CostFinalize"`. In WiX 6, `util:RemoveFolderEx` schedules its
custom action earlier in the InstallExecuteSequence than I assumed
(before `FileCost`, not before `InstallInitialize`). Result: the
sequence put SetProperty *after* WixRemoveFoldersEx in practice.

**Fix:** Changed to `After="LaunchConditions"` in
`installer/ZenVizor.wxs` — that's at sequence position ~100, guaranteed
to fire before any custom action. Referencing `WixRemoveFoldersEx`
directly via `Before="WixRemoveFoldersEx"` does not link in WiX 6 (the
CA symbol lives in the util.wixext fragment and isn't visible to the
linker from our wxs).

### Finding 3 — REMOVE_DATA bundle variable + MsiProperty passthrough

**Discovered:** same Gate 3 walk, after Finding 2 was fixed.

**Symptom:** With Finding 2 fixed, `msiexec /x ... REMOVE_DATA=1` worked
correctly. But `ZenVizorSetup.exe /uninstall REMOVE_DATA=1` (the
bundle path) still didn't wipe — Burn didn't know to forward the
property to the inner MSI.

**Fix:** Added a bundle variable + MsiProperty passthrough in
`installer/Bundle/ZenVizor.Bundle.wxs`:

```xml
<Variable Name="REMOVE_DATA" bal:Overridable="yes" Value="0" Type="numeric" />

<MsiPackage ...>
    <MsiProperty Name="REMOVE_DATA" Value="[REMOVE_DATA]" />
</MsiPackage>
```

Default value `0` means Settings UI uninstall (which invokes the bundle
without arguments) still preserves data — only an explicit
`/uninstall REMOVE_DATA=1` on the command line wipes.
`bal:Overridable="yes"` is required for the
`WixStandardBootstrapperApplication` to honor the command-line variable
override (without it Burn silently ignores the override).

### Finding 4 — Bundle branding (LogoFile + IconSourceFile)

**Discovered:** Gate 4 prep, while reviewing what the upgrade test would
expose visually.

**Symptom:** Bundle BA UI showed the default WiX CD-like logo; Settings
→ Apps showed the default "Installer" icon next to ZenVizor. Both
looked dated and generic.

**Fix:** Wired existing brand assets into the bundle:

```xml
<Bundle ... IconSourceFile="..\..\src\ZenVizor.Ui\Assets\favicon.ico">
  <BootstrapperApplication>
    <bal:WixStandardBootstrapperApplication ...
        LogoFile="..\..\assets\zv_logomark_v1.png" />
  </BootstrapperApplication>
</Bundle>
```

`LogoFile` slot in `rtfLicense` theme auto-scales any PNG to fit.
`IconSourceFile` requires `.ico` (favicon.ico already shipped with the
UI project, with 48×48 and 32×32 resolutions).

---

## Gate walkthroughs

Each gate starts from a clean snapshot restore. Always:

```powershell
$vbox = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$vm   = "ZenVizor-Phase7"
```

set in the host PS session.

### Gate 1 — clean sandbox, no .NET 10 pre-installed

**Tests:** the chained-install happy path — runtime is missing on the
guest, bundle detects that, installs runtime silently, then installs
MSI.

**Setup:**

```powershell
& $vbox controlvm $vm poweroff
Start-Sleep -Seconds 2
& $vbox snapshot $vm restore "Base"
& $vbox startvm $vm
```

**Precondition (inside VM):**

```powershell
dotnet --list-runtimes
```

Must say "not recognized." If it lists 10.0.8, the wrong snapshot was
restored — stop and re-check.

**Run:** Double-click `Z:\ZenVizorSetup.exe` → accept license → Install →
Success.

**Verifications (inside VM, elevated PS):**

```powershell
sc.exe query ZenVizor
```

Expected: `STATE : 4 RUNNING`.

```powershell
Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match "ZenVizor|Windows Desktop Runtime" -and $_.SystemComponent -ne 1 } | Select-Object DisplayName, DisplayVersion, Publisher | Format-Table -AutoSize
```

Expected: three rows — one `ZenVizor 0.1.1`, one `Microsoft Windows
Desktop Runtime - 10.0.8 (x64)`, one `Microsoft Windows Desktop
Runtime 10.0.8 (x64)`. (The runtime registers two ARP entries by
design; we filter `SystemComponent=1` to skip the hidden MSI entry
that `Visible="no"` produced — see Finding 1.)

```powershell
Test-Path "$env:ProgramData\ZenVizor\zenvizor.db"
icacls "$env:ProgramData\ZenVizor"
```

Expected: True; ACL shows only `NT AUTHORITY\SYSTEM` and
`BUILTIN\Administrators` full control.

```powershell
zvctl ping
```

Expected: pong.

Also confirm UI launches from Start menu and renders the dashboard
with some live traffic.

### Gate 2 — sandbox with .NET 10 pre-installed

**Tests:** the detect-existing skip path — runtime is already on the
guest, bundle should skip the runtime ExePackage and only install MSI.

**Setup:**

```powershell
& $vbox controlvm $vm poweroff
Start-Sleep -Seconds 2
& $vbox snapshot $vm restore "WithDotNet10"
& $vbox startvm $vm
```

**Precondition (inside VM):**

```powershell
dotnet --list-runtimes
```

Expected: `Microsoft.WindowsDesktop.App 10.0.8 [...]`.

**Run:** Double-click `Z:\ZenVizorSetup.exe` → accept → Install →
Success. Should feel faster than Gate 1 (no runtime install
underneath).

**Verifications (inside VM, elevated PS):**

```powershell
$burnLog = Get-ChildItem "$env:TEMP\ZenVizor_*.log" -Exclude "*elevated*","*ZenVizorMsi*" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
Select-String -Path $burnLog -Pattern "WindowsDesktopRuntime|DotNetCoreSearch|Detected package|Plan package" | Select-Object -First 30
```

Expected lines (paraphrased — exact wording from the log):
- `Setting version variable 'WindowsDesktopRuntimeVersion' to value '10.0.8'`
- `Condition 'WindowsDesktopRuntimeVersion >= v10.0.8' evaluates to true.`
- `Detected package: WindowsDesktopRuntime, state: Present`
- `Planned package: WindowsDesktopRuntime, ... execute: None`

The `execute: None` is the proof — Burn planned the runtime as
already-present and chose not to run the installer.

Standard sanity: `sc.exe query ZenVizor` → RUNNING, `zvctl ping` →
pong.

### Gate 3 — uninstall via Add/Remove Programs

**Tests:** two sub-paths: default uninstall (preserve data) and
explicit-wipe uninstall (REMOVE_DATA=1).

**Setup:** runs on whatever VM state has ZenVizor installed (typically
post-Gate 1 or post-Gate 2; no rollback needed).

**Sub-gate A — default uninstall via Settings UI:**

Inside the VM: Settings → Apps → Installed apps → ZenVizor → `...` →
Uninstall → confirm UAC. Burn UI shows rtfLicense panel with the
ZenVizor logomark; click **Uninstall** → "Complete."

Verify (elevated PS):

```powershell
sc.exe query ZenVizor
Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq "ZenVizor" } | Select-Object DisplayName, UninstallString
Test-Path "$env:ProgramFiles\ZenVizor"
Test-Path "$env:ProgramData\ZenVizor"
```

Expected:
- service does not exist (1060)
- registry query returns nothing (bundle ARP entry gone)
- `$env:ProgramFiles\ZenVizor` → False (binaries removed)
- `$env:ProgramData\ZenVizor` → **True** (data preserved by default)

**Sub-gate B — REMOVE_DATA=1 wipe via bundle:**

Reinstall the bundle (so there's something to wipe), then from elevated
PS:

```powershell
& "Z:\ZenVizorSetup.exe" /uninstall REMOVE_DATA=1 /quiet
```

Verify:

```powershell
Test-Path "$env:ProgramFiles\ZenVizor"
Test-Path "$env:ProgramData\ZenVizor"
Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq "ZenVizor" } | Select-Object DisplayName
```

Expected: all three False / empty.

Log confirmation (proves the chain actually fired):

```powershell
$log = Get-ChildItem "$env:TEMP\ZenVizor*ZenVizorMsi.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
Select-String -Path $log -Pattern "REMOVE_DATA|RemoveFolderEx|SetREMOVE_DATA_FOLDER" | Select-Object -First 20
```

Should show `Command Line: REMOVE_DATA=1 ARPSYSTEMCOMPONENT=1 ...`,
`Adding REMOVE_DATA property. Its value is '1'.`, `Doing action:
SetREMOVE_DATA_FOLDER`, `Adding REMOVE_DATA_FOLDER property. Its value
is 'C:\ProgramData\ZenVizor'.`, then enumeration of subdirs
(`_REMOVE_DATA_FOLDER_0`, `_REMOVE_DATA_FOLDER_1`) leading to the
folder deletion.

The .NET 10 runtime should remain in Settings → Apps after either
sub-gate (Permanent="yes" on the ExePackage).

### Gate 4 — bundle reinstall over prior version

**Tests:** locked bundle UpgradeCode + MSI MajorUpgrade correctly
replace one version with the next — single ARP entry, service swapped
to new binaries, data dir preserved.

**Setup (host):** build two bundle versions.

```powershell
# Build 1: save current v0.1.1 bundle aside
cp installer/Bundle/bin/x64/Release/ZenVizorSetup.exe installer/Bundle/bin/x64/Release/ZenVizorSetup-0.1.1.exe

# Bump version and rebuild for v0.1.2
# (Edit Directory.Build.props <Version>0.1.1</Version> → <Version>0.1.2</Version>)
dotnet build installer/Bundle/ZenVizor.Bundle.wixproj -c Release

cp installer/Bundle/bin/x64/Release/ZenVizorSetup.exe installer/Bundle/bin/x64/Release/ZenVizorSetup-0.1.2.exe
```

Both versioned EXEs now visible via `Z:\` inside the VM. (For the
original Phase 7 walk-through, we used 0.1.0 + 0.1.1; the pattern is
the same regardless of which two versions you pick. After the test,
delete the versioned copies and keep the canonical
`ZenVizorSetup.exe`.)

**Setup (VM):**

```powershell
& $vbox controlvm $vm poweroff
Start-Sleep -Seconds 2
& $vbox snapshot $vm restore "WithDotNet10"   # isolates upgrade from runtime install
& $vbox startvm $vm
```

**Run:**

1. Install older version: double-click `Z:\ZenVizorSetup-0.1.1.exe`,
   accept, Install.
2. Verify (elevated PS):

   ```powershell
   Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq "ZenVizor" } | Select-Object DisplayName, DisplayVersion, BundleProviderKey
   ```

   Expected: bundle row at the older version, with a `BundleProviderKey`
   GUID; possibly a second row for the inner MSI (no
   `BundleProviderKey`) — that's normal.

3. Install newer version: double-click `Z:\ZenVizorSetup-0.1.2.exe`.
   BA UI should still render the ZenVizor logomark. Accept, Install.
4. Verify upgrade:

   ```powershell
   Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq "ZenVizor" } | Select-Object DisplayName, DisplayVersion, BundleProviderKey
   sc.exe query ZenVizor
   Test-Path "$env:ProgramData\ZenVizor\zenvizor.db"
   (Get-Item "$env:ProgramData\ZenVizor\zenvizor.db").LastWriteTime
   zvctl ping
   ```

   Pass marks:
   - Bundle row shows the newer version, with a *different*
     `BundleProviderKey` (per-version Bundle ID, expected to change).
   - Old `BundleProviderKey` GUID is gone — proof the major upgrade
     replaced rather than co-installed.
   - Service RUNNING (binaries swapped transparently).
   - DB exists with a fresh `LastWriteTime` (service is writing post-
     upgrade; data was preserved).
   - zvctl pings.

**Visual:** during the newer-version install BA, the logomark should
match the v0.1.1+ branding. Settings → Apps → ZenVizor entry should
show the favicon icon (not the default Installer/CD icon).

---

## Deferred items

These were identified during Phase 7 work but not closed within Phase 7.
Track separately.

### Deferred 1 — REMOVE_DATA user-choice checkbox in BA UI

The current REMOVE_DATA opt-in is command-line only
(`ZenVizorSetup.exe /uninstall REMOVE_DATA=1`). End users uninstalling
via Settings UI have no in-UI way to choose "also delete my data"
during the BA flow.

To add: replace the stock `rtfLicense` theme with a custom Burn theme
XML containing a checkbox UI control bound to the `REMOVE_DATA`
variable, visible only on the uninstall pass. Stock WiX themes don't
support configurable checkboxes for arbitrary variables; a full custom
theme XML + localisation file is needed.

Estimated effort: half a day to a day of focused theme work + visual
iteration + Gate 1/3 re-validation against the new theme.

Trigger to do this work: any user feedback that the data-preserve
default is surprising, or before the 1.0 release if we want a polished
uninstall UX.

### Deferred 2 — Runtime payload cached even when not installed (minor)

Gate 2 confirmed that when the .NET 10 runtime is already present on
the host, Burn correctly plans the runtime package as `execute: None`
and does not run the installer. **However,** Burn still extracts the
runtime payload (~60 MB) into `%ProgramData%\Package Cache\<sha512>\`
because the ExePackage uses `Cache="keep"` (for repair scenarios).

Net effect: ~60 MB of disk used in the package cache even on machines
that already have the runtime and where it'll never be repaired by us
(MS owns runtime repair). Not a functional issue.

To improve: change `Cache="keep"` to `Cache="remove"` on the runtime
ExePackage in `installer/Bundle/ZenVizor.Bundle.wxs`. Trade-off: we
lose the cached payload for any future "Repair" flow on the runtime;
since the runtime is `Permanent="yes"` and shared with other apps,
we'd never invoke that flow anyway. Likely net positive but worth a
deliberate decision.

Trigger to do this work: any concern about installer footprint, or as
a polish item before MVP release.

### Deferred 3 — VM gotchas captured here, not yet folded into CLAUDE.md

If we expect to re-do Phase 7-style VM setup for Phase 8 testing (DNS
observer manual gates), the "gotchas" section above might warrant
promotion into a more durable place — e.g. a new
`docs/vm-test-setup.md` or a section in CLAUDE.md. Defer until Phase 8
actually needs another VM.
