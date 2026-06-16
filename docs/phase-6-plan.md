# Phase 6 — implementation plan (post-6.1a)

Detailed plan for the remainder of Phase 6 after Phase 6.1 (alert
pipeline) and Phase 6.1a (capture connect events + push dispatch +
service-reconnect lifecycle) shipped. Each sub-phase below includes
scope, implementation steps, acceptance criteria, and known findings
from prior phases that will affect the work.

This document is the working plan; the canonical sprint plan in
`docs/zenvizor-sprint-plan.md` lists higher-level acceptance gates and
the MVP-done definition.

---

## Status snapshot

| Item | Status | Commit |
|---|---|---|
| 6.1 — Alert pipeline + UnsignedFromUserPath producer | Done | `7be2588` |
| 6.1a — Capture connect events, push dispatch, reconnect | Done | `7502ed3` |
| 6.2 — Settings page | Not started | — |
| 6.3 — Tray polish | Not started | TRAY-01 noted in `docs/known-bugs.md` |
| 6.4 — Reports → Alerts deep-link | Not started | Reports renders inert chips |
| 6.5 — HC theme sweep | Not started | — |
| 6.6 — `zvctl alerts` subcommands | Not started | — |
| 6.7 — WiX installer | Not started | `installer/` directory does not exist |
| 6.8 — Self-monitoring zero-own-traffic gate | Pending all above | — |

Pre-v1 architectural follow-ups (orthogonal; documented in sprint plan):
- A1 — `ServiceReconnected` extended to History/Reports/PerApp/AppDetail
- A2 — Centralize query clients at app scope

---

## 6.2 — Settings page

**Goal.** Replace the placeholder Settings nav page with a working
surface that lets the user configure the runtime knobs the brief calls
out: autostart, retention windows, purge history, flush/bucket
intervals, toast toggle, theme.

### Scope

Settings rendered as grouped controls in a single scrollable page:

1. **Autostart** — radio group or ComboBox bound to a `ServiceStartMode`:
   - Automatic (default) — service starts at boot.
   - Manual — user-started only.
   - Off (fast-boot path) — service disabled at boot.
2. **Retention windows** — sliders or numeric inputs:
   - `traffic_samples_days` (default 30)
   - `traffic_hourly_days` (default 90)
   - `traffic_daily_days` (default 365)
   - `connections_days` (default 30)
   - `alerts_days_after_ack` (default 90)
3. **Purge history NOW** — explicit button that runs
   `RetentionRepository.PurgeOlderThan(now)` on demand. Confirmation
   dialog before firing.
4. **Flush + bucket intervals** — currently hard-coded
   (`FlushInterval = 5000ms`, `bucketSeconds = 5` per
   `BucketAligner.DefaultBucketSeconds`). Need to choose: expose as
   settings (requires service restart to take effect, or live re-apply)
   or keep hidden until users actually ask. Recommendation: ship with
   hard-coded defaults visible as read-only diagnostic, defer
   user-editable to post-v1 unless brief insists.
5. **Toast notifications toggle** — bool. When on, AlertRaised pushes
   produce a Windows toast in addition to the in-app feed. UI
   subscribes to `_alertsClient.AlertRaised` in MainWindow and
   conditionally calls `ToastNotificationManager.CreateToastNotifier()`
   (or whatever Wpf.Ui exposes — check H.NotifyIcon).
6. **Theme** — Light / Dark / System (default System). Already plumbed
   in `App.xaml.cs` via `ApplicationThemeManager.ApplySystemTheme`;
   Settings just needs to override the user choice and persist.

### Implementation steps

1. **Settings storage layer.** The `settings` table is already in
   `001_initial.sql:156-159` (`key TEXT PRIMARY KEY, value TEXT`).
   Build a `SettingsRepository` in `ZenVizor.Storage/Repositories/`
   that wraps GET / UPSERT for typed values:
   ```csharp
   public string? GetString(string key);
   public int    GetInt(string key, int defaultValue);
   public bool   GetBool(string key, bool defaultValue);
   public void   Set(string key, string value);
   ```
   Each retention setting becomes one row. Existing
   `RetentionRepository.LoadPolicy` already reads from a single
   `settings` row of JSON; align or replace as part of this work.
2. **IPC surface.** New methods on `IZenVizorIpc`:
   ```csharp
   Task<SettingsSnapshot> GetSettingsAsync();
   Task UpdateSettingsAsync(SettingsUpdate update);
   Task TriggerRetentionPurgeAsync();
   ```
   `SettingsSnapshot` and `SettingsUpdate` are new DTOs in
   `ZenVizor.Ipc.Contracts.Dto/`. Bump `IpcSchemaVersion` with a new
   `Settings = 1`.
