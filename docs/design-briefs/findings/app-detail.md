# Pre-brief — App Detail (findings, Group A)

Grounded walk of `src/ZenVizor.Ui/Views/AppDetailPage.xaml` +
`AppDetailPage.xaml.cs`. The busiest screen — two side-by-side grids,
a chart card, and a summary card all on one Page.

---

## 1. Purpose & IA placement

- **Purpose:** drill-down for one app. Header + summary card + traffic-
  over-time chart + side-by-side Connections / Recent Sessions tables.
- **IA placement:** **not** a nav-rail item. Reached only via Per-App
  double-click → `NavigationView.Navigate(typeof(AppDetailPage),
  row.AppId)`. Back button uses `NavigationView.GoBack()`.

## 2. What is literally on it today

Root: `<Grid Margin="24">` with 5 rows
(`Auto / Auto / Auto / * / *`).

### Row 0 — header

- `<StackPanel Orientation="Horizontal">`:
  - `<ui:Button Content="&lt; Per-App" Margin="0,0,16,0"
    Click="OnBackClick">`.
  - `<ui:TextBlock x:Name="HeaderText" FontTypography="Subtitle"
    Text="App detail" VerticalAlignment="Center">`. Text filled in
    code via `ApplyDetail` as `"{ImageName} (app id {AppId})"`
    (`AppDetailPage.xaml.cs:131`).

### Row 1 — picker

- `<StackPanel Orientation="Horizontal">`:
  - `<TextBlock Text="Window:" Margin="0,0,8,0">`.
  - `<ComboBox x:Name="WindowCombo" Width="180">` — same five
    `WindowPreset.All` entries as Per-App, default index 1 (last 24h).
  - `<Border x:Name="StatusBanner" Visibility="Collapsed">` — same
    caution-class banner pattern as Per-App.

### Row 2 — summary card

- `<Border CornerRadius="6" Padding="12,8" Margin="0,0,0,12">` with
  the standard card background/border pair.
- Inner `<StackPanel x:Name="SummaryStack" Orientation="Vertical">`:
  - `<TextBlock x:Name="SummaryLine1">`. Filled in code as
    `"Publisher: {pub}   |   Signature: {status}{user-writable tag}
    |   Grain: {grain}"` (`:132-138`).
  - `<TextBlock x:Name="SummaryLine2"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}"
    Margin="0,4,0,0">`. Filled as
    `"Path: {path}   |   Up: {bytes}   |   Down: {bytes}"`
    (`:139-144`).

### Row 3 — chart card

- `<Border CornerRadius="6" Padding="8" MinHeight="220">` with the
  standard card background/border pair.
- Inner Grid, 3 rows (`Auto / Auto / *`):
  - Row 0: `<ui:TextBlock Text="Traffic over time">`.
  - Row 1: `<TextBlock x:Name="ChartSubtitle"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}">`.
    Filled by `ChartBuilder.DescribeView`
    (e.g. `"Showing per-minute detail over the last 24 hours."`).
  - Row 2: a Grid containing
    `<lvc:CartesianChart x:Name="SeriesChart" Background="Transparent"
    MinHeight="180" LegendPosition="Top">` overlaid with
    `<TextBlock x:Name="NoDataOverlay" Visibility="Collapsed"
    HorizontalAlignment="Center" VerticalAlignment="Center"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}"
    Text="No traffic recorded in this window."
    IsHitTestVisible="False">`.

### Row 4 — side-by-side grids

- `<Grid Margin="0,12,0,0">` with two equal columns (`* / *`).
- **Connections card** (left, `Margin="0,0,6,0"`,
  `Background={DynamicResource CardBackgroundFillColorDefaultBrush}`):
  - `<ui:TextBlock Text="Connections (endpoints)"
    Margin="12,8,0,4">`.
  - `<DataGrid x:Name="ConnectionsGrid">` virtualized; 5 columns:
    `Proto` (60), `Address` (2*), `Port` (60), `Up` (90), `Down` (90).
- **Recent sessions card** (right, `Margin="6,0,0,0"`):
  - `<ui:TextBlock Text="Recent sessions" Margin="12,8,0,4">`.
  - `<DataGrid x:Name="SessionsGrid">` virtualized; 5 columns:
    `Session` (80), `PID` (70), `Start` (*), `End` (*), `Services` (*).

## 3. Current behavior

