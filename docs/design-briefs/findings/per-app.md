# Pre-brief — Per-App (findings, Group A)

Grounded walk of `src/ZenVizor.Ui/Views/PerAppPage.xaml` +
`PerAppPage.xaml.cs`. This is the input to the Per-App Claude Design
brief, not the brief itself.

---

## 1. Purpose & IA placement

- **Purpose:** apps ranked by total bytes over a chosen window. The
  primary drill surface — entry point to App Detail via double-click.
- **IA placement:** second item in the left nav rail. `Symbol="Apps24"`.
  Hosted in `ui:NavigationView`; `NavigationCacheMode.Enabled` so the
  picker selection survives nav-rail revisits
  (`MainWindow.xaml.cs:52`).

## 2. What is literally on it today

Walked from `PerAppPage.xaml`.

Root: `<Grid Margin="24">` with 3 rows (`Auto / Auto / *`).

### Row 0 — header

- `<StackPanel Orientation="Horizontal">`:
  - `ui:TextBlock FontTypography="Subtitle" Text="Per-App"`. (No
    explicit FontFamily binding — inconsistent with Dashboard, which
    explicitly binds `font.display`.)
  - `<Border x:Name="StatusBanner" Visibility="Collapsed" Margin="16,0,0,0">`
    — `Background="{DynamicResource SystemFillColorCautionBackgroundBrush}"`,
    `Padding="8,4"`, `CornerRadius="4"`, inner `<TextBlock
    x:Name="StatusBannerText" Foreground="{DynamicResource
    SystemFillColorCautionBrush}">`.

### Row 1 — picker

- `<StackPanel Orientation="Horizontal">`:
  - `<TextBlock Text="Window:" VerticalAlignment="Center"
    Margin="0,0,8,0">`.
  - `<ComboBox x:Name="WindowCombo" Width="200">` —
    `ItemsSource = WindowPreset.All`, `DisplayMemberPath =
    nameof(WindowPreset.Label)`. Five presets (last 1h / 24h / 7d /
    30d / 90d), default `SelectedIndex = 1` (last 24h).
  - `<ui:Button x:Name="RefreshButton" Content="Refresh"
    Margin="12,0,0,0">`.

### Row 2 — DataGrid card

- `<Border CornerRadius="6">` —
  `Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"`,
  `BorderBrush="{DynamicResource ControlElevationBorderBrush}"`.
- Inner `<DataGrid x:Name="AppsGrid">`:
  - `AutoGenerateColumns="False"`, `IsReadOnly="True"`,
    `HeadersVisibility="Column"`, `GridLinesVisibility="None"`,
    `Background="Transparent"`, `BorderThickness="0"`,
    `RowBackground="Transparent"`, `AlternatingRowBackground=
    "{DynamicResource SubtleFillColorTertiaryBrush}"`.
  - Virtualized: `EnableRowVirtualization`,
    `VirtualizationMode=Recycling`, `ScrollUnit=Item`,
    `CanContentScroll=True`.
  - `MouseDoubleClick="OnRowDoubleClick"`, `SelectionMode="Single"`,
    `SelectionUnit="FullRow"`.
  - 5 `DataGridTextColumn`s: `App` (2*), `Publisher` (2*),
    `Signature` (100), `Up` (110), `Down` (110).

## 3. Current behavior

- **Refresh trigger:** `Loaded` → `RefreshAsync`, picker selection
  change → `RefreshAsync` (guarded by `IsLoaded`), Refresh button
  click → `RefreshAsync` (`PerAppPage.xaml.cs:31-57`).
- **Loading affordance:** `Mouse.OverrideCursor = Cursors.Wait` for the
  duration of the call; nulled in `finally`. No in-grid spinner.
- **Grid bound enforcement:** `EnforceAppsGridBound` sets
  `AppsGrid.MaxHeight = Math.Max(200, window.ActualHeight - 220)` on
  `Loaded` and `SizeChanged` (`:43-48`). Required because
  `ui:NavigationView`'s `DynamicScrollViewer` hands the page infinite
  vertical extent — without the cap, `DataGrid` materializes every
  row instead of virtualizing
  (memory: `project_wpfui_navigationview_scrollviewer.md`).
- **Drill behavior:** double-click → `NavigationView.Navigate(typeof
  (AppDetailPage), row.AppId)`. The `int AppId` is plumbed via
  `AppDetailPage.DataContext` and unpacked on `DataContextChanged`
  (`AppDetailPage.xaml.cs:75-86`).