3. **Autostart via Windows SCM.** Service start-mode toggle requires
   running `sc.exe config ZenVizor start= demand|auto|disabled` or
   calling `ChangeServiceConfig` via P/Invoke. The UI cannot do this
   directly (non-elevated); it issues an IPC call and the service
   (running as SYSTEM) makes the SCM call. The service can reconfigure
   its OWN start mode without elevation issues. Add a
   `ChangeStartModeAsync(ServiceStartMode mode)` IPC method.
4. **Settings page UI.** Replace `Views/SettingsPage.xaml` with the
   grouped controls. Use Wpf.Ui's `ToggleSwitch`, `Slider`,
   `NumberBox`, `ComboBox` per control. Match the metal card
   surface treatment per project memory
   (`project_canonical_card_treatment`).
5. **Settings VM.** New `SettingsViewModel` with INPC fields per
   knob; load from `GetSettingsAsync` on page Loaded; save on
   debounce (e.g., 500ms after last change) or on explicit Apply.
   Recommendation: Apply button — settings changes are infrequent and
   immediate persistence on every slider tick is a worse UX than a
   clear "you changed something — apply?" affordance.
6. **Theme application.** Existing
   `ApplicationThemeManager.ApplySystemTheme` is wired in
   `App.xaml.cs`. Need a `ApplicationThemeManager.Apply(theme)`
   call when the user picks Light or Dark; persist via the new
   settings layer and re-apply on next launch.
7. **Toast wiring.** When the toggle is on, MainWindow.OnAlertRaised
   (which already has the alert in hand) ALSO fires a toast via
   `Wpf.Ui.Tray.NotificationService` or the H.NotifyIcon API
   surface. Toast carries alert title + severity glyph; clicking it
   opens the UI window and navigates to Alerts (or directly to the
   alert via the same nav-param shape 6.4 uses).

### Known findings affecting implementation

- **Settings table is already created.** No migration needed
  (`001_initial.sql:156-159`).
- **RetentionRepository already uses a settings-table-backed policy.**
  See `RetentionRepository.LoadPolicy`. New code should not duplicate
  this — extend the existing pattern or refactor to share.
- **`ApplicationThemeManager.ApplySystemTheme(updateAccent: false)`**
  is the right call shape for theme switches; the `updateAccent`
  flag is critical or BrandAccent gets clobbered (see
  `App.xaml.cs:33-39`).
- **`ServiceStatusPoller` already exists.** The Settings page does
  NOT need its own service-status detection; it can read from
  MainWindow's existing infrastructure or just trust IPC calls
  to fail loudly when service is down.
- **The Wpf.Ui `ToggleSwitch` template doesn't honor
  `HorizontalContentAlignment` reliably** — same family of issue as
  the Phase 5 Type filter button. Test in HC mode.

### Acceptance — CI

- [ ] `SettingsRepository` round-trips: get/set string, int, bool;
      defaults on missing key.
- [ ] `UpdateSettingsAsync` IPC test: invalid values rejected
      (negative retention days, invalid start mode).
- [ ] `TriggerRetentionPurgeAsync` IPC test confirms `PurgeOlderThan`
      is called.

### Acceptance — manual

- [ ] Toggle each setting, click Apply, restart UI: setting persists.
- [ ] Change theme to Dark: applies immediately, persists across UI
      restart.
- [ ] Change autostart to Off, reboot: service does not start.
      Change back to Automatic: service starts on next boot.
- [ ] Click Purge history NOW: confirmation prompt; on Yes, history
      shrinks per the retention policy.

---

## 6.3 — Tray polish

**Goal.** Finalize close-to-tray + Exit semantics; resolve TRAY-01
(menu lingers after Exit click); harden single-instance enforcement.

### Scope

1. **TRAY-01 fix.** Documented in `docs/known-bugs.md`. Symptom: tray
   context menu popup stays visible for ~3 seconds after Exit. Root
   cause was not nailed in prior investigation — needs another pass.
2. **Single-instance enforcement.** Launching the UI when an instance
   is already running should restore the existing window, not start
   a second process. Use a named `Mutex` in `App.OnStartup` and a
   secondary signaling mechanism (named pipe / message-only window)
   to ask the existing instance to show itself.
3. **Notification balloon on first close-to-tray.** When the user
   closes the window for the first time, show a brief tooltip /
   balloon saying "ZenVizor is still running in the tray." Persist
   "we already showed this" in settings.
