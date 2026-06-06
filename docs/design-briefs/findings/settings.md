# Pre-brief — Settings (spec-derived, Group B)

Placeholder screen today. Derived from `docs/zenvizor-prd.md` §7.8 /
§7.9 / §11 + `docs/zenvizor-sprint-plan.md` Phase 6.

---

## 1. Purpose & IA placement

- **Purpose:** user-facing configuration. Autostart, retention,
  intervals, alerts/toast, theme.
- **IA placement:** footer item in the left nav rail
  (`FooterMenuItems`). `Symbol="Settings24"`.
  `NavigationCacheMode.Enabled`. Today routed to `SettingsPage :
  PlaceholderPage` with subtitle "Autostart, retention, theme,
  intervals — Phase 6."

## 2. Requirements lifted from the spec

### Settings list (PRD §7.8 / §7.9, §11.1, Phase 6 scope)

- **Service**
  - Autostart toggle — service start mode, includes "off" for
    fast-boot users who want no monitoring until they launch
    ZenVizor (PRD §5.2 / Phase 6 acceptance).
- **Capture**
  - Flush interval (default ~5 s — locked default per PRD §15).
  - Bucket interval (default 60 s — locked default per PRD §15).
- **Retention** (PRD §7.9 defaults, all user-configurable)
  - `traffic_samples`: 30 days
  - `connections`: 30 days
  - `traffic_hourly`: 90 days
  - `traffic_daily`: 365 days (1 year)
  - `alerts` (after acknowledge): 90 days
- **History**
  - Purge history (button → confirm dialog → service.PurgeHistory(
    before)).
- **Alerts**
  - Toast-on-alert toggle.
- **Appearance**
  - Theme: Light / Dark / Follow system. Wpf.Ui's
    `ApplicationThemeManager` is the underlying manager;
    "Follow system" is the default per `SystemThemeWatcher.Watch`
    (`MainWindow.xaml.cs:32`).

### Settings storage

- PRD §7.8: `settings` table, key/value text. Seeded defaults at
  install time (PRD §7.8 last line).
- IPC: `GetSettings()` / `UpdateSettings(...)` (PRD §9.1).

## 3. Proposed layout & interaction (durable layers)

> The layout below locks the visual language, IA, layout, and
> interaction. Exact per-setting bounds, caption copy, and section
> ordering refinements are **provisional** — see §5.

Root: `<Page>` whose content is a `<ScrollViewer
VerticalScrollBarVisibility="Auto">` wrapping a vertical
`<StackPanel>`. **The whole page IS the scrollable surface** —
annotate `scroll: page`, NOT `scroll: pane` (this is the one screen
where the user expects to scroll-the-page rather than scroll-a-card).

`<StackPanel Margin="24" Spacing="{StaticResource space.16}">` (or
equivalent with margins on each child) containing:

### Header

- `<ui:TextBlock>` `Style="{StaticResource text.subtitle}"`,
  content "Settings".
- `text.caption` `text.secondary` subtitle: "Configure capture,
  retention, alerts, and appearance."

### Section card template

Each section is a `Border surface.card radius.card padding=space.16`
containing:

- Section header — `text.subtitle`, e.g. "Service".
- Optional section caption — `text.caption` `text.secondary`,
  one-line description of what this section governs.
- Setting rows.

### Setting-row template

Two-column Grid (`*` / `Auto`), `Padding=space.8,space.4`:

- **Left column** (vertical StackPanel):
  - Label — `text.body.strong`, e.g. "Autostart".
  - Description — `text.caption` `text.secondary`, e.g.
    "Off = fast boot, no monitoring until you launch ZenVizor."
- **Right column** (control):
  - `ui:ToggleSwitch` for booleans.
  - `ui:NumberBox` for numeric (with units inline as a suffix label,
    e.g. "30 days").
  - `ComboBox` for enumerated (e.g. theme: Light / Dark / Follow
    system).
  - `ui:Button` for actions (Purge history).

Rows within a section are separated by `Border border.subtle 1px`
horizontal line spanning the card's inner width with `space.8`
vertical padding. Last row in a section has no trailing separator.

### Sections (in order)

1. **Service** — Autostart toggle.
2. **Capture** — Flush interval (NumberBox seconds), bucket interval
   (NumberBox seconds).
3. **Retention** — five rows, one per tier:
   - Samples (`NumberBox days`).
   - Connections (`NumberBox days`).
   - Hourly rollups (`NumberBox days`).
   - Daily rollups (`NumberBox days`).
   - Alerts after acknowledge (`NumberBox days`).
4. **History** — Purge history button (`ui:Button`,
   `SymbolIcon="DeleteDismiss24"`, content "Purge history…").
   Triggers a `ContentDialog` confirm: "Purge all history older
   than `<retention>`? This cannot be undone." Two buttons: Cancel
   / Purge. Purge button uses `accent.fill` with critical foreground
   variant — visual weight without misrepresenting it as a
   destructive system action.
5. **Alerts** — Toast-on-alert toggle.
6. **Appearance** — Theme picker `ComboBox` with three items: Light
   / Dark / Follow system.

### Optional footer card

- `Border surface.card radius.card padding=space.16`.
- `text.subtitle` "About".
- `text.body` with version + build hash + a link to the GitHub repo
  / about page. Link styled with `accent.default` foreground.
- Don't open a network-facing URL automatically — the link must use
  shell open (`Process.Start(url) { UseShellExecute = true }`)
  so the user's browser handles it. ZenVizor itself emits no
  traffic.

