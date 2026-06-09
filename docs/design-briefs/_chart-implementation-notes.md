# Chart implementation notes — patterns + GOTCHAs from App Detail

Cross-screen reference for any brief that involves a LiveCharts2 chart. Captures
the lessons from App Detail's Phase 3 chart polish so the next chart-bearing
screen (History first; Dashboard is already shipped) inherits them without
rediscovering each one.

**Read this before touching `ChartBuilder`, `ChartTheming`, the chart axes on
any page, or the chart MinHeight / DrawMargin / legend chrome.**

The Dashboard chart is the "working reference" for the no-frills case (live
2-min rolling window). The App Detail chart is the working reference for the
grain-adaptive variable-window case (1h / 24h / 7d / 30d / 90d, Samples /
Hourly / Daily grain). History inherits the App Detail shape.

---

## GOTCHAs (chart can render blank in scary ways)

These are still live; treat them as load-bearing.

### 1. Labelers and tooltip formatters MUST be total

LC2 calls the `Labeler` once per separator during layout, with tick values
**it picks itself** — including positions OUTSIDE the data range. Any function
bound to `Labeler`, `XToolTipLabelFormatter`, or `YToolTipLabelFormatter`
must return a value for ANY input WITHOUT THROWING.

A throw here is swallowed by the render pass and **blanks the entire chart**
(axes, legend, series) — not just the offending label.

**Prime offender:** `new DateTime(ticks)` throws `ArgumentOutOfRangeException`
on out-of-range tick values during layout. `ChartBuilder.FormatXAxisLabel`
and `FormatTooltipTime` already guard:

```csharp
if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
    return string.Empty;
```

**Keep this guard on every new or edited formatter.** Also watch `(long)`
casts that can overflow and any indexing / format assumption that the value
sits inside the data window.

### 2. "Renders but no labels" = axis config, NOT the labeler

The out-of-range guard hides separators emitted outside the data range. It
WILL ALSO hide a wrong whole-axis range (e.g. wrong `UnitWidth` / `MinStep`
pushing the visible range out of bounds) by emitting blank labels
everywhere.

- Healthy per-grain labels visible = labeler + axis config both good.
- Chart renders, no labels anywhere = look at axis range / `UnitWidth` /
  `MinStep`, do NOT touch the formatter.

### 3. Axis lifecycle — ONCE then mutate

Axes are created ONCE in the page ctor and **mutated in place**
(`Labeler` / `MinStep` / `UnitWidth`) per refresh. `SeriesChart.XAxes` and
`SeriesChart.YAxes` arrays are NEVER reassigned after construction.
Wholesale array replacement leaves LC2 v2 in an inconsistent state — chart
renders blank.

App Detail's canonical pattern:

```csharp
private readonly Axis _xAxis;
private readonly Axis _yAxis;

public AppDetailPage()
{
    InitializeComponent();
    _xAxis = new Axis { Labeler = ..., MinStep = ..., UnitWidth = ... };
    _yAxis = new Axis { Labeler = ... };
    SeriesChart.XAxes = new[] { _xAxis };  // ONCE
    SeriesChart.YAxes = new[] { _yAxis };  // ONCE
}

private void UpdateAxesForGrain(TrafficGrain grain, WindowPreset? preset)
{
    _xAxis.Labeler   = ticks => ChartBuilder.FormatXAxisLabel((long)ticks, grain);
    _xAxis.MinStep   = ChartBuilder.MinStepFor(grain, preset);
    _xAxis.UnitWidth = ChartBuilder.UnitWidthFor(grain, preset);
    _yAxis.Labeler   = v => ChartBuilder.FormatYAxisLabel(v, grain);
}
```

Dashboard uses the same pattern. **Keep it.**

### 4. Bar-grain paths are less proven than line-grain

`StackedColumnSeries` + `UnitWidth` only exercise on 7d / 30d / 90d windows
(Hourly + Daily grains). Sweep all five presets (`1h / 24h / 7d / 30d /
90d`) PLUS an empty-traffic window early, not just at the end.

### 5. If it blanks again: probe first, don't hypothesize

A first-chance exception probe found the original chart-blanking bug in one
shot after nine code-reading hypotheses missed it. Re-drop `ChartProbe.cs`
(first-chance + dispatcher exception listener, logs to `%TEMP%`) before
theorizing.

### 6. Out of scope: MainWindow tray-exit teardown

The `SystemThemeWatcher.UnWatch` teardown freeze on tray exit is a known
separate issue, parked deliberately. Do NOT touch teardown code while
polishing chart surfaces.

---

## Patterns established in App Detail (inherit these)

