// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class HistoryPage : Page
{
    // A2: assigned from MainWindow.HistoryQueryClient in OnPageLoaded.
    // No _client touch happens before Loaded (verified at A2 time).
    private HistoryQueryClient _client = null!;

    // Chart axes — created ONCE in the ctor and mutated per refresh
    // (UpdateAxesForGrain assigns fresh Labeler / MinStep / UnitWidth values).
    // Per _chart-implementation-notes.md §3, wholesale axis-array replacement
    // combined with same-frame Series reassignment leaves LiveCharts2 v2 in
    // an inconsistent state and the chart renders blank. Mirrors the App
    // Detail pattern (AppDetailPage.xaml.cs:107-118, 217-223).
    private readonly Axis _xAxis;
    private readonly Axis _yAxis;

    // Phase 4 — loading-state flag + delayed-reveal timer.
    // _isLoading is true while a RefreshAsync is in flight; the timer Tick
    // gates on it (race-safe — Tick can fire after a fast refresh has
    // already completed).
    // _loadingDelayTimer runs once per refresh — if it ticks before the
    // refresh completes, the chart card reveals a centered ring +
    // "Loading…" caption. Fast refreshes (<1s) never flash a ring.
    // (History has no DataGrid empty-state to gate, so no _inErrorState
    // flag is needed; App Detail's _inErrorState gates per-grid overlays.)
    private bool _isLoading;
    private readonly DispatcherTimer _loadingDelayTimer;

    // Phase 2 (Epic A 1.1.0) — popover state.
    //
    // _lastChartWindow / _lastChartGrain are stashed from each successful
    // ApplyResult; ChartClickResolver needs both to clamp the popover query
    // window to what the user can actually see + label rates in the chart's
    // per-grain unit (/min / /hr / /day).
    //
    // _popoverRequestSeq dedupes superseded in-flight queries — fast double
    // clicks should display the latest, not whichever GetAppListAsync
    // happened to come back last.
    //
    // _popoverBackdropDown is the MouseDown half of the paired MouseDown +
    // MouseUp backdrop-dismiss handshake (memory: Phase 1's flyout backdrop
    // pattern; dismiss-on-MouseDown leaked dismissals from the calendar
    // popup close).
    private QueryWindow? _lastChartWindow;
    private TrafficGrain _lastChartGrain = TrafficGrain.Samples;
    private int _popoverRequestSeq;
    private bool _popoverBackdropDown;

    public HistoryPage()
    {
        InitializeComponent();

        // ItemTemplate (binds Short, ToolTip=Label) is set in XAML — do NOT
        // also assign DisplayMemberPath here. ItemTemplate and DisplayMemberPath
        // are mutually exclusive on ItemsControl; setting both throws
        // InvalidOperationException at runtime. Matches the PerAppPage /
        // AppDetailPage picker pattern.
        WindowCombo.ItemsSource = WindowPreset.All;
        WindowCombo.SelectedIndex = 1; // Last 24h

        // Axes created ONCE here and mutated per refresh by
        // UpdateAxesForGrain. Initial labelers / step / unit-width default to
        // Samples grain so the chart isn't blank between ctor and the first
        // ApplyResult. The axis INSTANCES persist for the page's lifetime —
        // HistoryChart.XAxes / YAxes are assigned exactly once, here.
        _xAxis = new Axis
        {
            Labeler = ticks => ChartBuilder.FormatXAxisLabel((long)ticks, TrafficGrain.Samples),
            MinStep = ChartBuilder.MinStepFor(TrafficGrain.Samples, preset: null),
            UnitWidth = ChartBuilder.UnitWidthFor(TrafficGrain.Samples, preset: null),
        };
        _yAxis = new Axis
        {
            Labeler = v => ChartBuilder.FormatYAxisLabel(v, TrafficGrain.Samples),
        };
        HistoryChart.XAxes = new[] { _xAxis };
        HistoryChart.YAxes = new[] { _yAxis };

        // Force the plot rectangle. Without an explicit DrawMargin, LC2
        // auto-reserves a horizontal band at the top for the legend (since
        // LegendPosition="Top"), pushing the plot area down into its own
        // band. With Top=10 set explicitly, the legend overlays the top of
        // the plot — matches AppDetailPage.xaml.cs:128 and reclaims the
        // lost vertical canvas. Left=80 fits the widest realistic Y-axis
        // label ("500 GB/day" plus padding); Bottom=30 fits the X-axis
        // label strip (HH:mm / MM-dd HH / MM-dd are all ≤ ~20px tall).
        HistoryChart.DrawMargin = new Margin(80, 10, 10, 30);

        ApplyChartTheme();
        ChartTheming.Changed += () => Dispatcher.Invoke(ApplyChartTheme);

        // Loading-overlay reveal timer. RefreshAsync starts it on entry;
        // Tick (after 1s) reveals the chart card ring IF the refresh is
        // still in flight. RefreshAsync also Stops it in finally, so a fast
        // refresh races to Stop() before Tick fires — ShowLoadingOverlay
        // also gates on _isLoading in case dispatch ordering loses.
        _loadingDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _loadingDelayTimer.Tick += (_, _) =>
        {
            _loadingDelayTimer.Stop();
            ShowLoadingOverlay();
        };

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;

        // Phase 2 (Epic A 1.1.0) — click-to-attribute popover wiring.
        // Preview phase so LC2's own pointer machinery can't swallow it.
        HistoryChart.PreviewMouseLeftButtonDown += OnChartPreviewMouseLeftButtonDown;
        PopoverOverlay.PreviewMouseLeftButtonDown += OnPopoverBackdropMouseDown;
        PopoverOverlay.PreviewMouseLeftButtonUp += OnPopoverBackdropMouseUp;
        PopoverOverlay.MouseLeave += (_, _) => _popoverBackdropDown = false;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            // A2: pick up the shared query client. Pages are
            // NavigationCacheMode.Enabled, so this runs once per page
            // instance; the assignment is idempotent across re-loads.
            _client = mw.HistoryQueryClient;
            mw.HistoryWiped += OnHistoryWiped;
            // A1: subscribe to the MainWindow-driven ServiceReconnected
            // event so a service restart (Settings panel, sc.exe, Services
            // snap-in) refreshes the page automatically rather than
            // waiting for the user to navigate away and back.
            mw.ServiceReconnected += OnServiceReconnected;
        }
        await RefreshAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.HistoryWiped -= OnHistoryWiped;
            mw.ServiceReconnected -= OnServiceReconnected;
        }
        // Page leaving — popover can't outlive the chart it was anchored to.
        DismissPopover();
    }

    private async void OnHistoryWiped(object? sender, EventArgs e) => await RefreshAsync();

    private async void OnServiceReconnected(object? sender, EventArgs e)
    {
        // A2: MainWindow.OnStatusChanged force-reconnected the shared
        // _client before raising this event, so the next RefreshAsync
        // hits a fresh pipe. No per-page reconnect needed.
        await RefreshAsync();
    }

    /// <summary>
    /// Mutate the existing <see cref="_xAxis"/> / <see cref="_yAxis"/>
    /// instances in place with grain- and window-tailored Labeler, MinStep,
    /// and UnitWidth. Called from <see cref="ApplyResult"/> as soon as the
    /// resolved grain is known.
    ///
    /// IMPORTANT: this method MUST NOT reassign <c>HistoryChart.XAxes</c> or
    /// <c>HistoryChart.YAxes</c> — see ctor doc / chart implementation notes.
    /// </summary>
    private void UpdateAxesForGrain(TrafficGrain grain, WindowPreset? preset)
    {
        _xAxis.Labeler   = ticks => ChartBuilder.FormatXAxisLabel((long)ticks, grain);
        _xAxis.MinStep   = ChartBuilder.MinStepFor(grain, preset);
        _xAxis.UnitWidth = ChartBuilder.UnitWidthFor(grain, preset);
        _yAxis.Labeler   = v => ChartBuilder.FormatYAxisLabel(v, grain);
    }

    private void ApplyChartTheme() => ChartTheming.Apply(HistoryChart);

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // Window changed — the popover was anchored to a bucket that's
        // about to be replaced. Dismiss before the refresh tears the chart.
        DismissPopover();
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (WindowCombo.SelectedItem is not WindowPreset preset) return;

        // Entry — set loading state, recover from any previous error.
        // Last-known summary + chart series stay visible during the refresh
        // (App Detail pattern); only the chart card overlays a ring after
        // 1s if the query is still in flight.
        _isLoading = true;
        StatusBanner.Visibility = Visibility.Collapsed;
        SetDataOpacity(1.0);   // un-dim from any previous disconnected/error
        _loadingDelayTimer.Stop();
        _loadingDelayTimer.Start();

        try
        {
            // Always Auto — the server picks the right tier for the window span.
            var result = await _client.GetTrafficHistoryAsync(preset.ToWindow(), TrafficGrain.Auto);
            ApplyResult(result, preset);
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            // Pipe down — Phase 6.5 standardized to caution-amber +
            // PlugDisconnected20 glyph across every page. Last-known data
            // stays dimmed to 0.6 so the user can still read what was
            // there before the pipe broke.
            StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
            StatusBannerGlyph.Symbol = SymbolRegular.PlugDisconnected20;
            StatusBannerGlyph.SetResourceReference(SymbolIcon.ForegroundProperty, "status.caution.text");
            StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
            StatusBannerText.Text = "Service disconnected. Last refresh stale.";
            StatusBanner.Visibility = Visibility.Visible;
            SetDataOpacity(0.6);
        }
        catch (Exception ex)
        {
            // Any other query failure — caution-amber banner, same dim.
            StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
            StatusBannerGlyph.Symbol = SymbolRegular.Warning20;
            StatusBannerGlyph.SetResourceReference(SymbolIcon.ForegroundProperty, "status.caution.text");
            StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
            StatusBannerText.Text = $"Query failed ({ex.GetType().Name}): {ex.Message}";
            StatusBanner.Visibility = Visibility.Visible;
            SetDataOpacity(0.6);
        }
        finally
        {
            _isLoading = false;
            _loadingDelayTimer.Stop();
            HideLoadingOverlay();
        }
    }

    /// <summary>
    /// Show the chart card loading ring. Called from the
    /// _loadingDelayTimer Tick handler after the 1s grace period; guards
    /// on _isLoading in case the refresh completed in the same dispatcher
    /// cycle and Stop() lost the race.
    /// </summary>
    private void ShowLoadingOverlay()
    {
        if (!_isLoading) return;
        ChartLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoadingOverlay()
    {
        ChartLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Dim or restore both data card surfaces (summary, chart). 0.6
    /// telegraphs stale data during disconnected/error states without
    /// clearing the last-known content; 1.0 restores on next successful
    /// refresh.
    /// </summary>
    private void SetDataOpacity(double opacity)
    {
        SummaryCard.Opacity = opacity;
        ChartCard.Opacity = opacity;
    }

    private void ApplyResult(TrafficHistoryResult result, WindowPreset preset)
    {
        var bucketsUp = new SortedDictionary<long, long>();
        var bucketsDown = new SortedDictionary<long, long>();
        foreach (var p in result.Series)
        {
            bucketsUp[p.BucketStartUnixMs]   = bucketsUp.GetValueOrDefault(p.BucketStartUnixMs)   + p.BytesUp;
            bucketsDown[p.BucketStartUnixMs] = bucketsDown.GetValueOrDefault(p.BucketStartUnixMs) + p.BytesDown;
        }

        var upPoints = bucketsUp
            .Select(kv => new DateTimePoint(DateTimeOffset.FromUnixTimeMilliseconds(kv.Key).LocalDateTime, kv.Value))
            .ToList();
        var downPoints = bucketsDown
            .Select(kv => new DateTimePoint(DateTimeOffset.FromUnixTimeMilliseconds(kv.Key).LocalDateTime, kv.Value))
            .ToList();

        // Average-based cap: the Y axis renders rate per the grain's time unit
        // (/min, /hr, /day), so the reducer must produce averages — summing
        // adjacent buckets would make the displayed numbers ~N× too high
        // relative to the labeled unit. Caps at ChartSeriesDownsampler.MaxBuckets
        // for the very dense 24h Samples case (1440 → 240 buckets, each
        // carrying the average bytes/min over its 6-minute group).
        (upPoints, downPoints) = ChartSeriesDownsampler.DownsampleAverage(upPoints, downPoints);

        // Bar-grain density control: 7d Hourly (168 buckets) and 90d Daily
        // (90 buckets) coalesce 2× so the rendered bar density stays
        // readable. 30d Daily (30 buckets) and 24h Samples (already capped
        // to 240 by DownsampleAverage above) do not need extra coalescing.
        // ChartBuilder.DescribeView's subtitle copy mirrors this policy
        // (returns "2-hour buckets" / "2-day buckets" for the coalesced
        // cases). Matches AppDetailPage.ApplyDetail.
        if ((result.GrainUsed == TrafficGrain.Hourly || result.GrainUsed == TrafficGrain.Daily)
            && Math.Max(upPoints.Count, downPoints.Count) > 60)
        {
            (upPoints, downPoints) = ChartSeriesDownsampler.Coalesce(upPoints, downPoints, factor: 2);
        }

        // Mutate axis properties (in-place — see UpdateAxesForGrain doc) BEFORE
        // assigning Series so the new Labeler / MinStep / UnitWidth are in
        // place when LC2 lays out the upcoming redraw triggered by the Series
        // assignment.
        UpdateAxesForGrain(result.GrainUsed, preset);
        HistoryChart.Series = ChartBuilder.BuildSeries(result.GrainUsed, upPoints, downPoints);
        // Re-paint Up/Down with brand chart.upSeries / chart.downSeries.
        // ChartTheming.Apply in the ctor ran BEFORE HistoryChart.Series was
        // assigned, so its internal ApplyToSeries pass no-op'd on the
        // still-null Series array; without this re-call the fresh line /
        // bar series here would render in LC2's default palette.
        ChartTheming.ApplyToSeries(HistoryChart.Series);
        ChartSubtitle.Text = ChartBuilder.DescribeView(result.GrainUsed, preset);

        NoDataOverlay.Visibility = upPoints.Count == 0 && downPoints.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Phase 2 — stash visible chart window + grain for ChartClickResolver.
        // The resolver needs both to clamp the popover query window to what
        // the user can see and to label rates in the chart's per-grain unit.
        _lastChartWindow = preset.ToWindow();
        _lastChartGrain = result.GrainUsed;

        FillSummary(result, preset);
    }

    /// <summary>
    /// Populate the 5-cell "Total traffic" summary card. The summary scope
    /// is deliberately wider than the chart card:
    ///
    /// <list type="bullet">
    ///   <item><b>RESOLUTION</b> shows the chart grain in user-facing
    ///   vocabulary (<see cref="GrainLabel"/>) — never the enum name
    ///   (`Samples`) or the internal "N buckets" jargon.</item>
    ///   <item><b>UPLOADED</b> / <b>DOWNLOADED</b> are window totals,
    ///   summed across every TrafficPoint in <c>result.Series</c>. Painted
    ///   in <c>chart.upSeries</c> / <c>chart.downSeries</c> so they
    ///   visually anchor to the chart's series below.</item>
    ///   <item><b>AVERAGE</b> / <b>PEAK</b> use the
    ///   <see cref="ImpliedStep">window-implied step</see> (1h → per-minute,
    ///   24h → per-hour, 7d/30d/90d → per-day) — deliberately NOT the
    ///   chart grain. The chart grain depends on coalesce (a 7d window
    ///   plots 2-hour Hourly buckets), but the user-friendly rate for "how
    ///   busy is this window" is per the window's own depth: a 7d window
    ///   reads naturally as a per-day rate, not a per-2-hour rate.</item>
    /// </list>
    /// </summary>
    private void FillSummary(TrafficHistoryResult result, WindowPreset preset)
    {
        ResolutionValue.Text = GrainLabel(result.GrainUsed);
        ResolutionValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");

        var totalUp = result.Series.Sum(p => p.BytesUp);
        var totalDown = result.Series.Sum(p => p.BytesDown);

        UploadedValue.Text = PerAppPage.FormatBytes(totalUp);
        UploadedValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "chart.upSeries");

        DownloadedValue.Text = PerAppPage.FormatBytes(totalDown);
        DownloadedValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "chart.downSeries");

        var step = StepFor(preset);
        var stepCount = StepCountFor(preset);
        var suffix = StepSuffix(step);

        // Average = (combined Up + Down total) ÷ nominal step count for the
        // window. Nominal — not "buckets seen" — so a sparse window still
        // reports a defined average rate against the full window depth
        // (e.g. a 24h window with 6 hours of activity still averages over 24).
        var combinedTotal = totalUp + totalDown;
        var average = stepCount > 0 ? combinedTotal / stepCount : 0L;
        AverageValue.Text = PerAppPage.FormatBytes(average) + suffix;
        AverageValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");

        // Peak = busiest single implied-step bucket's combined (Up + Down)
        // rate, plus that bucket's start timestamp. Re-bucket the raw points
        // into the implied step regardless of server grain — for 24h Samples
        // we collapse 60 minute-points into each per-hour implied bucket;
        // for 7d Hourly we collapse 24 hour-points into each per-day bucket.
        // 1h / 30d / 90d already align (server grain == implied step) but
        // truncation is idempotent there so the same code path covers all
        // windows.
        var (peakBytes, peakStartTicks) = ComputePeakAtImpliedStep(result, step);
        if (peakBytes > 0)
        {
            PeakValue.Text = PerAppPage.FormatBytes(peakBytes) + suffix;
            // Inline with the PEAK eyebrow — "PEAK · 17:00" /
            // "PEAK · 05-29". Leading separator carried on this text
            // (not on the eyebrow) so the empty-state branch below
            // collapses the timestamp slot entirely.
            PeakTimestamp.Text = "· " + FormatPeakTimestamp(peakStartTicks, step);
        }
        else
        {
            // Zero-traffic window — Peak is genuinely zero, no timestamp.
            // Brief §4 empty state lock — "0 B" not em-dash. Eyebrow
            // reads just "PEAK" (no trailing separator).
            PeakValue.Text = "0 B";
            PeakTimestamp.Text = string.Empty;
        }
        PeakValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");
    }

    /// <summary>
    /// Re-bucket <c>result.Series</c> into the implied-step grain
    /// (truncating each raw bucket-start to the next-coarsest step
    /// boundary) and return the busiest combined (Up + Down) bucket along
    /// with its bucket-start ticks for timestamp rendering. Returns
    /// (0, 0) for an empty / zero-traffic window.
    /// </summary>
    private static (long peakBytes, long peakStartTicks) ComputePeakAtImpliedStep(
        TrafficHistoryResult result,
        ImpliedStep step)
    {
        if (result.Series.Count == 0) return (0, 0);

        var combined = new SortedDictionary<long, long>();
        foreach (var p in result.Series)
        {
            var key = TruncateToStep(p.BucketStartUnixMs, step);
            combined[key] = combined.GetValueOrDefault(key) + p.BytesUp + p.BytesDown;
        }

        if (combined.Count == 0) return (0, 0);

        long peakKey = 0;
        long peakVal = 0;
        foreach (var kv in combined)
        {
            if (kv.Value > peakVal)
            {
                peakVal = kv.Value;
                peakKey = kv.Key;
            }
        }
        return (peakVal, peakKey);
    }

    /// <summary>
    /// Window-implied step — the user-friendly cadence to think about
    /// rates over the selected window, deliberately distinct from the
    /// chart grain. 1h reads as per-minute (the chart also plots minutes),
    /// 24h reads as per-hour (the chart still plots minutes — Samples
    /// grain — but no one says "this app averaged 921 bytes per minute
    /// over the last 24 hours", they say per-hour); multi-day windows
    /// (7d / 30d / 90d) all read as per-day.
    /// </summary>
    private enum ImpliedStep { Minute, Hour, Day }

    private static ImpliedStep StepFor(WindowPreset preset) => preset.Label switch
    {
        "Last 1 hour" => ImpliedStep.Minute,
        "Last 24 hours" => ImpliedStep.Hour,
        _ => ImpliedStep.Day,
    };

    /// <summary>
    /// Nominal step count for the window — used as the denominator in the
    /// Average rate. Nominal (not buckets-with-data) so sparse windows
    /// still report an honest average against the window's full depth.
    /// </summary>
    private static int StepCountFor(WindowPreset preset) => preset.Label switch
    {
        "Last 1 hour" => 60,
        "Last 24 hours" => 24,
        "Last 7 days" => 7,
        "Last 30 days" => 30,
        "Last 90 days" => 90,
        _ => 1,
    };

    private static string StepSuffix(ImpliedStep step) => step switch
    {
        ImpliedStep.Minute => "/min",
        ImpliedStep.Hour => "/hr",
        _ => "/day",
    };

    /// <summary>
    /// Truncate a Unix-ms timestamp to the next-coarsest implied-step
    /// boundary, returning the local-time bucket-start as DateTime.Ticks.
    /// Truncation is idempotent — when server grain already matches the
    /// implied step (1h Samples → per-minute; 30d/90d Daily → per-day),
    /// the input already sits on a step boundary.
    /// </summary>
    private static long TruncateToStep(long unixMs, ImpliedStep step)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        return step switch
        {
            ImpliedStep.Minute => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0).Ticks,
            ImpliedStep.Hour => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0).Ticks,
            _ => new DateTime(dt.Year, dt.Month, dt.Day).Ticks,
        };
    }

    private static string FormatPeakTimestamp(long ticks, ImpliedStep step)
    {
        if (ticks <= 0) return string.Empty;
        var dt = new DateTime(ticks);
        return step switch
        {
            ImpliedStep.Day => dt.ToString("MM-dd", CultureInfo.InvariantCulture),
            _ => dt.ToString("HH:mm", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// User-facing label for the RESOLUTION summary cell. Brief §8.4 Q2
    /// LOCKED — never surface the enum name (`Samples`) or the internal
    /// "N buckets" jargon. Casual user vocabulary only.
    /// </summary>
    private static string GrainLabel(TrafficGrain grain) => grain switch
    {
        TrafficGrain.Hourly => "Hourly",
        TrafficGrain.Daily => "Daily",
        _ => "Per-minute",
    };

    // =====================================================================
    // Phase 2 (Epic A 1.1.0) — click-to-attribute popover.
    //
    // See docs/roadmap/epic-a-history-click-to-attribute.md §Phase 2 for
    // the design and docs/epic-a-phase-2-gate-0.md for the LiveCharts2
    // pixel→data API the resolver depends on.
    //
    // Flow:
    //   1. PreviewMouseLeftButtonDown on HistoryChart →
    //      HandleChartClickAsync.
    //   2. ChartClickResolver maps click pixel → (popover window, visual
    //      bucket anchor, grain). Misses (axis-label band, gaps) silently
    //      no-op (open question #6).
    //   3. GetAppListAsync over the popover window → top-5 + remainder.
    //   4. Build chrome in code-behind (avoid same-assembly UserControl
    //      XAML cross-ref issue per memory project_wpf_usercontrol_same_
    //      assembly), measure + position at the visual bucket center pixel,
    //      reveal overlay.
    //   5. Backdrop click dismisses; if the dismiss click landed on the
    //      chart pixels too, immediately resolve a new popover at that
    //      position (open question #7 — confirmed).
    // =====================================================================

    private void OnChartPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Popover open? Backdrop handler owns this click — don't double-fire.
        if (PopoverOverlay.Visibility == Visibility.Visible) return;
        _ = HandleChartClickAsync(e.GetPosition(HistoryChart));
    }

    private async Task HandleChartClickAsync(Point clickPx)
    {
        if (_lastChartWindow is not { } chartWindow) return;

        if (!ChartClickResolver.TryResolveClick(
                clickPx, HistoryChart, chartWindow, _lastChartGrain, out var resolved)
            || resolved is null)
        {
            return; // miss: axis-label band, gap, no series — silent no-op
        }

        var seq = ++_popoverRequestSeq;
        AppListResult result;
        try
        {
            result = await _client.GetAppListAsync(resolved.PopoverWindow);
        }
        catch
        {
            // IPC failure: silent dismiss. RefreshAsync's banner handles
            // chronic disconnection; a popover-specific error UI would be
            // over-engineered for a click-driven, retry-able interaction.
            return;
        }

        // Superseded by a later click — drop this result.
        if (seq != _popoverRequestSeq) return;

        if (result.Apps.Count == 0)
        {
            // Open question #6: silent no-op on empty buckets. Chart
            // already shows zero traffic visually; a "no traffic" popover
            // would be redundant.
            return;
        }

        ShowPopover(resolved, result);
    }

    private void ShowPopover(ResolvedClick resolved, AppListResult result)
    {
        PopoverChrome.Content = BuildPopoverChromeContent(resolved, result);
        PopoverOverlay.Visibility = Visibility.Visible;
        PositionPopover(resolved);
    }

    private void DismissPopover()
    {
        PopoverOverlay.Visibility = Visibility.Collapsed;
        PopoverChrome.Content = null;
        _popoverBackdropDown = false;
        // Bump seq so any in-flight IPC reply is dropped.
        _popoverRequestSeq++;
    }

    private void OnPopoverBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only the backdrop itself — clicks on the chrome subtree should
        // not arm dismiss. e.Source is the chrome root when click hits any
        // chrome descendant; only e.OriginalSource is the actual hit.
        // Comparing to PopoverChrome: chrome subtree clicks have Source =
        // PopoverChrome (or a descendant); backdrop clicks have Source =
        // PopoverOverlay.
        if (ReferenceEquals(e.Source, PopoverOverlay))
        {
            _popoverBackdropDown = true;
        }
    }

    private void OnPopoverBackdropMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_popoverBackdropDown) return;
        _popoverBackdropDown = false;

        if (!ReferenceEquals(e.Source, PopoverOverlay))
        {
            // MouseDown on backdrop, MouseUp on chrome — drag-into-chrome.
            // Phase 1 lesson: the pair didn't match, don't dismiss.
            return;
        }

        // Resolve the dismiss-click against the chart. If it lands on a
        // chart bucket, kick off a fresh popover at that location (open
        // question #7 — clicks on the chart while a popover is open
        // should spawn a new associated popover, not just dismiss).
        var px = e.GetPosition(HistoryChart);
        DismissPopover();
        _ = HandleChartClickAsync(px);
    }

    private FrameworkElement BuildPopoverChromeContent(ResolvedClick resolved, AppListResult result)
    {
        const int TopN = 5;

        // Canonical metal-recipe card surface (memory:
        // project_canonical_card_treatment). Theme-flippable properties
        // (Background / BorderBrush / Effect) use SetResourceReference, not
        // static FindResource, so the chrome tracks runtime theme switches
        // even if a popover is open across the flip. Same fix as the
        // flyout chrome builders on PerAppPage / AppDetailPage.
        var chrome = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("radius.card"),
            Padding = new Thickness(16),
            MinWidth = 240,
            MaxWidth = 360,
            UseLayoutRounding = true,
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "metal.card");
        chrome.SetResourceReference(Border.BorderBrushProperty, "border.card");
        chrome.SetResourceReference(UIElement.EffectProperty, "shadow.card");

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        chrome.Child = stack;

        // Header: time range disclosing the popover window (open question
        // #1 confirmed — bucket-center anchor + window-range header copy).
        var header = new System.Windows.Controls.TextBlock
        {
            Text = FormatPopoverHeaderTimeRange(resolved.PopoverWindow),
            Style = (Style)FindResource("text.eyebrow"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        stack.Children.Add(header);

        var top = result.Apps.Take(TopN).ToList();
        foreach (var app in top)
        {
            stack.Children.Add(BuildPopoverAppRow(app, resolved));
        }

        var remainder = result.Apps.Count - TopN;
        if (remainder > 0)
        {
            stack.Children.Add(BuildPopoverMoreRow(remainder, resolved));
        }

        return chrome;
    }

    /// <summary>
    /// Build one talker row with the canonical drill affordance: hover
    /// chevron + hand cursor + single click (memory:
    /// feedback_drill_grid_pattern). Hover state uses the project's
    /// canonical row-hover token (<c>surface.subtle</c>, same as the
    /// DataGrid RowStyle's IsMouseOver overlay in
    /// <c>DesignTokens.xaml:713</c>) so the popover's hover affordance
    /// matches every other clickable-row surface in the app.
    /// </summary>
    private FrameworkElement BuildPopoverAppRow(AppListEntry app, ResolvedClick resolved)
    {
        var row = new Grid
        {
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 2, 0, 0),
            Background = Brushes.Transparent, // make whole row hit-testable
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new System.Windows.Controls.TextBlock
        {
            Text = app.ImageName,
            Style = (Style)FindResource("text.body"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 4, 0, 4),
        };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var rate = new System.Windows.Controls.TextBlock
        {
            Text = FormatRateForPopover(app.BytesUp + app.BytesDown, resolved),
            Style = (Style)FindResource("text.mono"),
            Margin = new Thickness(16, 4, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rate, 1);
        row.Children.Add(rate);

        // Hover-only drill chevron — telegraphs single-click drill semantics
        // (talker row → AppDetailPage). Opacity, not Visibility, so the
        // row's measured width doesn't jitter on hover.
        var chevron = new SymbolIcon
        {
            Symbol = SymbolRegular.ChevronRight20,
            FontSize = 14,
            Opacity = 0,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(Control.ForegroundProperty, "text.tertiary");
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);

        WireRowHover(row, chevron);
        row.MouseLeftButtonUp += (_, _) => OnPopoverAppRowClicked(app, resolved);
        return row;
    }

    /// <summary>
    /// "+N more" row — same hover pattern as the app rows but with
    /// <see cref="SymbolRegular.ArrowRight20"/> rather than the chevron,
    /// hinting at "navigate to another surface" rather than "expand record."
    /// </summary>
    private FrameworkElement BuildPopoverMoreRow(int remainder, ResolvedClick resolved)
    {
        var row = new Grid
        {
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 8, 0, 0),
            Background = Brushes.Transparent,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new System.Windows.Controls.TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"+{remainder} more"),
            Style = (Style)FindResource("text.caption"),
            Margin = new Thickness(6, 4, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "accent.text");
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var arrow = new SymbolIcon
        {
            Symbol = SymbolRegular.ArrowRight20,
            FontSize = 14,
            Opacity = 0,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrow.SetResourceReference(Control.ForegroundProperty, "accent.text");
        Grid.SetColumn(arrow, 1);
        row.Children.Add(arrow);

        WireRowHover(row, arrow);
        row.MouseLeftButtonUp += (_, _) => OnPopoverMoreClicked(resolved);
        return row;
    }

    /// <summary>
    /// Apply the canonical hover treatment to a popover row: subtle
    /// background tint (<c>surface.subtle</c>, matching the DataGrid
    /// RowStyle hover overlay) + chevron/arrow opacity fade. Uses
    /// SetResourceReference so the tint tracks live theme switches.
    /// </summary>
    private static void WireRowHover(Grid row, UIElement chevron)
    {
        row.MouseEnter += (_, _) =>
        {
            row.SetResourceReference(Panel.BackgroundProperty, "surface.subtle");
            chevron.Opacity = 1;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            chevron.Opacity = 0;
        };
    }

    private void OnPopoverAppRowClicked(AppListEntry app, ResolvedClick resolved)
    {
        // Talker row → AppDetailPage with the popover's fixed window. Phase 1
        // added Window?: QueryWindow to AppDetailNavParams as an additive,
        // trailing-optional positional so the legacy Reports→AppDetail
        // (AppId, Date) drill keeps working. Date stays null on this path —
        // AppDetail's chrome-row date override would otherwise stomp Window.
        var nav = PerAppPage.FindNavigationView(this);
        var window = resolved.PopoverWindow;
        DismissPopover();
        nav?.Navigate(typeof(AppDetailPage), new AppDetailNavParams(app.AppId, Date: null, Window: window));
    }

    private void OnPopoverMoreClicked(ResolvedClick resolved)
    {
        // "+N more" → PerAppPage with the popover's fixed window so the
        // user sees the FULL ranked list, not a top-N. Preserves the
        // discovery-over-ranking invariant: top-5 is a surfacing convenience;
        // the deep-link is the unfiltered source of truth (memory:
        // project_discovery_principle).
        var nav = PerAppPage.FindNavigationView(this);
        var window = resolved.PopoverWindow;
        DismissPopover();
        nav?.Navigate(typeof(PerAppPage), new PerAppNavParams(window));
    }

    /// <summary>
    /// Position PopoverChrome at the visual bucket-center pixel, clamped to
    /// the chart card bounds. Per open question #1 (confirmed), anchor is
    /// the bucket center, not the click position — popover represents the
    /// BUCKET and the click position is incidental.
    /// </summary>
    private void PositionPopover(ResolvedClick resolved)
    {
        // Bucket center in chart data coords → chart pixel coords.
        var centerTicks = resolved.VisualBucketStartTicks + resolved.VisualBucketSpanTicks / 2;
        var centerPx = HistoryChart.ScaleDataToPixels(new LvcPointD((double)centerTicks, 0));

        // PopoverChrome must be measured before its DesiredSize is meaningful.
        PopoverChrome.Measure(new Size(
            double.IsFinite(PopoverOverlay.ActualWidth) ? PopoverOverlay.ActualWidth : 360,
            double.IsFinite(PopoverOverlay.ActualHeight) ? PopoverOverlay.ActualHeight : double.PositiveInfinity));
        var chromeW = PopoverChrome.DesiredSize.Width;
        var chromeH = PopoverChrome.DesiredSize.Height;
        var overlayW = PopoverOverlay.ActualWidth;
        var overlayH = PopoverOverlay.ActualHeight;

        // X: center horizontally on bucket-center pixel, clamp to overlay bounds.
        var x = centerPx.X - chromeW / 2;
        x = Math.Max(0, Math.Min(overlayW - chromeW, x));

        // Y: top-anchored inside the chart card (below the legend strip).
        // Refinement for click-relative Y / flip-up below if not enough
        // room is deferred to Slice 3 polish if the manual gate demands it.
        var y = 12.0;
        if (y + chromeH > overlayH) y = Math.Max(0, overlayH - chromeH);

        // Math.Round per Phase 1 sub-pixel positioning discipline.
        PopoverChrome.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
    }

    // -- popover formatting helpers ---------------------------------------

    /// <summary>
    /// Format a popover row's rate. Divisor math lives on
    /// <see cref="ResolvedClick.BytesPerGrainUnit"/> (single source of
    /// truth; pure-tested in <c>ChartClickResolverTests</c>); this method
    /// only handles formatting + unit suffix.
    /// </summary>
    private static string FormatRateForPopover(long bytes, ResolvedClick resolved)
    {
        return PerAppPage.FormatBytes(resolved.BytesPerGrainUnit(bytes)) + PerGrainSuffix(resolved.Grain);
    }

    private static string PerGrainSuffix(TrafficGrain grain) => grain switch
    {
        TrafficGrain.Hourly => "/hr",
        TrafficGrain.Daily => "/day",
        _ => "/min",
    };

    /// <summary>
    /// Confirmed format (open question, "your defaults"): time-range
    /// disclosing the popover window. Sub-day windows render time-only
    /// (<c>"16:42 – 16:48"</c>); day-grain windows render date-only
    /// (<c>"Jun 23 – Jun 24"</c>). Plain range copy — duration is implicit
    /// and the range is self-documenting.
    /// </summary>
    private static string FormatPopoverHeaderTimeRange(QueryWindow window)
    {
        var fromLocal = DateTimeOffset.FromUnixTimeMilliseconds(window.FromUnixMs).LocalDateTime;
        var toLocal = DateTimeOffset.FromUnixTimeMilliseconds(window.ToUnixMs).LocalDateTime;
        var isDayGrain = window.SpanMs >= 86_400_000L; // ≥ 1 day
        var fmt = isDayGrain ? "MMM d" : "HH:mm";
        var from = fromLocal.ToString(fmt, CultureInfo.InvariantCulture);
        var to = toLocal.ToString(fmt, CultureInfo.InvariantCulture);
        return $"{from} – {to}";
    }
}