- **Error path:** `RefreshAsync` catches all exceptions →
  `StatusBanner.Visibility = Visible`, copy
  `$"Query failed ({ex.GetType().Name}): {ex.Message}"`. Connection-
  lost exceptions (`ConnectionLostException` / `IOException` /
  `ObjectDisposedException`) reset the `HistoryQueryClient`'s pipe
  silently and surface as the same generic banner.

## 4. Data-presentation reality

- **App column:** `ImageName` only. `ImagePath` exists on
  `AppListEntry` but is NOT rendered on this row.
- **Publisher column:** `"(unknown)"` when null/empty
  (`AppRowViewModel.From :129-136`); otherwise raw publisher string.
- **Signature column:** raw enum string from the server
  (`Signed | Unsigned | Invalid | Unchecked`). No color, no icon — a
  data-relevant signal painted as plain text.
- **Up / Down columns:** humanized via `FormatBytes`
  (`:105-117`). `B → KB → MB → GB → TB`, one decimal until value >= 100
  then no decimal. Right-aligned. Proportional digits (NOT mono).
- **Server-side sort:** server returns `AppListResult.Apps` sorted by
  total bytes descending (per `AppListResult.cs` doc comment). No
  client-side sort UI; column headers are not sortable.
- **Row content NOT shown:** `ImagePath` (drill to App Detail to see),
  `IsUserWritablePath` (drill to App Detail summary), `FirstSeenUnixMs`,
  `LastSeenUnixMs`. Most of these are intentional (drill-down's job).
- **AppListEntry does NOT carry `HostedServices`** (verified — see
  `Ipc.Contracts/Dto/AppListResult.cs`). So unlike Dashboard's talker
  rows (which surface `[services]` in the AppLabel), Per-App rows have
  no svchost service decoration today. Adding it would require a
  contract change.
- **No `TextTrimming` declared** on the proportional `App` /
  `Publisher` columns. Default `DataGridTextColumn` truncation behavior
  applies; long values get clipped without an ellipsis affordance.

## 5. State coverage today

| State | Handled today | Notes |
|---|---|---|
| empty | **no** | Empty grid with no caption. |
| loading | partial | Wait cursor only. No in-grid `ProgressRing`. |
| warming | n/a | History surface — queries SQLite, not the in-memory aggregate. |
| disconnected | merged with error | `IsConnectionLost` branch exists in `HistoryQueryClient` but the page doesn't distinguish at presentation. |
| error | yes | `StatusBanner` copy `"Query failed (<Type>): <msg>"`. |

## 6. Friction list (paired with proposed direction)

1. **Empty state is silent.** When the window has no traffic, the
   grid renders empty without explanation.
   → Centered `text.body` `text.secondary` "No applications observed
   in this window." inside the card viewport.
2. **Loading affordance is wait-cursor only.** No in-grid signal —
   first refresh of the day looks identical to a stalled call.
   → Default Fluent `ProgressRing` (indeterminate) centered in the
   card viewport while `RefreshAsync` is in flight. Caption `text.caption`
   `text.secondary` "Loading…" beneath if wait exceeds ~1 s.
   **Skeleton shimmer is explicitly rejected** (design-system §2:
   continuous animation pays no benchmark dividend).
3. **`StatusBanner` doesn't distinguish disconnected from
   query-failed.** Both surface as the same caution-class strip with
   `"Query failed (<Type>): <msg>"`. A pipe-down state ("ZenVizor
   service isn't responding") reads identically to a one-off SQL hiccup.
   → Branch on the catch: when the exception is one of the
   `HistoryQueryClient.IsConnectionLost` set, paint the banner with
   `status.critical.background` + `status.critical` foreground and
   copy `"Service disconnected — last refresh stale."` Otherwise keep
   caution-class with the technical message.
4. **Picker label typography is inconsistent.** "Window:" is a bare
   `TextBlock` at default body weight; `ComboBox` is default; the
   "Refresh" `ui:Button` carries text only.
   → Label uses `Style="{StaticResource text.caption}"` (de-emphasized,
   secondary foreground). ComboBox stays default. Refresh button uses
   `ui:SymbolIcon Symbol="ArrowSync24"` + "Refresh" text — smaller
   horizontal footprint, Fluent vocabulary.
5. **DataGrid uses default density.** The whole point of
   `style.datagrid.compact` (per design-system §8) is data-dense grids
   like this one.
   → Apply `Style="{StaticResource style.datagrid.compact}"`
   (row 22, padding 6,2, body font).
6. **Up / Down columns use proportional digits.** Right-aligned but
   the digits don't column-align across rows.
   → Bind numeric columns to `text.mono` (NF Code Regular 14). Same
   fix as Dashboard.
7. **`AlternatingRowBackground` references `SubtleFillColorTertiaryBrush`
   directly.** Should reference the design-system token `surface.subtle.alt`
   (which today aliases the same Wpf.Ui brush — so this is a rename,
   not a value change).
8. **Card background uses `CardBackgroundFillColorDefaultBrush`
   (translucent).** Primary data-bearing card on the page; per the
   Mica + contrast rule must be `surface.card` (opaque).
9. **Card border uses `ControlElevationBorderBrush` (LinearGradient,
   not tokenizable).** Migrate to `border.card`. (Same call as
   Dashboard.)
10. **`App` and `Publisher` columns have no explicit `TextTrimming`.**
    Long values clip without the ellipsis affordance Dashboard's
    talkers list has.
    → Add `TextTrimming="CharacterEllipsis"` on both. Specify
    consistent right-padding so the trim doesn't kiss the next
    column's left edge.
11. **Drill-down discoverability is low.** Double-click is the only
    path to App Detail. No hover affordance beyond Wpf.Ui's row
    highlight; no chevron telegraphing drill.
    → Add a trailing `ui:SymbolIcon Symbol="ChevronRight12"` (or
    similar) on the right edge of each row when the row is hovered.
    Presentation-only — drill behavior unchanged.
12. **Signature column is plain text.** "Unsigned" / "Invalid" are
    the security-relevant rows and render identically to "Signed"
    visually.
    → Foreground = `status.caution` for "Unsigned" / "Invalid";
    `text.tertiary` for "Unchecked"; `text.primary` for "Signed".
    Convert the column to a `DataGridTemplateColumn` to host the
    conditional foreground. Presentation only — no new data, no new
    contract.
13. **Window-picker labels are verbose.** "Last 1 hour", "Last 24
    hours", "Last 7 days", "Last 30 days", "Last 90 days" rendered
    inline.
    → Render shorthand in the ComboBox item: "1h", "24h", "7d",
    "30d", "90d"; carry the long form in the ComboBox's
    `SelectedItem` display or as a `ToolTip`. Smaller picker,
    rhythm-consistent with the typography ladder.
14. **Header is utilitarian.** Subtitle is "Per-App"; no context.
    → Add caption beneath: `text.caption` `text.secondary` "Apps
    ranked by total bytes over the selected window."
15. **No total/summary strip.** The page tells you each app's bytes
    but never the window total. History has a summary row; Per-App
    doesn't.
    → Add a `space.8`-padded summary row above the DataGrid (or in
    the picker row, right-aligned): `text.caption` labels above
    `text.mono` values for `apps`, `Up`, `Down`. Mirror History's
    summary pattern so the two screens read similarly.

### Scope sort — MANDATORY

**Polish (this round):** 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
15.

**Feature (flagged for later — explicitly out of brief):**

- F1. **Column-click sort.** DataGrid supports header-sort; not
  enabled today. Adds new interaction capability.
- F2. **Inline filter/search box.** `IZenVizorIpc.GetAppList(window,
  filter)` already has a filter parameter at the contract layer but the
  UI doesn't expose it. Adding a search box = new capability.
- F3. **Custom window picker (free-form date range).** Today is 5
  presets. Custom range = feature.
- F4. **Persist picker selection across launches.** Settings concern
  (Phase 6).
- F5. **svchost service decoration on Per-App rows.** Verified above:
  `AppListEntry` does NOT carry `HostedServices`. Adding it requires a
  contract change → feature, not polish.
- F6. **Path column.** `AppListEntry.ImagePath` is available; showing
  it on Per-App rows would be presentation polish (no contract
  change) — but it's a new visible field and pushes the grid wider.
  Marked as feature so the polish round doesn't add columns
  silently. Drill to App Detail to see path.
- F7. **Active-action affordances (kill / block buttons).** HARD NO
  per the passive-only invariant. Not even in the feature backlog —
  out of scope for the product, period. Noted so the brief can
  explicitly forbid them in a mock.