Things that took multiple iterations to land. Don't redo from scratch — these
are working.

### A. Theming runs ONCE + on theme flip; series need explicit re-paint after every Series assignment

`ChartTheming.Apply(chart)` paints axes + legend + tooltip on call AND calls
`ApplyToSeries(chart.Series)` internally. But in a page ctor, `Apply()`
runs BEFORE the page assigns its first `Series`, so the internal
`ApplyToSeries` no-ops over null.

Two consequences:

1. `ChartTheming.Apply(chart)` is wired in the ctor + on
   `ChartTheming.Changed` (theme flip). Do this:

   ```csharp
   ApplyChartTheme();
   ChartTheming.Changed += () => Dispatcher.Invoke(ApplyChartTheme);
   ```

2. After EVERY `SeriesChart.Series = ChartBuilder.BuildSeries(...)`, also
   call `ChartTheming.ApplyToSeries(SeriesChart.Series)` EXPLICITLY —
   otherwise the new Up / Down series render in LC2's default palette.

   App Detail's `ApplyDetail` does this:

   ```csharp
   UpdateAxesForGrain(detail.GrainUsed, preset);
   SeriesChart.Series = ChartBuilder.BuildSeries(detail.GrainUsed, upPoints, downPoints);
   ChartTheming.ApplyToSeries(SeriesChart.Series);  // <-- mandatory
   ```

`ApplyToSeries` is public on `ChartTheming` for exactly this reason.

### B. Grain-adaptive labelers live in ChartBuilder, not on the page

`ChartBuilder` owns the per-grain matrix. Inherit; don't re-implement on the
page side:

| Helper                         | Returns                                                                                                                                                |
|--------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------|
| `MinStepFor(grain, preset)`    | Tick density floor per grain+window (10 min for 1h Samples, 3 hr for 24h Samples, 1 day for 7d Hourly, 5/15 days for 30d/90d Daily).                   |
| `UnitWidthFor(grain, preset)`  | Bar width in `DateTime.Ticks` for DateTime axes. LC2 v2 NEEDS this on bar series or bars render sub-pixel. Line series ignore it (safe to set anyway). |
| `FormatXAxisLabel(ticks, grain)` | `HH:mm` for Samples, `MM-dd HH` for Hourly, `MM-dd` for Daily. Out-of-range-guarded.                                                                  |
| `FormatYAxisLabel(value, grain)` | Nice-rounded byte-per-unit-time (`"20 MB/hr"` not `"19.6 MB/hr"`). Position unchanged; rounding is cosmetic.                                          |
| `YUnitSuffix(grain)`           | `"/min"` (Samples), `"/hr"` (Hourly), `"/day"` (Daily). Used by Y axis AND tooltip Y formatter.                                                         |
| `DescribeView(grain, preset)`  | Subtitle in `"<bucket> · <window>"` form, e.g. `"per-minute detail · last 24 hours"`. No `"Showing "` prefix.                                          |

### C. Forgiving hover via GeometrySize on line series

`ChartBuilder.BuildSeries` sets `GeometrySize = 20`, `GeometryFill = null`,
`GeometryStroke = null` on every `LineSeries`. Tooltip X-snap detection
(`FindingStrategy.CompareOnlyXTakeClosest`, set by `ChartTheming.Apply`)
needs a non-zero hit area to register hover anywhere along the line.
Markers stay invisible (null fill + stroke); only the hit area widens.

### D. Legend overlays the plot via DrawMargin

To get the legend to share space with the plot's top edge instead of
pushing the plot down into its own band:

```csharp
SeriesChart.DrawMargin = new Margin(80, 10, 10, 30);
```

`(Left=80, Top=10, Right=10, Bottom=30)`. `Top=10` is the trick — anything
bigger and the legend grabs its own slice. `Left=80` fits the widest
realistic Y label (e.g. `500 GB/day`); `Bottom=30` fits any of the X
formats.

Dashboard uses `(80, 10, 10, 44)` because Dashboard has a custom static
X-overlay row that needs `Bottom=44` clearance. App Detail / History use
the LC2 X axis so `Bottom=30` is enough.

### E. Tooltip per-series formatter echoes the Y unit

`XToolTipLabelFormatter` calls `FormatTooltipTime` (more precision than the
axis labeler — e.g. `"MM-dd HH:mm"` for Hourly to read the exact bucket
start). `YToolTipLabelFormatter` calls `PerAppPage.FormatBytes(...)` for
precision THEN appends `YUnitSuffix(grain)`. Tooltip rows read
`"Up · 3.2 MB/min"`. The axis labeler uses the rounded `FormatYAxisLabel`
instead; tooltip stays precise.

