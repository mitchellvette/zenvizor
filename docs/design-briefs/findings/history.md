# Pre-brief — History (findings, Group A)

Grounded walk of `src/ZenVizor.Ui/Views/HistoryPage.xaml` +
`HistoryPage.xaml.cs`.

---

## 1. Purpose & IA placement

- **Purpose:** aggregate timeline across all apps in the selected
  window. Chart-only — no per-app drill on this surface (that's
  Per-App's job).
- **IA placement:** third item in the left nav rail.
  `Symbol="History24"`. `NavigationCacheMode.Enabled`.

## 2. What is literally on it today

Root: `<Grid Margin="24">` with 4 rows
(`Auto / Auto / Auto / *`).

### Row 0 — header

- `<StackPanel Orientation="Horizontal">`:
  - `<ui:TextBlock FontTypography="Subtitle" Text="History">`.
  - `<Border x:Name="StatusBanner" Visibility="Collapsed">` — same
    caution-class banner pattern as Per-App / App Detail.

### Row 1 — picker

- `<StackPanel Orientation="Horizontal">`:
  - `<TextBlock Text="Window:" Margin="0,0,8,0">`.
  - `<ComboBox x:Name="WindowCombo" Width="180">` — five
    `WindowPreset.All` entries, default `SelectedIndex=1` (last 24h).
  - `<ui:Button Content="Refresh" Margin="12,0,0,0">`.

### Row 2 — summary card

- `<Border Padding="12,8" CornerRadius="6" Margin="0,0,0,12">` with
  the standard card background/border pair.
- Inner `<StackPanel>`:
  - `<TextBlock x:Name="ChartSubtitle"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}">`.
    Filled by `ChartBuilder.DescribeView` (e.g.
    `"Showing hourly totals over the last 7 days."`).
  - `<TextBlock x:Name="SummaryLine" Margin="0,4,0,0">`. Filled as
    `"{count} buckets   |   Up: {bytes}   |   Down: {bytes}"`.

### Row 3 — chart card

- `<Border Padding="8" CornerRadius="6" MinHeight="320">` with the
  standard card background/border pair.
- Inner Grid (1 cell, chart + overlay):
  - `<lvc:CartesianChart x:Name="HistoryChart" MinHeight="280"
    Background="Transparent" LegendPosition="Top">`.
  - `<TextBlock x:Name="NoDataOverlay" Visibility="Collapsed"
    HorizontalAlignment="Center" VerticalAlignment="Center"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}"
    Text="No traffic recorded in this window."
    IsHitTestVisible="False">`.

## 3. Current behavior

- **Refresh trigger:** `Loaded` → `RefreshAsync`; picker change →
  `RefreshAsync` (guarded by `IsLoaded`); Refresh button click →
  `RefreshAsync`.
- **Grain selection:** always `TrafficGrain.Auto`
  (`HistoryPage.xaml.cs:63`). Server picks Samples / Hourly / Daily
  based on window span; resolved grain is reported back via
  `result.GrainUsed` and surfaced in `ChartSubtitle`.
- **Aggregation:** same shape as App Detail. `result.Series` rows
  bucketed into `SortedDictionary<long, long>` for up and down
  separately, projected to `DateTimePoint` lists, downsampled via
  `ChartSeriesDownsampler.Downsample`, fed to `ChartBuilder.BuildSeries`
  (`:77-95`).
- **Loading:** `Mouse.OverrideCursor = Cursors.Wait` only.
- **Error:** standard `StatusBanner` `"Query failed (<Type>):
  <msg>"`.
- **Chart axes:** X labeler `"MM-dd HH:mm"` (universal across grains
  — same problem as App Detail). Y labeler `{bytes}/bucket`.
- **Chart theme:** `ChartTheming.Apply(HistoryChart)` in ctor + on
  theme flip.
- **No DataGrid → no `EnforceDataGridBounds`-style code.**

## 4. Data-presentation reality

- **Summary line:** `"{count} buckets   |   Up: {bytes}   |   Down:
  {bytes}"`. `"N buckets"` is internal terminology — what's a bucket
  to a user?
- **ChartSubtitle:** same `"Showing X over Y."` pattern as App
  Detail. Same verbal padding.
- **X axis:** `"MM-dd HH:mm"` for every grain. For Daily, the
  `HH:mm` part is always `00:00` and pure noise.
- **Y axis:** `"{bytes}/bucket"` jargon — same as App Detail.
- **Bytes humanized via `FormatBytes`** (proportional digits).
- **Legend:** at top, default styling, copy = `"Up"` / `"Down"`
  (this page's series Names are plain — no `"/s"` suffix, unlike
  Dashboard).
- **Chart series colors:** still LiveCharts2 defaults.

## 5. State coverage today

| State | Handled today | Notes |
|---|---|---|
| empty | yes | `NoDataOverlay` "No traffic recorded in this window." centered in chart card. |
| loading | partial | Wait cursor only. |
| warming | n/a | History surface. |
| disconnected | merged with error | Same `IsConnectionLost` blind spot. |
| error | yes | `StatusBanner` `"Query failed (<Type>): <msg>"`. |

## 6. Friction list (paired with proposed direction)

1. **`"N buckets"` is internal jargon.** Means nothing to a casual
   user. The chart visually answers "how many points" anyway.
   → Drop the bucket count; replace with `"{grain}-level detail"`
   matching the ChartSubtitle vocabulary. Or specify a friendlier
   phrasing in the brief (e.g. `"42 hours of data"` for hourly grain,
   `"7 days of data"` for daily).
2. **Summary card is two stacked default-style TextBlocks.** Same
   flatness as App Detail's.
   → Restructure as a 3-cell horizontal strip:
   `Grain` | `Up: X` | `Down: Y`, each cell with `text.caption` label
   above `text.mono` value. Specify column widths in brief.
3. **Refresh button is text-only.** Same SymbolIcon argument as
   Per-App (`ArrowSync24`).
4. **X axis is `"MM-dd HH:mm"` universally.** For Daily grain, the
   `HH:mm` is always `00:00` — wastes label horizontal space and
   adds visual noise. For Samples grain over 1 hour, the `MM-dd` is
   constant and wastes label space.
   → Adapt label per grain: `HH:mm` (Samples), `MM-dd HH`
   (Hourly), `MM-dd` (Daily). Pass grain through the labeler
   closure. Same fix as App Detail.
5. **Y axis says `"/bucket"`.** Same jargon as App Detail.
   → Adapt per grain: `/min`, `/hr`, `/day`.
6. **`StatusBanner` does not split disconnected vs query-failed.**
   Same fix as Per-App / App Detail.
7. **Card backgrounds use translucent
   `CardBackgroundFillColorDefaultBrush`.** Migrate to opaque
   `surface.card`. Chart card especially — axis label contrast
   becomes wallpaper-dependent otherwise.
8. **Card borders use `ControlElevationBorderBrush`.** Migrate to
   `border.card`.
9. **Loading affordance is wait-cursor only.**
   → Centered Fluent `ProgressRing` in the chart-card viewport
   during `RefreshAsync`. No shimmer.
10. **Chart series colors still LiveCharts2 defaults.** Same fix as
    Dashboard / App Detail — `chart.upSeries` / `chart.downSeries`
    wiring.
11. **No chart tooltip styling.** Same fix.
12. **Chart card has no header.** Only the legend (`"Up"`, `"Down"`)
    at top. The summary card above sits as a separate Border —
    visually two stacked cards. Visual hierarchy is flatter than the
    other screens.
    → Choose one consistent pattern across History, App Detail, and
    Dashboard:
    Option A (recommended): merge the History summary card and chart
    card into one larger card with the summary as an internal header
    row. Mirrors App Detail's chart card composition (title +
    subtitle + chart).
    Option B: keep separate cards; add a chart-card title "Total
    traffic" so the visual rhythm matches.
    Specify which in brief.