- **Init / refresh:** ctor wires axes + theming + `DataContextChanged`
  → `OnAppIdReceived` (unpacks `AppId` from `int`/`long` DataContext).
  `Loaded` → `EnforceDataGridBounds()` + `RefreshAsync`.
  `SizeChanged` → `EnforceDataGridBounds()` (`:48-55`).
- **Bound enforcement:** `EnforceDataGridBounds` sets each
  DataGrid's `MaxHeight = Math.Max(200, (window.ActualHeight - 220) /
  2)` (`:64-71`). Both grids share the residual vertical space split
  in half. Without this both DataGrids materialize every row.
- **Parallel queries:** `RefreshAsync` runs
  `GetAppDetailAsync` and `GetConnectionsAsync` concurrently on a
  single shared pipe (StreamJsonRpc supports concurrent in-flight
  requests). `Task.WhenAll` joins them (`:108-115`).
- **Chart series style:** `ChartBuilder.BuildSeries(grain, …)` returns
  `LineSeries<DateTimePoint>` for `Samples` grain (≤24h windows),
  `StackedColumnSeries<DateTimePoint>` for `Hourly` / `Daily`
  (`ChartBuilder.cs:26-67`). Up = bottom of stack, Down = top.
- **Aggregation:** raw `detail.Series` entries are accumulated into
  `SortedDictionary<bucketStartMs, bytes>` then projected to
  `DateTimePoint` lists and downsampled via
  `ChartSeriesDownsampler.Downsample` (`:146-160`). Server returns
  per-bucket up/down rows; client buckets up + down separately and
  hands two parallel point arrays to `ChartBuilder`.
- **`NoDataOverlay` shown** when both `upPoints` and `downPoints` are
  empty.
- **Loading:** `Mouse.OverrideCursor = Cursors.Wait` only.
- **Error:** same `StatusBanner` pattern as Per-App.
- **Theme:** `ChartTheming.Apply(SeriesChart)` in ctor + on theme
  flip. Axis labels / separators / legend repaint.

## 4. Data-presentation reality

- **Header:** `"{ImageName} (app id {AppId})"`. The `(app id 42)`
  suffix is implementation detail — useful for debugging, irrelevant
  to a casual user.
- **Summary line 1:** pipe-separated triplet
  `"Publisher: {pub}   |   Signature: {status}{user-writable tag}   |
  Grain: {grain}"`. The user-writable tag is the literal string
  `"  [user-writable path]"` appended inline after status, with no
  visual elevation. This is a security-relevant signal (it's the
  first-customer alert condition) painted as a bracketed footnote.
- **Summary line 2:** `"Path: {path}   |   Up: {bytes}   |   Down:
  {bytes}"`. Path renders as default proportional text — paths read
  badly in proportional fonts.
- **Chart X-axis labeler:** `"HH:mm"` only — no date qualifier. For a
  Hourly grain over 7 days, the same `HH:mm` repeats every day with
  no day boundary cue.
- **Chart Y-axis labeler:** `$"{FormatBytes((long)v)}/bucket"` — the
  string `/bucket` is internal terminology. A bucket is a minute for
  Samples grain, an hour for Hourly, a day for Daily.
- **ChartSubtitle:** `ChartBuilder.DescribeView(grain, preset)` →
  e.g. `"Showing per-minute detail over the last 24 hours."` The
  `"Showing "` prefix is verbal padding.
- **Connections grid:** binds `Protocol`, `RemoteAddress`,
  `RemotePort`, `UpText`, `DownText`. **`RemoteClass` (Local / Wan) is
  populated on the view-model but NOT bound to a column** — the data
  exists, nothing renders it.
- **Sessions grid:** binds `SessionId`, `Pid`, `StartText` /
  `EndText` (formatted `"yyyy-MM-dd HH:mm"` local), `HostedServices`
  (raw comma-separated string from server). End column shows
  `"(running)"` for sessions still alive.
- **All DataGrid columns** use default proportional digits, no
  `TextTrimming`, no `AlternatingRowBackground`.

## 5. State coverage today

| State | Handled today | Notes |
|---|---|---|
| empty | partial | `NoDataOverlay` in chart card. **Connections grid has no empty state.** **Sessions grid has no empty state.** |
| loading | partial | Wait cursor only — no spinner. |
| warming | n/a | History surface. |
| disconnected | merged with error | Same `IsConnectionLost` blind spot as Per-App. |
| error | yes | `StatusBanner` `"Query failed (<Type>): <msg>"`. |

