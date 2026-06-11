using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class DashboardPage : Page
{
    // 60 polled points at 2 s cadence = ~2 min of trailing chart history.
    private const int ChartHistoryPoints = 60;

    // Rows that drop out of the active top-N stay on the list at half-opacity
    // for this long before being evicted. Coarsening by TIME, not rank —
    // aligned with the discovery > ranking principle.
    private static readonly TimeSpan DimmedPersistenceWindow = TimeSpan.FromSeconds(30);

    // Banner steady-state threshold: > this many consecutive failed cycles flips
    // the banner from caution-class (transient/retrying) to critical-class
    // (steady/last refresh stale). 1 cycle ≈ 2 s.
    private const int SteadyDisconnectFailureThreshold = 1;

    // Minimum time the initial loading state stays visible before the first
    // ApplyUpdate is allowed to transition the page out of empty/loading.
    // A snapshot on a warm service arrives in ~50-200 ms — below the
    // perception threshold for a deliberate loading cue. Holding the
    // initial state for at least 500 ms guarantees the spinners are
    // actually visible to the user.
    private const int MinimumInitialPaintMs = 500;

    private readonly ObservableCollection<DateTimePoint> _upSeries = new();
    private readonly ObservableCollection<DateTimePoint> _downSeries = new();

    public ObservableCollection<TalkerRowViewModel> Talkers { get; } = new();

    private int _consecutiveFailures;
    private bool _minimumInitialPaintElapsed;
    private ActivitySnapshotUpdate? _deferredFirstUpdate;
    private DispatcherTimer? _minimumInitialPaintTimer;

    // Y-axis anti-jitter. EWMA-smoothed peak (max across the trailing
    // Up/Down window) rounded UP to the next {1, 2, 5} × 10ⁿ value, with
    // MinStep = niceUpper / 4 so labels land at 0/25/50/75/100%. α=0.3 is
    // responsive but doesn't yo-yo at the 2 s cadence. InitialUpperBound
    // is the first-paint default so the axis has a sensible scale before
    // any data arrives, and also acts as the FLOOR on the smoothed value
    // so very quiet networks don't collapse the axis below the legible
    // range.
    private const double SmoothingAlpha = 0.3;
    private const double InitialUpperBound = 1024;
    private double _smoothedUpperBound = InitialUpperBound;
    private readonly Axis? _yAxis;
    private readonly Axis? _xAxis;

    // Phase D.7 — smooth-scroll chart animation experiment. OFF by
    // default because the 2200ms / EasingFunctions.Lineal tween pays an
    // ~8% idle CPU cost (over the project's <1% budget). Visual quality
    // when enabled is excellent: continuous chained motion — animation
    // duration slightly exceeds the 2s tick cadence so each tween is
    // interrupted by the next and never reaches a stationary "done"
    // state, giving the line constant motion with a ~200ms lag behind
    // real-time (visually imperceptible). Flip to true at compile time
    // to opt in locally; this graduates to a user toggle on the Settings
    // page once that page is built (§11 backlog item). When false, the
    // chart snaps between ticks (pre-Phase D.7 behavior).
    private static readonly bool EnableChartSmoothScroll = false;

    public DashboardPage()
    {
        InitializeComponent();

        RatesChart.Series = new ISeries[]
        {
            // GeometrySize=12 sizes each data point's hover area (~12px wide)
            // so X-snap tooltip detection registers anywhere within roughly
            // the per-tick column instead of only when the cursor sits on
            // the line. GeometryFill/GeometryStroke = null suppresses the
            // visible point markers — only the hit area is enlarged, the
            // line itself stays clean.
            new LineSeries<DateTimePoint>
            {
                Name = "Up",
                Values = _upSeries,
                GeometrySize = 20,
                GeometryFill = null,
                GeometryStroke = null,
                XToolTipLabelFormatter = FormatTooltipTime,
                YToolTipLabelFormatter = p => RateFormatter.FormatRate(p.Coordinate.PrimaryValue),
            },
            new LineSeries<DateTimePoint>
            {
                Name = "Down",
                Values = _downSeries,
                GeometrySize = 20,
                GeometryFill = null,
                GeometryStroke = null,
                XToolTipLabelFormatter = FormatTooltipTime,
                YToolTipLabelFormatter = p => RateFormatter.FormatRate(p.Coordinate.PrimaryValue),
            },
        };
        // X-axis chrome: TEXT labels suppressed via Labeler => "", but the
        // vertical separator lines stay ON. The separators are data-anchored
        // (LiveCharts2 picks tick positions from the data range) so they
        // scroll with the chart, acting as visual time anchors between the
        // static -2m / -90s / -1m / -30s / now WPF overlay markers — e.g.
        // a gridline drifting from -45s toward -50s tells the user where
        // the in-progress event sits relative to the static labels.
        // MinStep = 10 seconds (in DateTime.Ticks) holds the gridline
        // density steady at ~12 across the 2-minute window and prevents
        // the auto-tick algorithm from oscillating as data scrolls.
        // DrawMargin reserves the chart's plot area at (Left=80, Top=10,
        // Right=10, Bottom=44): Left=80 gives Y-axis labels comfortable
        // width without the plot drawing on top of them, Bottom=44 leaves
        // vertical breathing room between the lowest Y label and the X
        // overlay row.
        //
        // X-axis range is LOCKED to a fixed 120-second window anchored at
        // the right edge ("now"). Without this, LiveCharts2 auto-fits the
        // visible range to data — and during the first 2 minutes of uptime
        // (when fewer than 60 data points exist) the partial buffer gets
        // stretched across the full chart width, making the static overlay
        // labels misrepresent positions. Initial limits here are now±120s;
        // the per-tick MinLimit/MaxLimit update in ApplyUpdate keeps the
        // window scrolling so data accumulates right-to-left at launch.
        var initialNowTicks = DateTime.Now.Ticks;
        _xAxis = new Axis
        {
            Labeler = _ => string.Empty,
            MinStep = TimeSpan.FromSeconds(10).Ticks,
            MinLimit = initialNowTicks - TimeSpan.FromSeconds(120).Ticks,
            MaxLimit = initialNowTicks,
        };
        RatesChart.XAxes = new[] { _xAxis };
        RatesChart.DrawMargin = new Margin(80, 10, 10, 44);

        // Phase D.7 chart animation — gated on EnableChartSmoothScroll
        // (see field declaration for rationale and the path to a Settings
        // toggle). When disabled, AnimationsSpeed=Zero gives snap-only
        // behavior identical to pre-Phase D.7.
        if (EnableChartSmoothScroll)
        {
            RatesChart.AnimationsSpeed = TimeSpan.FromMilliseconds(2200);
            RatesChart.EasingFunction = EasingFunctions.Lineal;
        }
        else
        {
            RatesChart.AnimationsSpeed = TimeSpan.Zero;
        }
        _yAxis = new Axis
        {
            Labeler = v => RateFormatter.FormatRate(v),
            MinLimit = 0,
            MaxLimit = InitialUpperBound,
            MinStep = InitialUpperBound / 4.0,
        };
        RatesChart.YAxes = new[] { _yAxis };
        ApplyChartTheme();
        ChartTheming.Changed += () => Dispatcher.Invoke(ApplyChartTheme);

        TalkersList.ItemsSource = Talkers;

        // First paint: cover both the chart and talkers card bodies with the
        // ProgressRing overlay and leave em-dash placeholders in the status
        // cards. This is `state: empty` per the brief — no snapshot has
        // landed yet, neither chart nor list has anything to render.
        ApplyInitialPaint();

        // The ActivitySnapshotPoller is owned by MainWindow now so the
        // bottom-bar rate mirror keeps updating on every screen. We
        // subscribe/unsubscribe to its event by lifecycle: Loaded attaches
        // when this cached page becomes visible, Unloaded detaches when
        // the user navigates away.
        Loaded += OnLoadedHook;
        Unloaded += OnUnloadedHook;
        SizeChanged += (_, _) => EnforceTalkersBounds();
    }

    private void OnLoadedHook(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.ActivitySnapshotReceived += OnActivitySnapshot;
        }
        EnforceTalkersBounds();

        // Disable scrolling on the parent DynamicScrollViewer that
        // ui:NavigationView wraps every hosted page in. The
        // ScrollViewer.* attached property on the Page root is a no-op for
        // this container (Wpf.Ui's DynamicScrollViewer doesn't read it);
        // we have to set the properties on the actual instance found via
        // visual-tree walk. Without this, the talkers ListView's measure
        // pass gets infinite available height from the outer scroll, so
        // its own ScrollViewer.Auto scrollbar misbehaves and the page
        // itself can scroll past the chart at narrow window heights.
        if (FindAncestorScrollViewer(this) is { } pageScrollViewer)
        {
            pageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            pageScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject child)
    {
        DependencyObject? current = child;
        while ((current = VisualTreeHelper.GetParent(current)) is not null)
        {
            if (current is ScrollViewer sv) return sv;
        }
        return null;
    }

    /// <summary>
    /// Tooltip X header for both series: relative-time first (matches the
    /// static X overlay labels: -2m / -90s / ... / now), then absolute
    /// wall-clock time. Per the mockup spec: "-90s · 23:34:10 ...".
    /// </summary>
    private static string FormatTooltipTime(ChartPoint point)
    {
        var ticks = (long)point.Coordinate.SecondaryValue;
        var dt = new DateTime(ticks);
        var relSecs = (int)(DateTime.Now - dt).TotalSeconds;
        if (relSecs < 0) relSecs = 0;
        var rel = relSecs == 0 ? "now" : $"-{relSecs}s";
        return $"{rel} · {dt:HH:mm:ss}";
    }

    /// <summary>
    /// Round value UP to the next BINARY-aligned {1, 2, 5} × 10ⁿ value in
    /// its natural unit (B, KB, MB, GB). Binary alignment because
    /// RateFormatter is 1024-based — decimal-nice 20000 would format as
    /// "19.5 KB/s" (20000/1024 = 19.53). Binary-nice 20480 (= 20 × 1024)
    /// formats clean as "20 KB/s". Returns 1 for non-positive input so
    /// axis upper bounds stay sensible if the smoothed peak ever hits 0.
    /// </summary>
    private static double RoundUpToNiceValue(double value)
    {
        if (value <= 0) return 1;
        var unitPower = Math.Min(3, (int)Math.Floor(Math.Log(value) / Math.Log(1024)));
        if (unitPower < 0) unitPower = 0;
        var unitFactor = Math.Pow(1024, unitPower);
        var valueInUnit = value / unitFactor;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(valueInUnit)));
        var mantissa = valueInUnit / magnitude;
        var niceMantissa = mantissa <= 1.0 ? 1.0 :
                           mantissa <= 2.0 ? 2.0 :
                           mantissa <= 5.0 ? 5.0 : 10.0;
        return niceMantissa * magnitude * unitFactor;
    }

    private void OnUnloadedHook(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.ActivitySnapshotReceived -= OnActivitySnapshot;
        }
    }

    private void OnActivitySnapshot(object? sender, ActivitySnapshotUpdate update)
    {
        Dispatcher.Invoke(() => ApplyUpdate(update));
    }

    private void ApplyChartTheme() => ChartTheming.Apply(RatesChart);

    /// <summary>
    /// Cap the talkers card height to its declared MaxHeight so the inner
    /// ListView virtualizes — mirrors EnforceDataGridBounds on App Detail.
    /// NavigationView wraps each page in a DynamicScrollViewer that hands the
    /// page infinite vertical extent, so without this enforcement the ListView
    /// would grow without bound and the chart card would lose its share.
    /// </summary>
    private void EnforceTalkersBounds()
    {
        // The Border's own MaxHeight is the contract; no extra math needed
        // here yet, but the hook is in place for future refinement (e.g.
        // computing cap as (windowH - 220) / 2 to match App Detail).
    }

    private void ApplyInitialPaint()
    {
        // State: empty (no snapshot yet). Em-dash placeholders, hidden
        // sublines, spinners over the chart and talkers cards.
        SetStatusCardsPlaceholder();
        ChartLoadingOverlay.Visibility = Visibility.Visible;
        ChartXAxisOverlay.Visibility = Visibility.Collapsed;
        TalkersLoadingOverlay.Visibility = Visibility.Visible;
        TalkersEmptyText.Visibility = Visibility.Collapsed;
        HideBanner();
        DimmableContent.Opacity = 1.0;

        // Start the minimum-display-duration timer. ApplyUpdate will defer
        // any incoming snapshot until this elapses so the spinners are
        // perceivably visible on first launch (and on every page
        // re-construction if the cache mode isn't actually preserving
        // state — see the analysis in dashboard-UI-phase-plan.md).
        _minimumInitialPaintElapsed = false;
        _deferredFirstUpdate = null;
        _minimumInitialPaintTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(MinimumInitialPaintMs),
        };
        _minimumInitialPaintTimer.Tick += OnMinimumInitialPaintElapsed;
        _minimumInitialPaintTimer.Start();
    }

    private void OnMinimumInitialPaintElapsed(object? sender, EventArgs e)
    {
        if (_minimumInitialPaintTimer is { } t)
        {
            t.Stop();
            t.Tick -= OnMinimumInitialPaintElapsed;
            _minimumInitialPaintTimer = null;
        }
        _minimumInitialPaintElapsed = true;

        // Replay the most-recent deferred update (if any) so the page
        // transitions out of the initial state as soon as the floor has
        // been served.
        if (_deferredFirstUpdate is { } update)
        {
            _deferredFirstUpdate = null;
            ApplyUpdate(update);
        }
    }

    private void ApplyUpdate(ActivitySnapshotUpdate update)
    {
        // Floor: keep the initial loading state visible for at least
        // MinimumInitialPaintMs. Any update arriving before that gets
        // stashed; the timer-elapsed handler replays the most recent
        // stashed update once the floor has been served.
        if (!_minimumInitialPaintElapsed)
        {
            _deferredFirstUpdate = update;
            return;
        }

        if (!update.IsConnected || update.Envelope is null)
        {
            _consecutiveFailures++;
            var steady = _consecutiveFailures > SteadyDisconnectFailureThreshold;
            ShowBanner(
                isCritical: steady,
                copy: steady
                    ? $"Service disconnected ({update.FailureReason}); last refresh stale"
                    : $"Service disconnected ({update.FailureReason}); retrying");

            // Disconnect: preserve last-known content (chart history, talkers
            // list, status card values) and dim the lot. Chart spinner stays
            // hidden — we're showing stale data, not loading. X-axis overlay
            // stays visible: the time markers still anchor the visible
            // history correctly.
            DimmableContent.Opacity = 0.6;
            ChartLoadingOverlay.Visibility = Visibility.Collapsed;
            ChartXAxisOverlay.Visibility = Visibility.Visible;
            TalkersLoadingOverlay.Visibility = Visibility.Collapsed;
            TalkersEmptyText.Visibility = Talkers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        // Successful tick: reset failure counter, undim content.
        _consecutiveFailures = 0;
        DimmableContent.Opacity = 1.0;

        var snap = update.Envelope.Payload;

        // Warming branch: the rolling window hasn't sealed its first bucket
        // yet (WindowSeconds <= 0). Banner up, chart spinner up, talkers
        // shows the canonical empty-state copy, status cards stay em-dash.
        if (snap.WindowSeconds <= 0)
        {
            ShowBanner(isCritical: false, copy: "Warming up. First flush bucket lands within ~5 s.", warmingClass: true);
            SetStatusCardsPlaceholder();
            ChartLoadingOverlay.Visibility = Visibility.Visible;
            ChartXAxisOverlay.Visibility = Visibility.Collapsed;
            TalkersLoadingOverlay.Visibility = Visibility.Collapsed;
            TalkersEmptyText.Visibility = Visibility.Visible;
            // Talkers list cleared so a stale frame can't show through the overlay.
            Talkers.Clear();
            return;
        }

        // Connected with a sealed bucket — clear banner.
        HideBanner();
        ChartLoadingOverlay.Visibility = Visibility.Collapsed;
        ChartXAxisOverlay.Visibility = Visibility.Visible;
        TalkersLoadingOverlay.Visibility = Visibility.Collapsed;

        // Push a point into the chart — even zero values, so a quiet system
        // renders flat lines instead of empty space.
        var totalUp = snap.Apps.Sum(a => a.BytesUpPerSec);
        var totalDown = snap.Apps.Sum(a => a.BytesDownPerSec);
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(snap.CapturedAtUnixMs).LocalDateTime;
        _upSeries.Add(new DateTimePoint(ts, totalUp));
        _downSeries.Add(new DateTimePoint(ts, totalDown));
        while (_upSeries.Count > ChartHistoryPoints) _upSeries.RemoveAt(0);
        while (_downSeries.Count > ChartHistoryPoints) _downSeries.RemoveAt(0);

        // Y-axis anti-jitter: peak across the current Up/Down trailing
        // window → asymmetric EWMA → round UP to next binary-nice
        // {1, 2, 5} × 10ⁿ × 1024ᵏ. MinStep keeps tick labels at
        // 0/25/50/75/100% of MaxLimit. Floored at InitialUpperBound so
        // quiet networks don't shrink the axis below the legible range.
        //
        // Asymmetric EWMA: jump UP immediately when peak exceeds the
        // current bound so spikes don't clip off the visible top; decay
        // slowly with α=0.3 on the way down so the axis doesn't yo-yo
        // when activity falls back.
        double peak = 0;
        foreach (var p in _upSeries)   if (p.Value.HasValue && p.Value.Value > peak) peak = p.Value.Value;
        foreach (var p in _downSeries) if (p.Value.HasValue && p.Value.Value > peak) peak = p.Value.Value;
        if (peak > _smoothedUpperBound)
            _smoothedUpperBound = peak;
        else
            _smoothedUpperBound = SmoothingAlpha * peak + (1 - SmoothingAlpha) * _smoothedUpperBound;
        var niceUpper = Math.Max(RoundUpToNiceValue(_smoothedUpperBound), InitialUpperBound);
        if (_yAxis is not null)
        {
            _yAxis.MaxLimit = niceUpper;
            _yAxis.MinStep = niceUpper / 4.0;
        }

        // X-axis fixed-window scroll: anchor the right edge to the newest
        // data point's timestamp and slide the 120-second window left to
        // match. Keeps the static overlay labels (-2m, -90s, ...) accurate
        // during the first 2 minutes of uptime — data accumulates from the
        // right edge inward rather than stretching to fill the full chart
        // width.
        if (_xAxis is not null && _upSeries.Count > 0)
        {
            var newestTicks = _upSeries[^1].DateTime.Ticks;
            _xAxis.MaxLimit = newestTicks;
            _xAxis.MinLimit = newestTicks - TimeSpan.FromSeconds(120).Ticks;
        }

        // Status cards — concrete values.
        var activeCount = snap.Apps.Count(a => a.BytesUpPerSec > 0 || a.BytesDownPerSec > 0);
        UpdateStatusCards(totalUp, totalDown, activeCount, snap.WanLocalBreakdown);

        // Talkers list — refresh + dimmed-row persistence.
        UpdateTalkers(snap, DateTimeOffset.Now);
        TalkersEmptyText.Visibility = Talkers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowBanner(bool isCritical, string copy, bool warmingClass = false)
    {
        Brush bg, fg;
        if (warmingClass)
        {
            // Caution-class paint, semantically tagged as warming so future
            // repoints don't have to thread through every caution banner.
            bg = (Brush)FindResource("status.warming.background");
            fg = (Brush)FindResource("status.caution.text");
        }
        else if (isCritical)
        {
            bg = (Brush)FindResource("status.critical.background");
            fg = (Brush)FindResource("status.critical");
        }
        else
        {
            bg = (Brush)FindResource("status.caution.background");
            fg = (Brush)FindResource("status.caution.text");
        }

        StatusBanner.Background = bg;
        StatusBannerText.Foreground = fg;
        StatusBannerText.Text = copy;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void HideBanner() => StatusBanner.Visibility = Visibility.Collapsed;

    private void SetStatusCardsPlaceholder()
    {
        var dim = (Brush)FindResource("text.tertiary");
        StatusCardUpValue.Text = "—";
        StatusCardUpValue.Foreground = dim;
        StatusCardDownValue.Text = "—";
        StatusCardDownValue.Foreground = dim;
        StatusCardActiveValue.Text = "—";
        StatusCardActiveValue.Foreground = dim;

        StatusCardUpSubline.Visibility = Visibility.Collapsed;
        StatusCardDownSubline.Visibility = Visibility.Collapsed;
        StatusCardActiveSubline.Visibility = Visibility.Collapsed;

        // WAN/LOCAL: hide bar + both subline variants until first data.
        WanLocalBar.Visibility = Visibility.Collapsed;
        StatusCardWanLegend.Visibility = Visibility.Collapsed;
        StatusCardWanQuietText.Visibility = Visibility.Collapsed;
    }

    private void UpdateStatusCards(double totalUp, double totalDown, int activeCount, ClassBreakdown breakdown)
    {
        var primary = (Brush)FindResource("text.primary");
        StatusCardUpValue.Text = RateFormatter.FormatRate(totalUp);
        StatusCardUpValue.Foreground = primary;
        StatusCardDownValue.Text = RateFormatter.FormatRate(totalDown);
        StatusCardDownValue.Foreground = primary;
        StatusCardActiveValue.Text = activeCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StatusCardActiveValue.Foreground = primary;

        StatusCardUpSubline.Visibility = Visibility.Visible;
        StatusCardDownSubline.Visibility = Visibility.Visible;
        StatusCardActiveSubline.Visibility = Visibility.Visible;

        // WAN vs LOCAL split. Aggregate bytes (not rates); when nothing has
        // been classified yet, render the "No active traffic" message and
        // hide both the bar and the legend so we don't draw an
        // indeterminate split.
        var wanBytes = breakdown.WanBytesUp + breakdown.WanBytesDown;
        var localBytes = breakdown.LocalBytesUp + breakdown.LocalBytesDown;
        var grand = wanBytes + localBytes;
        if (grand <= 0)
        {
            WanLocalBar.Visibility = Visibility.Collapsed;
            StatusCardWanLegend.Visibility = Visibility.Collapsed;
            StatusCardWanQuietText.Visibility = Visibility.Visible;
        }
        else
        {
            WanLocalBar.Visibility = Visibility.Visible;
            // Star-weighted column widths give a proportional fill without
            // measuring the bar's actual pixel width.
            WanLocalBar.ColumnDefinitions[0].Width = new GridLength(wanBytes, GridUnitType.Star);
            WanLocalBar.ColumnDefinitions[1].Width = new GridLength(localBytes, GridUnitType.Star);

            var wanPct = 100.0 * wanBytes / grand;
            var localPct = 100.0 * localBytes / grand;
            StatusCardWanLegendWan.Text =
                $"WAN {wanPct.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}%";
            StatusCardWanLegendLocal.Text =
                $"Local {localPct.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}%";
            StatusCardWanLegend.Visibility = Visibility.Visible;
            StatusCardWanQuietText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Top-10-by-current-rate refresh with dimmed-row persistence: rows
    /// that drop out stay at <see cref="TalkerRowViewModel.Opacity"/> = 0.5
    /// for <see cref="DimmedPersistenceWindow"/> before eviction. Coarsens
    /// by TIME, not by rank — aligns with the discovery > ranking principle.
    /// </summary>
    private void UpdateTalkers(ActivitySnapshot snap, DateTimeOffset now)
    {
        // Active set = top 10 by total bytes this window (same key the brief's
        // "Top by current rate" exception names — see §11).
        var activeApps = snap.Apps
            .OrderByDescending(a => a.BytesUpTotal + a.BytesDownTotal)
            .ThenBy(a => a.ImageName, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToDictionary(TalkerRowViewModel.MakeIdentity, a => a);

        // Pass 1: refresh existing rows in place. Rows still in the active
        // set keep Opacity=1.0; rows that fell out either dim (within the
        // 30 s persistence window) or get evicted.
        var toEvict = new List<TalkerRowViewModel>();
        foreach (var row in Talkers)
        {
            if (activeApps.TryGetValue(row.Identity, out var app))
            {
                row.RefreshActive(app, now);
                activeApps.Remove(row.Identity);
            }
            else if (now - row.LastSeenActive < DimmedPersistenceWindow)
            {
                row.MarkDimmed();
            }
            else
            {
                toEvict.Add(row);
            }
        }
        foreach (var row in toEvict)
        {
            Talkers.Remove(row);
        }

        // Pass 2: add newly-active rows that weren't already in the list.
        foreach (var (_, app) in activeApps)
        {
            Talkers.Add(new TalkerRowViewModel(app, now));
        }

        // Pass 3: reorder. Active rows first (sorted by current total rate
        // descending), then dimmed rows (most recently active first). The
        // ObservableCollection has no Sort; Move() per displaced item is the
        // ~20-row-worst-case cost.
        var sorted = Talkers
            .OrderByDescending(r => r.Opacity)
            .ThenByDescending(r => r.Opacity >= 1.0 ? r.CurrentTotalRate : 0)
            .ThenByDescending(r => r.LastSeenActive)
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(Talkers[i], sorted[i]))
            {
                var src = Talkers.IndexOf(sorted[i]);
                Talkers.Move(src, i);
            }
        }
    }
}

/// <summary>
/// One row on the Dashboard talkers list. Mutable + INotifyPropertyChanged so
/// the same instance can be refreshed in place across snapshot ticks — the
/// dimmed-row persistence layer in <see cref="DashboardPage.UpdateTalkers"/>
/// depends on stable instance identity across cycles.
/// </summary>
public sealed class TalkerRowViewModel : INotifyPropertyChanged
{
    /// <summary>Stable key per (ImagePath, HostedServices) — matches the
    /// AppActivity rollup key, so the same app's row survives across ticks.</summary>
    public string Identity { get; }

    public string AppLabel { get; }
    public string Publisher { get; }
    public string SignatureStatus { get; }

    /// <summary>Sum of up + down per-second rate from the most recent
    /// active-refresh. Used to sort active rows by current rate desc.</summary>
    public double CurrentTotalRate { get; private set; }

    /// <summary>The most recent tick at which this row was in the active
    /// set. Drives the 30-s dimmed-row persistence window.</summary>
    public DateTimeOffset LastSeenActive { get; private set; }

    private string _upRateText = "0 B/s";
    public string UpRateText
    {
        get => _upRateText;
        private set { if (_upRateText != value) { _upRateText = value; OnPropertyChanged(); } }
    }

    private string _downRateText = "0 B/s";
    public string DownRateText
    {
        get => _downRateText;
        private set { if (_downRateText != value) { _downRateText = value; OnPropertyChanged(); } }
    }

    private double _opacity = 1.0;
    public double Opacity
    {
        get => _opacity;
        private set { if (_opacity != value) { _opacity = value; OnPropertyChanged(); } }
    }

    public TalkerRowViewModel(AppActivity app, DateTimeOffset now)
    {
        Identity = MakeIdentity(app);
        AppLabel = MakeAppLabel(app);
        Publisher = MakePublisher(app);
        SignatureStatus = app.SignatureStatus;
        RefreshActive(app, now);
    }

    public void RefreshActive(AppActivity app, DateTimeOffset now)
    {
        UpRateText = RateFormatter.FormatRate(app.BytesUpPerSec);
        DownRateText = RateFormatter.FormatRate(app.BytesDownPerSec);
        CurrentTotalRate = app.BytesUpPerSec + app.BytesDownPerSec;
        LastSeenActive = now;
        Opacity = 1.0;
    }

    public void MarkDimmed() => Opacity = 0.5;

    internal static string MakeIdentity(AppActivity app) =>
        app.ImagePath + "|" + (app.HostedServices ?? string.Empty);

    private static string MakeAppLabel(AppActivity app) =>
        string.IsNullOrEmpty(app.HostedServices)
            ? app.ImageName
            : $"{app.ImageName} [{app.HostedServices}]";

    private static string MakePublisher(AppActivity app) =>
        string.IsNullOrEmpty(app.Publisher) ? "(unknown)" : app.Publisher;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