13. **Header subtitle "History" is utilitarian.**
    → Add caption beneath: `text.caption` `text.secondary` "Aggregate
    up/down traffic across all apps in the selected window."
14. **`ChartSubtitle` reads `"Showing X over Y."`** Same verbal
    padding as App Detail.
    → Drop `"Showing "`. Format as `"hourly totals · last 7 days"`.
15. **`CornerRadius="6"` on both cards.** Same migration to
    `radius.card` (10 px role token).
16. **Picker label "Window:" + ComboBox layout** is the same as
    Per-App / App Detail.
    → Same treatment: label uses `text.caption`; window-preset
    shorthand (`1h / 24h / 7d / 30d / 90d`); refresh button gets
    a SymbolIcon.

### Scope sort — MANDATORY

**Polish (this round):** 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
15, 16.

**Feature (flagged for later — explicitly out of brief):**

- F1. **Grain override** (manually force Samples / Hourly / Daily).
  Today Auto only. Lets the user inspect at finer resolution than
  the server picked. New capability.
- F2. **Per-app overlay** (stacked-by-app History view). Adds
  multi-series viz; conflicts with the screen's "aggregate" purpose
  but is a natural extension. New capability.
- F3. **WAN vs Local split** as a separate series or overlay. PRD §11
  names this as a Daily Report element; arguably belongs here too.
  Adding the split = new categorical viz = feature.
- F4. **Brush-to-zoom on chart.** Click-and-drag to select a
  sub-window. New interaction.
- F5. **Custom window picker (free-form date range).** Same as
  Per-App F3.
- F6. **Click a chart point to filter Per-App to that window.** Cross-
  page state coordination — new interaction model.
- F7. **Export aggregate history to CSV.** Phase 5 owns export.