4. **Right-click menu copy** — ensure "Open ZenVizor" /
   "Hide to tray" / "Exit" wording matches the brief's voice
   (memory: `feedback_no_emdash_in_ui_copy`).
5. **Auto-show on launch** vs **silent-launch on autostart** — when
   the service is set to Automatic and the UI launches at boot, the
   UI should silently go to tray rather than popping a window the
   user didn't ask for. Add a `start_minimized` setting (default off
   for manual launch, on for autostart) controlling this.

### Implementation steps

1. **Re-investigate TRAY-01.** Per the known-bugs entry, the
   investigation stalled at "WPF tray menu Popup dismiss is on a
   separate dispatcher tick after window Close." Worth checking
   whether
   `H.NotifyIcon.Wpf` exposes a way to dispose the context menu
   before triggering `Close()`. Try ordering: `ContextMenu.IsOpen = false`
   → small `await Task.Yield()` → `Close()`.
2. **Single instance.** In `App.OnStartup`:
   ```csharp
   _mutex = new Mutex(initiallyOwned: true,
                      "Global\\ZenVizor.SingleInstance",
                      out bool createdNew);
   if (!createdNew)
   {
       SignalExistingInstance();
       Shutdown(0);
       return;
   }
   ```
   `SignalExistingInstance` sends a message via either a named
   pipe (reusing the existing ZenVizor pipe surface is tempting but
   wrong — that's the service pipe; use a separate UI-to-UI pipe
   like `ZenVizor.Ui.SingleInstance.v1`) or a Win32 broadcast.
3. **Balloon tooltip.** H.NotifyIcon supports
   `TaskbarIcon.ShowBalloonTip(title, msg, icon)`. Wire from the
   close-to-tray path with a one-shot "shown" setting check.
4. **`start_minimized` plumbing.** Settings entry; check in
   MainWindow.OnLoaded — if true, `Hide()` before `Show()` would
   otherwise paint.

### Known findings affecting implementation

- **`MainWindow.OnClosing` already cancels close and hides** unless
  `_exiting` is true. The Exit path sets `_exiting = true` in
  `OnTrayExitClicked` (line 333) before `Close()`.