### Apply / Save behavior

- **Apply-on-change**, not Save button. Each control's
  `ValueChanged` calls `UpdateSettings(key, value)` directly. The
  service writes the SQLite row + re-emits the relevant interval if
  the change affects the running config.
- One exception: **autostart toggle** is the service start mode —
  this writes to SCM (start type), not the SQLite `settings` table.
  Per PRD §5.2: "service auto-start configurable." The brief should
  note the dual writeback (SCM + settings table for the mirrored
  flag).
- **Purge history** is the only multi-step interaction (confirm
  dialog).

## 4. State coverage

| State | Treatment |
|---|---|
| empty | n/a — Settings is always populated from defaults. |
| loading (initial GetSettings) | Default Fluent `ProgressRing` centered on the page during initial load. Brief flash only (< 100 ms expected for a settings query). |
| saving / change-in-flight | Per-control: control briefly disables (50–200 ms) while the IPC roundtrip completes. No skeleton. |
| disconnected | `status.critical.background` banner inline beneath header: "Service disconnected — settings cannot be changed." Every control disables. |
| error (UpdateSettings failed) | `status.caution.background` banner per-section OR per-row (decide in brief; my preference is per-section). Control reverts to its prior value. |
| no warming state | Settings surface; queries the settings table. |

## 5. Provisional-data flag — MANDATORY

The brief **locks** the durable layers:

- Visual language (tokens, type ramp, density).
- IA placement.
- Section structure + section ordering (Service → Capture →
  Retention → History → Alerts → Appearance → About).
- Setting-row template (label + caption + control).
- Apply-on-change interaction model.
- Confirm-before-purge interaction.
- State matrix.

The brief **flags as provisional**, to lock at Phase 6
implementation:

- Exact NumberBox min/max bounds per retention tier (a user-set
  retention of 0 days = "never store" is a reasonable interpretation
  but the contract decision lives at Phase 6).
- Caption copy for each setting row (the strings above are
  illustrative — Phase 6 finalizes voice).
- Whether Theme = "Follow system" persists across launches (lock at
  Phase 6 — depends on whether we honor `SystemThemeWatcher` per
  launch or persist the user's explicit choice).
- Section ordering refinements (e.g. "should About sit above or
  below Appearance?" — sketch shows below).
- "About" card contents — version / build hash / commit ref / any
  diagnostic fields.

## 6. Two designed states — MANDATORY

### (a) Interim placeholder treatment

Wears in the shipped polish-interlude app between now and Phase 6.

- Centered StackPanel on `surface.background`.
- `<ui:SymbolIcon Symbol="Settings48"
  Foreground="{DynamicResource text.tertiary}">`.
- `text.title.large` "Settings".
- `text.body.large` `text.secondary` "Coming in Phase 6 —
  autostart, retention, capture intervals, alerts, and theme."

### (b) Eventual functional layout

The layout in §3 above. The brief asks for both mocks side by side.

## 7. WPF translation gotchas

- **The whole page scrolls** (`scroll: page`). Annotate explicitly so
  the implementer wraps the StackPanel in a `<ScrollViewer>` —
  NavigationView's `DynamicScrollViewer` handles outer scrolling, but
  the cards inside should NOT have their own scrollbars (mixed
  outer/inner scrolling is bad UX). No `MaxHeight`-enforcement code
  needed for this page (no DataGrid).
- **`ui:ToggleSwitch`** in Wpf.Ui — use it, not stock `CheckBox`
  re-templated as a toggle. Brand-canonical.
- **`ui:NumberBox`** in Wpf.Ui — exists; use it for numeric values.
  Has built-in min/max + spin buttons. Annotate units as a suffix
  TextBlock or via `ui:NumberBox.SpinButtonPlacementMode`.
- **Theme picker:** binds to a settings key (`appearance.theme`); the
  `ApplicationThemeManager.Apply(theme)` call wires from the
  view-model's `OnSettingChanged` handler. "Follow system" =
  re-subscribe to `SystemThemeWatcher`; explicit Light/Dark unwires
  it.
- **Confirm dialog:** `Wpf.Ui.Controls.ContentDialog` is the
  Fluent-canonical confirm surface. Use it (not stock
  `MessageBox.Show`).
- **Autostart toggle dual-write:** the SCM call requires elevation,
  which the UI does NOT have. The toggle's `UpdateSettings` call has
  to route through the service; the service does the SCM
  `ChangeServiceConfig` from its elevated context. Document in
  brief.
- **No DataGrid → no `EnforceDataGridBound` wiring.**

## 8. Out-of-scope — features flagged for later

- **Export / import settings** (JSON dump, restore on new
  install). New file I/O surface + format contract.
- **Per-app retention overrides** ("keep history for trusted apps
  shorter"). New data shape — feature.
- **Multi-profile settings** ("work" vs "home" config). Major
  scope expansion — feature.
- **Network proxy settings.** ZenVizor emits no traffic; there is
  nothing to proxy. Hard NO.
- **Self-update / check for updates.** Would emit network traffic.
  Hard NO per zero-own-traffic invariant. The MSI is install-once;
  updates are a future installer concern, not a Settings concern.
- **Telemetry opt-in.** Hard NO — telemetry implies network egress.
- **Color picker / custom theme.** Theme is OS-aligned + brand
  brushes; custom theming is feature-class and would need a parallel
  token surface.
- **Keyboard-shortcut customization.** Feature.
- **About page beyond the small footer card.** A full About page is
  feature-class.