Wired in `ChartBuilder.BuildSeries` — don't re-implement on the page side.

### F. Downsampler is rate-aware (averages, not sums)

`ChartSeriesDownsampler.DownsampleAverage` collapses 1440 minute-buckets
(24h Samples) → 240 buckets while preserving the per-minute rate semantics.
**Do not replace with a sum-based reducer** — it makes the Y axis lie.

For Hourly / Daily, `ChartSeriesDownsampler.Coalesce(factor: 2)` runs when
bucket count > 60 (7d Hourly → 84 buckets of 2 hours; 90d Daily → 45
buckets of 2 days). `DescribeView` knows about this and emits the right
subtitle (`"2-hour buckets"` / `"2-day buckets"`).

### G. Chart card MinHeight + page-level layout

Two-row gotcha hits if you put the chart card in a `*` row of a Grid:

1. `Height="*"` rows can shrink below the child Border's `MinHeight` under
   pressure, clipping X-axis labels + the card's bottom rounded corners.
   Set `RowDefinition.MinHeight` on the `*` row to match (or beat) the
   Border's MinHeight. Dashboard sets `MinHeight="316"` on the chart row;
   App Detail sets `260`.
2. If multiple `*` rows compete and one row hosts a `DataGrid` whose own
   programmatic `MaxHeight` scales with window height, the grids row will
   outpace the chart row at large windows. WPF Grid `*` arrange gives each
   row its measured-desired height FIRST, then splits excess by weight —
   `3*:2*` weights can't bridge a 700px head-start.
   - App Detail's fix: Row 5 = `Auto`, Row 4 (chart) = `*` (only `*`
     row, owns all residual). DataGrid `MaxHeight` capped at
     `Math.Max(200, Math.Min(360, (window.ActualHeight - 220) / 2))` so
     grids row stays moderate.

### H. Page-level ScrollViewer (if total content can exceed viewport)

If the page's `*` rows + MinHeight floors can make total content height
exceed the viewport (small windows), wrap content in a ScrollViewer with a
`MinHeight={Binding ViewportHeight, ElementName=PageScroll}` binding so:

- Large viewport: inner Border fills viewport, `*` rows expand to fill.
- Small viewport: content exceeds viewport, ScrollViewer scrolls,
  MinHeights still respected.

Lift any pinned overlays (toast banners) OUTSIDE the ScrollViewer so they
pin to viewport bottom, not page bottom (which scrolls).

Wpf.Ui's `NavigationView` already wraps each page in a `DynamicScrollViewer`
but that wrapper's scroll behaviour isn't reliable when our page's
MinHeights push content past the viewport — own the scroll explicitly.

---

## Disconnected vs error pattern (page-side, not chart)

Charts live inside a page's `RefreshAsync`. Inherit App Detail / Per-App's
exception filter pattern:

```csharp
try
{
    // ... do the query, set series, apply theming ...
    SetDataOpacity(1.0);
    _inErrorState = false;
}
catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
{
    _inErrorState = true;
    StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.critical.background");
    StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.critical");
    StatusBannerText.Text = "Service disconnected. Last refresh stale.";
    StatusBanner.Visibility = Visibility.Visible;
    SetDataOpacity(0.6);  // preserve last-known
}
catch (Exception ex)
{
    _inErrorState = true;
    StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
    StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
    StatusBannerText.Text = $"Query failed ({ex.GetType().Name}): {ex.Message}";
    StatusBanner.Visibility = Visibility.Visible;
    SetDataOpacity(0.6);
}
```

`SetDataOpacity(0.6)` dims all data card Borders so stale data still reads
as "stale, last refresh failed." Restore to 1.0 on next successful refresh.

`System.Windows.Controls.TextBlock.ForegroundProperty` is fully qualified
because `Wpf.Ui.Controls.TextBlock` also exists in the file's imports.

---

## Loading overlay pattern (page-side)

Per-surface ring + caption with 1s delay so fast refreshes don't flash.

XAML — overlay is a sibling of the chart inside the chart's row Grid:

```xml
<Grid Grid.Row="2">  <!-- chart row -->
    <lvc:CartesianChart x:Name="SeriesChart" ... />
    <TextBlock x:Name="NoDataOverlay" Visibility="Collapsed" ... />
    <StackPanel x:Name="ChartLoadingOverlay"
                Visibility="Collapsed"
                HorizontalAlignment="Center" VerticalAlignment="Center"
                IsHitTestVisible="False">
        <ui:ProgressRing IsIndeterminate="True" HorizontalAlignment="Center" />
        <TextBlock Style="{StaticResource text.caption}"
                   Foreground="{DynamicResource text.secondary}"
                   Text="Loading…"
                   Margin="0,12,0,0"
                   HorizontalAlignment="Center" />
    </StackPanel>
</Grid>
```