## 6. Friction list (paired with proposed direction)

This is the busiest screen; expect the longest list.

1. **Summary card is two long pipe-separated strings.** Lines wrap
   ungracefully on narrow windows; field labels (`Publisher:`,
   `Signature:`, `Grain:`, `Path:`, `Up:`, `Down:`) are inline. Hard
   to scan.
   → Restructure as a labeled-cell grid: each metric becomes
   `text.caption` `text.secondary` label above its `text.body` value
   (or `text.mono` for Path / Up / Down). 2-row by 3-column or
   3-row by 2-column layout. Specify in brief.
2. **`is_user_writable_path` is buried as a bracketed footnote.**
   `"  [user-writable path]"` inline appended after Signature. This
   is the alert-condition signal — should be visually elevated.
   → When `is_user_writable_path == true` AND `signature_status !=
   "Signed"`, render a pill next to the Signature value:
   `status.caution.background` + `status.caution.text`, copy
   "User-writable path", `radius.control` (`6 px`). Presentation
   only; no new data.
3. **Path is rendered in proportional text.** Paths are code-like —
   characters per row matters, slashes need column alignment, the eye
   tracks better in a monospaced face.
   → Bind path's value to `Style="{StaticResource text.mono}"`.
   Specify `TextWrapping="NoWrap"` + middle-ellipsis truncation
   (`TextTrimming="WordEllipsis"` is the closest WPF gets; a custom
   converter is the polish-pass option). Tooltip carries full path.
4. **Header includes `(app id {AppId})`.** Useful for support,
   irrelevant to casual users.
   → Drop from visible header. Keep AppId in a tooltip on the header.
5. **Back button is text-only (`"< Per-App"`).** The `<` is an ASCII
   chevron; Fluent vocabulary uses `ChevronLeft20` / `ArrowLeft24`.
   → `ui:Button` with `<ui:SymbolIcon Symbol="ChevronLeft20" />` +
   text `"Per-App"`. Smaller, consistent.
6. **Chart card title + subtitle are two stacked TextBlocks at
   default styles.** "Traffic over time" + dynamic subtitle.
   → Title uses `Style="{StaticResource text.subtitle}"`; subtitle
   uses `Style="{StaticResource text.caption}"` with secondary
   foreground (the style already sets it).
7. **Chart Y-axis says `"/bucket"`.** Jargon — internal storage term.
   → Adapt the label per grain: `/min` for Samples, `/hr` for
   Hourly, `/day` for Daily. Pass grain through the labeler closure.
8. **Chart X-axis is bare `HH:mm`** regardless of window span. For
   Hourly grain over 7 days, every day's labels repeat
   indistinguishably.
   → Adapt by grain: `HH:mm` for Samples, `MM-dd HH` for Hourly,
   `MM-dd` for Daily. Same closure plumbing as Y-axis fix.
9. **Connections grid has no empty state.**
   → Centered `text.body` `text.secondary` "No endpoints recorded in
   this window." in the grid viewport when the result is empty.
10. **Sessions grid has no empty state.**
    → Centered `text.body` `text.secondary` "No sessions recorded
    in this window." Same pattern.
11. **Connections grid is missing the Class column.** `RemoteClass`
    (Local / Wan) is on the view-model but never bound. WAN-vs-local
    is THE most user-facing categorical distinction the screen
    surfaces about endpoints.
    → Add a "Class" column (width 60). Render as a `radius.control`
    pill: `chart.wan` background for `Wan`, `chart.local` background
    for `Local`, white text. Same Okabe-Ito palette as the
    categorical viz. Pure presentation; no new data.
12. **Two grids side-by-side compress badly on narrow windows.** At
    800 px window width (`MinWidth` in `MainWindow.xaml:11`), each
    grid gets ~370 px before chrome. Connections needs ~360 px to
    show IPv6 + port without clipping; Sessions needs ~330 px to
    show both timestamps. Both clip ungracefully.
    → Responsive switch: when page width drops below ~1000 px, stack
    the two grids vertically. Specify the breakpoint and the stacked
    sizing in the brief.
13. **Compact density not applied.** Both grids feel airy at default
    spacing — these are the canonical data-dense surfaces.
    → Apply `Style="{StaticResource style.datagrid.compact}"` on
    both (row 22, padding 6,2, body font).