- **`H.NotifyIcon.Wpf` auto-disposes the tray icon at process exit**
  (`TaskbarIcon.DisposeAfterExit`). `OnClosed` deliberately does NOT
  call `Tray.Dispose()` because the context menu uses a hidden
  message window for activation tracking and destroying it early
  strands the popup (this is TRAY-01's adjacent surface).
- **`SystemThemeWatcher.UnWatch` must run in `OnClosing` (with
  live HWND), not `OnClosed`** — pattern documented at
  `MainWindow.xaml.cs:286-300`.

### Acceptance — manual

- [ ] Click X on the window: window hides to tray; process keeps
      running. Tray icon visible.
- [ ] Right-click tray → Exit: window closes, process exits within
      ~1s, **tray menu popup disappears with the window** (TRAY-01
      gate).
- [ ] Launch a second UI process: existing window restores; second
      process exits immediately. No second tray icon.
- [ ] Reboot with autostart=Automatic: service starts; UI does NOT
      pop a window (`start_minimized=true`); UI is reachable via
      tray icon.

---

## 6.4 — Reports → Alerts deep-link

**Goal.** Wire the inert `Alerts · #N` chips on Reports' Notable cards
to navigate to the Alerts page filtered to / scrolled to the matching
alert.

### Scope

- Reports already renders the `Alerts · #N` chip on each Notable
  incident card. Currently click does nothing (inert per
  `docs/zenvizor-sprint-plan.md:344-352`).
- Click should navigate to AlertsPage with a parameter identifying
  the alert. AlertsPage scrolls the list to the matching row and
  highlights it briefly.
- If the alert has been dismissed and is currently filtered out
  (default state is Active), AlertsPage should switch to State=All
  so the row is visible.

### Implementation steps

1. **Contract addition.** `DailyReportNotable` DTO needs an `AlertId`
   field. The Phase 5b daily-report query joins to `alerts` to find
   `(type, entity_kind, entity_ref)` matches; we now have a real
   `alerts.alert_id` to project alongside. Add `long? AlertId` to
   the DTO (additive, schema bump
   `IpcSchemaVersion.DailyReport`).
2. **Server-side query update.** `DailyReportRepository.LoadNotable`
   joins to alerts and projects the new `alert_id`. The existing
   query shape should already join on
   `(type, entity_kind, entity_ref)`; just add `a.alert_id` to the
   SELECT and the DTO ctor.
3. **Reports click handler.** Find the chip in
   `Views/ReportsPage.xaml` (it's named per the design brief). Add
   a Click handler in `ReportsPage.xaml.cs` that pulls `AlertId`
   from the DataContext and navigates:
   ```csharp
   var nav = FindNavigationView(this);
   if (nav is null) return;
   nav.Navigate(typeof(AlertsPage), new AlertsNavParams(alertId));
   ```
   Reuses `FindNavigationView` pattern from
   `ReportsPage.xaml.cs:817-827`.
4. **`AlertsNavParams` DTO.** New record:
   `public sealed record AlertsNavParams(long AlertId);`. Lives in
   `ZenVizor.Ui/Views/`. NOT in IPC contracts — purely UI-internal.
5. **AlertsPage receives the param.** In
   `AlertsPage.OnPageLoaded`, check `DataContext` for
   `AlertsNavParams`:
   ```csharp
   if (DataContext is AlertsNavParams p)
   {
       _vm.SelectedState = AlertState.All;  // ensure row is visible
       await RefreshAsync();
       ScrollAndHighlight(p.AlertId);
       DataContext = _vm;  // restore VM DataContext
   }
   ```
6. **Scroll + highlight.** After the feed populates, find the
   `ListViewItem` for the matching `AlertId` via
   `AlertsList.ItemContainerGenerator.ContainerFromItem`. Scroll
   into view via `ListView.ScrollIntoView`. Apply a transient
   highlight (motion token `motion.duration.arrival` = 600ms, ease
   `motion.ease.glide`) on the matching card's border.

### Known findings affecting implementation

- **`AlertsPage.DataContext` is set to `_vm` in the constructor.**
  Setting it to `AlertsNavParams` for the nav-param-receive path is
  a transient swap; restore to `_vm` immediately after consuming
  the param. The existing pattern in `AppDetailPage.OnAppIdReceived`
  (`AppDetailPage.xaml.cs:240-273`) shows the same idiom.
- **`AlertsViewModel.SelectedState` setter triggers a
  RefreshAsync** via the page's `OnVmPropertyChanged` handler
  (Phase 4b wiring). When this nav-param path sets State=All, the
  subsequent explicit `RefreshAsync()` call is redundant —
  consider skipping one or the other.
- **Reports' chip currently has no click — adding one shouldn't
  conflict with existing template** but verify against the chip's
  visual state triggers (selected / hover / pressed).
- **Schema bump.** `IpcSchemaVersion.DailyReport` is currently a
  constant in `ZenVizor.Ipc.Contracts/IpcSchemaVersion.cs`; check
  whether prior schema bumps incremented or just kept the value
  stable (additive changes are non-breaking; explicit bump is
  optional but cleaner for tracking).

### Acceptance — CI

- [ ] `DailyReportRepository` test: notable item joins to its alert
      and projects `AlertId`.
- [ ] `ReportsPage` chip Click handler nav unit test (in-process).

### Acceptance — manual

- [ ] Trigger an unsigned-from-user-folder alert (use the 6.1a test
      binary). Wait for it to appear in tomorrow's daily report.
- [ ] On Reports page, the matching Notable card shows the
      `Alerts · #N` chip. Click it. Navigation: → Alerts page,
      State=All, list scrolled to the row, row briefly highlighted.

---

## 6.5 — HC theme sweep + visual audit

**Goal.** Verify Alerts + Settings (the new Phase 6 surface) render
correctly in High Contrast Dark and High Contrast Light, with every
brush resolving via `DynamicResource` so the theme swap works.

### Scope

Walk both pages in:

- HC Dark (Windows Settings → Accessibility → Contrast themes → Dark)
- HC Light (Aquatic / Desert / Dusk variants — test at least one)

Verify each token-driven surface element. Watch points (in order
of fragility):

1. **Severity brushes** — `status.critical`, `status.caution`,
   `status.neutral` plus their `.background` and `.text` variants.
   Used by severity bar / tile / badge / dot in alert cards; banner
   chrome; KPI strip eyebrows. HC overrides live in
   `Resources/HighContrast.xaml`.
2. **Card surface** — `metal.card` recipe (background + border +
   shadow). Cards should remain readable with the HC palette.
3. **Dismissed-row treatment** — `text.tertiary` for the
   desaturated severity bar; `surface.card.alt` + opacity 0.72 on
   the card; meta-row `text.tertiary` separators.
4. **Settings controls** — `ToggleSwitch`, `Slider`, `NumberBox`,
   `ComboBox` thumb / track / fill. Wpf.Ui handles HC for its own
   controls but verify against the brand palette.
5. **ContextMenu** — Type filter dropdown chrome. `brand.menu.item`
   ItemContainerStyle pulls token brushes.
6. **Tooltip** — Reset button tooltip, View-app tooltip, Type
   filter tooltip. Wpf.Ui default tooltip styling.
7. **Status banner** — Disconnected (amber) and Error (amber) per
   Phase 6.1a fix; ensure foreground stays AA-clear against the
   HC background.
8. **Status indicator dot** (bottom bar) — `status.connected` and
   `status.disconnected` brushes.

### Implementation steps

1. **Run UI in HC Dark.** Inspect each surface. Fix any
   `DynamicResource` lookup miss — typically a `StaticResource` that
   should be Dynamic, or a literal color baked in.
2. **Run UI in HC Light.** Same.
3. **Verify dismissed cards are still distinguishable** from
   active in HC. The opacity 0.72 demotion might wash out in HC
   palettes; may need to swap to a contrast-friendly demotion
   (e.g., italicized title, prefix tag).
4. **Update `Resources/HighContrast.xaml`** for any token that
   needs an HC-specific override.
5. **Document any unresolvable visual** in `docs/known-bugs.md`
   rather than shipping a broken-looking surface.

### Acceptance — manual

- [ ] Alerts page reads cleanly in HC Dark and HC Light.
- [ ] Settings page reads cleanly in HC Dark and HC Light.
- [ ] All severity tints distinguishable from neutral chrome.
- [ ] Status banner readable (foreground vs background AA).
- [ ] No control collapses to invisible (transparent on transparent).

---

## 6.6 — `zvctl alerts` subcommands

**Goal.** CLI parity for the alerts surface so QA and scripted
automation can drive the same IPC the UI uses.

### Scope

Four new subcommands under `zvctl alerts`:

1. `zvctl alerts list [--state active|dismissed|all] [--severity critical,warning,info] [--type <name>] [--limit N]`
   — wraps `GetAlertsAsync`. Default `--state active --limit 50`.
   Renders a table or JSON via `--json`.
2. `zvctl alerts dismiss <alertId>` — wraps `DismissAlertAsync`.
   Prints confirmation on success; non-zero exit code on failure.
3. `zvctl alerts watch` — long-running. Subscribes to AlertRaised
   pushes (same `IAlertNotifications` target the UI uses) and prints
   each alert as it arrives. Ctrl-C to exit.
4. `zvctl alerts clear-history` — QA aid. Deletes ALL rows from the
   `alerts` table. NEW: surfaced during 6.1a manual validation —
   without it, repeat-binary testing requires manual SQL surgery
   (we shipped a one-liner for this in the 6.1a validation cycle).

### Implementation steps

1. **CLI command registration.** Add a parent `alerts` command to
   `Cli/Program.cs` following the existing pattern
   (`statusCommand`, `statsCommand`, `snapshotCommand`). Each
   subcommand is a child command with its own handler.
2. **`list` handler.** Reuse `AlertsClient` from
   `ZenVizor.Ui/Services/AlertsClient.cs`? No — that's a UI assembly
   reference the CLI shouldn't take. Either:
   - Move `AlertsClient` to a shared assembly (e.g., add a
     `ZenVizor.Ipc.Client.Alerts` namespace in
     `ZenVizor.Ipc.Client`), OR
   - Have the CLI use `ZenVizorPipeClient.ConnectAsync` directly
     and call `Proxy.GetAlertsAsync(filter)` inline. The CLI
     pattern in `RunStatsAsync` already does this — copy the
     shape.
3. **`dismiss` handler.** Direct proxy call:
   ```csharp
   await client.Proxy.DismissAlertAsync(alertId);
   ```
4. **`watch` handler.** Connect with a notification target;
   subscribe to AlertRaised; await Console.CancelKeyPress; on Ctrl-C
   gracefully dispose the client. Notification target wiring uses
   the **generic `AddLocalRpcTarget<IAlertNotifications>`** per
   Phase 6.1a fix and the project memory
   `project_streamjsonrpc_explicit_interface_dispatch.md`.
5. **`clear-history` handler.** New IPC method
   `ClearAlertHistoryAsync()` on `IZenVizorIpc`. Server side: a
   gated method that DELETEs all rows from `alerts` and returns
   the count. Guard the entry behind a "are you sure" prompt on
   the CLI side (skipped if `--yes` flag is present, for scripted
   QA).
6. **Output formatting.** Tables: simple aligned columns, no
   third-party dep. JSON: `System.Text.Json`
   `JsonSerializer.Serialize` with `WriteIndented = true` for
   `--json` mode.

### Known findings affecting implementation

- **CLI cannot reference `ZenVizor.Ui`** — that's a WPF-host project
  with WindowsAppSDK overhead. The `AlertsClient` design intended
  to be a UI-only convenience wrapper; CLI should call
  `Proxy.GetAlertsAsync(filter)` directly through
  `ZenVizorPipeClient`.
- **`ZenVizorPipeClient.ConnectAsync(notificationTarget: this)`**
  already accepts a notification target; CLI watch path uses
  this. After 6.1a fix, the generic
  `AddLocalRpcTarget<IAlertNotifications>` correctly dispatches.
- **`ClearAlertHistoryAsync`** is a destructive action; per
  CLAUDE.md "always confirm risky actions" — keep the CLI confirm
  prompt; the IPC method itself doesn't need extra gating beyond
  the existing pipe ACL.
- **CLI binary location** — `zvctl.exe` lives in
  `src/ZenVizor.Cli/bin/<Configuration>/net10.0-windows/`. Manual
  testing aliases the path; consider exposing a shorter shim or
  publishing to a known location as part of the installer.

### Acceptance — CI

- [ ] `zvctl alerts list` against an empty service: prints "No
      alerts" or empty list, exit 0.
- [ ] `zvctl alerts list --json`: valid JSON output.
- [ ] `zvctl alerts dismiss <invalid-id>`: non-zero exit, error
      message.

### Acceptance — manual

- [ ] `zvctl alerts list` after triggering an unsigned binary
      shows the row.
- [ ] `zvctl alerts watch` in one terminal; trigger binary in
      another: alert prints in real time.
- [ ] `zvctl alerts dismiss <id>` flips the row in the DB.
- [ ] `zvctl alerts clear-history --yes` empties the table.

---

## 6.7 — WiX installer

**Goal.** Ship a `.msi` that installs both the service and the UI,
registers the service with the configured start mode, sets the
data-directory ACL, and uninstalls cleanly. CLI-drivable via
`wix build` so CI can produce the artifact.

### Scope

Single `.msi` producing:

- `%ProgramFiles%\ZenVizor\Service\ZenVizor.Service.exe` + deps
- `%ProgramFiles%\ZenVizor\Ui\ZenVizor.Ui.exe` + deps
- `%ProgramFiles%\ZenVizor\Cli\zvctl.exe` + deps
- Service registration via `ServiceInstall` element (auto-start
  configurable at install time via property; default Automatic).
- `%ProgramData%\ZenVizor\` directory with SYSTEM + Administrators
  ACL applied via custom action (mirrors
  `ProgramDataAcl.EnsureDirectoryWithAcl` runtime behaviour).
- Start Menu shortcut → UI exe.
- Tray autostart via `Run` registry value or Task Scheduler.
- Uninstall: stops service, removes service registration,
  removes Program Files, optionally purges `%ProgramData%\ZenVizor\`
  via a checkbox at uninstall time (default: keep, so re-install
  doesn't lose history).

### Implementation steps

1. **Tool dependency: install WiX 5.** `winget install -e --id WiXToolset.WiX`
   or `dotnet tool install --global wix`. Memory:
   `feedback_tool_dependencies` — surface this before validation
   steps.
2. **Create `installer/` directory.** Doesn't exist yet (CLAUDE.md
   already notes "NOT YET CREATED — Phase 6"). Inside:
   - `ZenVizor.Installer.wixproj` — MSBuild project.
   - `Product.wxs` — product definition.
   - `Service.wxs` — service component (ServiceInstall,
     ServiceControl).
   - `Ui.wxs` — UI exe + shortcut components.
   - `Cli.wxs` — `zvctl.exe` component.
   - `DataDir.wxs` — `%ProgramData%\ZenVizor\` directory + ACL
     custom action.
3. **Wire to solution.** Add `installer/ZenVizor.Installer.wixproj`
   to `ZenVizor.slnx`. CI will build it via `dotnet build` or
   `wix build`.
4. **Service registration.**
   ```xml
   <ServiceInstall
     Id="ZenVizorService"
     Name="ZenVizor"
     DisplayName="ZenVizor Network Monitor"
     Description="Passive network monitoring for personal accountability."
     Type="ownProcess"
     Start="auto"
     ErrorControl="normal"
     Account="LocalSystem"
     Vital="yes"/>
   <ServiceControl
     Id="ZenVizorServiceControl"
     Name="ZenVizor"
     Start="install"
     Stop="both"
     Remove="uninstall"
     Wait="yes"/>
   ```
5. **ACL custom action.** Custom action `EnsureDataDirAcl` runs
   the equivalent of `ProgramDataAcl.EnsureDirectoryWithAcl`
   server-side (deferred-immediate, runs as SYSTEM). Easiest: a
   small "post-install setup" exe (`ZenVizor.PostInstall.exe`)
   that calls into the existing `ProgramDataAcl` static. WiX runs
   it deferred.
6. **Optional UI tray autostart.** A `Run` registry value
   under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` is
   the standard pattern. Conditional on the user's install-time
   choice.
7. **CI integration.** GitHub Actions workflow:
   ```yaml
   - name: Install WiX
     run: dotnet tool install --global wix
   - name: Build installer
     run: dotnet build installer/ZenVizor.Installer.wixproj -c Release
   - name: Upload MSI
     uses: actions/upload-artifact@v4
     with:
       name: ZenVizor.msi
       path: installer/bin/Release/ZenVizor.msi
   ```

### Known findings affecting implementation

- **CLAUDE.md `gitStatus` notes** `installer/` is "NOT YET CREATED
  — Phase 6 (WiX project lands then)" — this is the phase.
- **`ProgramDataAcl.EnsureDirectoryWithAcl`** already exists in
  `ZenVizor.Service`. The installer's custom action should call
  the SAME logic — either via a shim exe or by linking the same
  static class. Don't duplicate the ACL logic.
- **Service display name + description** show in Services.msc;
  pick copy that aligns with the brand voice. No em-dash
  (memory rule).
- **Service account** is LocalSystem per CLAUDE.md invariant 1
  (service runs LocalSystem to access ETW; UI is non-elevated).
  Hard-coded; don't expose as installer option.
- **Co-existence with dev builds.** Memory
  `project_release_build_locks.md` — during dev, the service
  runs from `bin\Release\`. The installer puts the service exe
  in `Program Files\ZenVizor\Service\` and registers it there.
  The two should not be active simultaneously. Document the
  dev-vs-installed switching workflow.
- **Uninstall ordering.** Service must be stopped BEFORE its
  files are removed (`ServiceControl Stop="both"`). The
  ACL custom action only applies on install; uninstall doesn't
  need to remove the ACL because the directory itself gets
  removed (or kept per user choice).
- **`wix build` is the CLI driver,** not MSBuild. Both work via
  the `wixproj`; the GHA workflow can use either. Prefer
  `dotnet build` for consistency with the rest of the solution.

### Acceptance — CI

- [ ] GHA workflow builds the `.msi` artifact on push.
- [ ] Artifact uploaded; size reasonable (~20-30 MB).

### Acceptance — manual

- [ ] Fresh Windows VM (or clean machine): install the `.msi`.
- [ ] Service shows in Services.msc as "ZenVizor Network Monitor",
      Automatic, running.
- [ ] `%ProgramData%\ZenVizor\` exists with the correct ACL
      (SYSTEM + Administrators only; verify with
      `accesschk.exe -d`).
- [ ] UI starts; shows clean state; populates data over a few
      minutes.
- [ ] `zvctl ping` works from any shell.
- [ ] Uninstall: service stopped + removed; Program Files cleared;
      data directory preserved by default; re-install picks up
      the existing DB.
- [ ] Re-install over an existing install: clean upgrade, no
      orphan service registration.

---

## 6.8 — Self-monitoring zero-own-traffic gate

**Goal.** The Phase 6 acceptance lock per CLAUDE.md invariant 1.
Verify on a real Windows box that ZenVizor itself emits NO outbound
network bytes during a 24-hour operational window.

### Scope

The three processes that constitute ZenVizor's runtime surface:

- `ZenVizor.Service.exe`
- `ZenVizor.Ui.exe`
- `zvctl.exe`

…must show ZERO bytes outbound across a 24-hour window when
ZenVizor is pointed at itself. Any non-zero reading is a hard block;
investigate the leak source (a logger sink, a telemetry SDK, an
auto-update check, a font-fetching call, an analytics ping —
anything).

### Implementation steps

This is a manual gate; no code work unless a leak is found.

1. **Fresh install.** Use the 6.7 installer on a real Windows box
   (not WSL, not VM with shared networking — needs to be a real
   network interface so ETW captures everything).
2. **Configure for self-monitor.** The default install captures all
   processes including its own. Confirm via `zvctl snapshot` that
   ZenVizor processes appear in the snapshot (with zero bytes
   expected during idle).
3. **24-hour run.** Leave the machine running normally. Use other
   apps; let Windows updates / OneDrive / browsers do their thing.
   ZenVizor should be observing all of this.
4. **End-of-window check.**
   - Per-App view, filter to `ZenVizor.Service.exe`,
     `ZenVizor.Ui.exe`, `zvctl.exe` (all three).
   - 24-hour window.
   - Each line: bytes up = 0, bytes down = 0.
5. **If any non-zero:** drill in. Use the Per-App detail view to
   find when the bytes happened. Cross-reference with the
   `connections` table for endpoints. Identify the call site.
   Fix at the source — remove the dependency, disable the
   network-using feature, replace with an offline alternative.
   Re-run the 24h gate.

### Known leak surfaces to pre-emptively rule out

- **`Microsoft.Extensions.Logging`** — none of the providers we
  wire (`ConsoleLogger`, `EventLogLogger`) emit network. Verify
  no `ApplicationInsightsLogger` or similar accidentally landed.
- **`StreamJsonRpc`** — local pipe only, no network.
- **`Microsoft.Data.Sqlite`** — local file only.
- **`Microsoft.Diagnostics.Tracing.TraceEvent`** — local ETW, no
  network. Confirm no symbol-server lookup happens at runtime
  (it does at build time for `wpr.exe`; runtime should be clean).
- **WPF font rendering** — system fonts only; no web font load.
  Verify no `<TextBlock FontFamily="https://...">` patterns slipped
  in.
- **Wpf.Ui** — local controls; verify no telemetry phone-home.
  Recent Wpf.Ui releases have been clean; check the version we're
  on against any known telemetry advisories.
- **Wpf SVG / image loading** — only `pack://` URIs; no `http`.
- **No analytics, no Application Insights, no Sentry, no Datadog,
  no New Relic** — verified by absence from
  `*.csproj` PackageReferences.

### Acceptance — manual

- [ ] 24-hour real-box run completes.
- [ ] Per-App view: all three ZenVizor processes show 0 bytes /
      0 bytes for the window.
- [ ] No alert fired against any ZenVizor process during the
      window.
- [ ] **No exceptions** — any non-zero reading is a sprint blocker.

---

## Pre-v1 architectural follow-ups (A1 / A2)

Documented separately in `docs/zenvizor-sprint-plan.md` →
"Pre-v1 architectural follow-ups". Summary for cross-reference:

### A1 — Extend `ServiceReconnected` to History/Reports/PerApp/AppDetail

Phase 6.1a wired `MainWindow.ServiceReconnected` and AlertsPage's
subscription. The other four data pages still exhibit the
"banner-sticks-until-nav" UX after a service restart.

**Effort:** Small. Per-page change:
1. Add `ForceReconnectAsync` on `HistoryQueryClient` (one shared
   class change — same shape as
   `AlertsClient.ForceReconnectAsync`).
2. Each of HistoryPage / ReportsPage / PerAppPage / AppDetailPage
   subscribes to `ServiceReconnected` in `OnLoaded`, unsubscribes
   in `OnUnloaded`, handler calls
   `_client.ForceReconnectAsync()` then `RefreshAsync()`.

**Why before v1:** every service restart degrades UX across 4
pages.

### A2 — Centralize query clients at app scope

Each data page currently constructs its own
`HistoryQueryClient`. Better: move ownership to MainWindow as a
single instance (or a small `IQueryClientProvider` service), pages
take a reference, single `ForceReconnectAsync` in
`OnStatusChanged` covers everything, A1's per-page handlers
disappear.

**Effort:** Medium. ~6 files. Refactor surface but no new
behaviour.

**Also unblocks:** the count-summary IPC payload for the nav-badge
accuracy gap from Phase 4b — once MainWindow owns an authoritative
query path, a periodic
`GetAlertsAsync(State=Active)` for badge state becomes a clean
addition.

---

## Execution suggestion

Order I'd take:

1. **6.4 deep-link** — smallest. Hour-ish. Sets a quick win + tests
   the `IpcSchemaVersion.DailyReport` schema bump pattern for
   later.
2. **6.6 zvctl alerts** — small/medium. Useful for QA in subsequent
   phases.
3. **6.2 Settings** — largest non-installer piece. Unblocks 6.7
   (installer needs to know the autostart default and how to
   apply it).
4. **6.3 Tray polish** — small. Folds in nicely after Settings is
   in place (tray's `start_minimized` reads from settings).
5. **6.5 HC sweep** — visual pass. Needs Settings to be visually
   complete to be worth doing.
6. **A1** — small architectural cleanup. Easiest after the active
   feature work is done so the refactor doesn't fight against
   in-flight changes.
7. **6.7 installer** — last code work before final gate. Touches
   build infra; ideally done when all the runtime surface is
   stable.
8. **6.8 self-monitoring gate** — the gate, last step.
9. **A2** — defer to post-v1 unless time permits. The cleaner
   refactor is right but A1 is the necessary fix.

That's the full Phase 6 backlog. Pick a slice and we go.