Code-behind:

```csharp
private bool _isLoading;
private readonly DispatcherTimer _loadingDelayTimer;

// ctor:
_loadingDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
_loadingDelayTimer.Tick += (_, _) => { _loadingDelayTimer.Stop(); ShowLoadingOverlays(); };

// RefreshAsync entry:
_isLoading = true;
StatusBanner.Visibility = Visibility.Collapsed;
SetDataOpacity(1.0);
_loadingDelayTimer.Stop();
_loadingDelayTimer.Start();

// RefreshAsync finally:
_isLoading = false;
_loadingDelayTimer.Stop();
HideLoadingOverlays();

// helpers:
private void ShowLoadingOverlays()
{
    if (!_isLoading) return;  // race guard
    ChartLoadingOverlay.Visibility = Visibility.Visible;
    // ...other surfaces' overlays...
}

private void HideLoadingOverlays()
{
    ChartLoadingOverlay.Visibility = Visibility.Collapsed;
    // ...other surfaces' overlays...
}
```

The `_isLoading` race guard handles the case where Tick fires after a fast
refresh has already completed and stopped the timer.

---

## Files / call-sites to reference

- `src/ZenVizor.Ui/Services/ChartBuilder.cs` — single source of truth for
  axis labelers, downsampler decisions, subtitle copy, line-series hover
  config.
- `src/ZenVizor.Ui/Services/ChartTheming.cs` — series + chrome paints;
  page-side wires `Apply()` once in ctor + on `Changed`; `ApplyToSeries()`
  re-call after each `Series` assignment.
- `src/ZenVizor.Ui/Services/ChartSeriesDownsampler.cs` —
  `DownsampleAverage` + `Coalesce`.
- `src/ZenVizor.Ui/Views/AppDetailPage.xaml.cs` — ctor pattern (axes
  once, theme wiring, DrawMargin, timer); `ApplyDetail` Y-axis re-paint
  call; `RefreshAsync` state machine (disconnected/error split, opacity,
  overlays).
- `src/ZenVizor.Ui/Views/AppDetailPage.xaml` — chart card Border with
  `MinHeight="260"` + `RowDefinition.MinHeight="260"` on the `*` row;
  chart Grid with overlays; page-level ScrollViewer wrap; `DrawMargin`
  set in code-behind so legend overlays the plot top.
- `src/ZenVizor.Ui/Views/DashboardPage.xaml.cs` — alternate chart shape
  (live rolling 2-min window, static overlay X labels); also uses the
  once-then-mutate axis pattern.

---

## Validation sweep (every chart-bearing screen)

- All five window presets render (1h / 24h / 7d / 30d / 90d).
- Empty-traffic window renders the no-data overlay (or page equivalent).
- Up violet, Down teal at first paint; same colors after theme flip.
- Tooltip hover registers anywhere across the X axis width (not just on
  the line).
- Tooltip values are PRECISE (`16.6 MB/min`); axis labels are
  NICE-ROUNDED (`20 MB/min`). Both forms readable.
- Legend overlays the plot top; doesn't get its own slice.
- Chart card bottom rounded corners + X-axis labels stay inside the
  card at small window heights.
- Disconnected (stop service) → critical-red banner, content dims to
  0.6, last-known data still visible (not cleared).
- Recovery (restart service, change window picker) → next refresh
  removes banner, content returns to 1.0.
- Loading affordance: 1s delay before ring + caption show; fast
  refresh (1h light app) doesn't flash a ring; slow refresh (90d
  heavy app) shows ring after 1s.

---

## Cross-screen consequences this round established

These were called out in App Detail's brief §16 as "what NOT to redesign on
other screens." If a future polish on Dashboard or Per-App touches the
chart, the change has already landed centrally:

- **`ChartBuilder.BuildSeries`** sets the forgiving-hover `GeometrySize = 20`
  on every line series. Dashboard's chart used to set this page-side; that
  override is now redundant and can be deleted on Dashboard's next polish
  pass.
- **`ChartBuilder.MinStepFor` / `UnitWidthFor` / `FormatXAxisLabel` /
  `FormatYAxisLabel` / `YUnitSuffix` / `DescribeView`** are the shared
  grain-adaptive policy. History inherits them as-is.

Dashboard's chart is rolling-window-with-static-overlay shape — DIFFERENT
shape than App Detail / History. Dashboard's labeler returns empty string
and uses a static WPF overlay for the X markers. Don't unify the labeler
with `FormatXAxisLabel`; the two shapes need different X treatment.