14. **No `AlternatingRowBackground` set on either grid.** Reduces
    row-scan ergonomics.
    → Specify `surface.subtle.alt` (matches Per-App's pattern after
    the rename).
15. **Connections `Address` and Sessions `Start` / `End` /
    `Services` columns use proportional digits.** All four are
    code-like (IP/port, timestamps, comma-separated service tokens).
    → Bind to `text.mono`. Combined with item 13, the two grids
    finally read like tables instead of paragraphs.
16. **Card backgrounds use translucent `CardBackgroundFillColor
    DefaultBrush`.** Migrate to opaque `surface.card`. Chart card
    is the highest-data surface; chart text contrast (axis labels)
    becomes wallpaper-dependent under Mica without opacity.
17. **Card borders use gradient `ControlElevationBorderBrush`.**
    Migrate to `border.card`.
18. **`StatusBanner` does not split disconnected vs query-failed.**
    Same fix as Per-App (item 3 there).
19. **No chart tooltip styling.** Default LiveCharts2 tooltip,
    unstyled.
    → Specify `chart.tooltip.bg` (opaque) + `chart.tooltip.text` in
    the brief; extend `ChartTheming` to apply
    `TooltipBackgroundPaint` / `TooltipTextPaint` to `SeriesChart`.
20. **Chart series colors still LiveCharts2 defaults.** Same Dashboard
    fix; `chart.upSeries` / `chart.downSeries` wiring.
21. **Loading affordance is wait-cursor only.** Three loading
    surfaces (chart, connections grid, sessions grid) all silent
    during refresh.
    → One centered Fluent `ProgressRing` per surface, indeterminate;
    OR a single page-level ring overlaid on the whole content area
    (decide in brief). Either way, no shimmer.
22. **Flyout: not designed yet.** The primer carries the locked
    decision "App Detail flyout is opaque (`surface.layer`),
    NOT acrylic." But there is no flyout in the current XAML. The
    proposed direction has been "selecting a Connections row opens
    a detail flyout for that endpoint (its sessions, its timeline)."
    → Carry the opaque-flyout decision into the brief. If the
    Claude Design mock proposes a flyout for connection inspection,
    it MUST be opaque + `surface.layer`. If it does NOT propose
    one, that's also fine — flyout is a feature for later (F4
    below).
23. **Long header strings collide with picker on the next row.**
    `"svchost.exe (app id 42)"` is short; a name like
    `"NetworkServiceLikeAReallyLongName.exe (app id 12345)"` can
    overflow.
    → Set `MaxWidth` + `TextTrimming="CharacterEllipsis"` on
    `HeaderText`. Combined with item 4, the header is short anyway.
24. **`ChartSubtitle` reads `"Showing X over Y."`** Verbose narrator.
    → Drop `"Showing "`; render as `"per-minute detail · last 24
    hours"` (or grain-by-window without the verb). Specify in brief.

### Scope sort — MANDATORY

**Polish (this round):** 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
14, 15, 16, 17, 18, 19, 20, 21, 22 (carrying the locked decision
forward; not building the flyout), 23, 24.

**Feature (flagged for later — explicitly out of brief):**

- F1. **Click-a-connection-row → flyout / detail page** for that
  endpoint (sessions, timeline, per-endpoint chart). New interaction
  surface; the primer's "flyout intent" is here but the implementation
  is feature-class work.
- F2. **Reverse DNS / hostname column on Connections.** PRD §7.4
  explicitly reserves `connections.resolved_host` for a future
  passive-DNS module — **NOT IN MVP**. Hard "do not propose this
  column" boundary; brief should state this.
- F3. **Brush-to-zoom on chart.** Click-and-drag to select a
  sub-window. New interaction.
- F4. **Endpoint-detail flyout** (see item 22 above — the locked
  decision applies *when* the flyout is added; building the flyout
  itself is feature-class).
- F5. **Export endpoint list to CSV.** Phase 5 owns export.
- F6. **"See related apps" link from svchost row** — jump to all apps
  that ever shared this PID's services. New nav surface.
- F7. **Active-action affordances (kill / block / disconnect endpoint).**
  HARD NO per the passive-only invariant. Document the boundary; do
  not even mock.
- F8. **Filter/search within Connections** (by port, address, class).
  New capability.
