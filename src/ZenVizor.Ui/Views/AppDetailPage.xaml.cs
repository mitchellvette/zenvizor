using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class AppDetailPage : Page
{
    private readonly HistoryQueryClient _client = new();
    private readonly DispatcherTimer _toastTimer;

    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = new();
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    public int? AppId { get; private set; }

    // Latest detail summary, retained so RefreshTrustLine can re-classify after
    // a Connections refresh changes the has-WAN signal (the alert combination
    // is computed from both surfaces).
    private AppListEntry? _lastSummary;

    // Chart axes — created ONCE in the ctor and mutated per refresh
    // (UpdateAxesForGrain assigns fresh Labeler / MinStep / UnitWidth values).
    // We deliberately do NOT reassign SeriesChart.XAxes / YAxes arrays after
    // construction — wholesale axis-array replacement combined with same-frame
    // Series reassignment leaves LiveCharts2's internal layout state in an
    // inconsistent state and the chart fails to render at all.
    // (Phase 1's pattern was axes-once-then-mutate; Phase 3's first attempt
    // tried replacement-per-refresh and the chart pane went blank.)
    private readonly Axis _xAxis;
    private readonly Axis _yAxis;

    public AppDetailPage()
    {
        InitializeComponent();

        // ItemTemplate (binds Short, ToolTip=Label) is set in XAML — do NOT
        // also assign DisplayMemberPath here. ItemTemplate and DisplayMemberPath
        // are mutually exclusive on ItemsControl; setting both throws
        // InvalidOperationException at runtime.
        WindowCombo.ItemsSource = WindowPreset.All;
        WindowCombo.SelectedIndex = 1;

        // Toast auto-dismiss timer: ~1.5s after ShowCopiedToast() flips the
        // banner Visible, the Tick callback flips it back. Restarting the
        // timer on subsequent copies extends the visible window rather than
        // queueing additional toasts.
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBanner.Visibility = Visibility.Collapsed;
        };

        // Axes created ONCE here and mutated per refresh below in
        // UpdateAxesForGrain. Initial labelers / step / unit-width default to
        // Samples grain so the chart isn't blank between ctor and the first
        // ApplyDetail. The axis INSTANCES persist for the page's lifetime —
        // SeriesChart.XAxes / YAxes are assigned exactly once, here.
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
        SeriesChart.XAxes = new[] { _xAxis };
        SeriesChart.YAxes = new[] { _yAxis };
        // Force the plot rectangle. Without this, LC2 auto-reserves a
        // horizontal band at the top for the legend (since
        // LegendPosition="Top"), pushing the plot area down by ~30-40px.
        // With Top=10 set explicitly, the legend overlays the top of the
        // plot instead of getting its own row — matches Dashboard's
        // RatesChart (DashboardPage.xaml.cs:146) and reclaims the lost
        // vertical canvas. Left=80 fits the widest realistic Y-axis label
        // ("500 GB/day" plus padding); Bottom=30 fits the X-axis label
        // strip (HH:mm / MM-dd HH / MM-dd are all ≤ ~20px tall).
        SeriesChart.DrawMargin = new Margin(80, 10, 10, 30);
        ApplyChartTheme();
        ChartTheming.Changed += () => Dispatcher.Invoke(ApplyChartTheme);

        ConnectionsGrid.ItemsSource = Connections;
        SessionsGrid.ItemsSource = Sessions;

        DataContextChanged += (_, _) => OnAppIdReceived();
        Loaded += async (_, _) =>
        {
            EnforceDataGridBounds();
            await RefreshAsync();
        };
        SizeChanged += (_, _) => EnforceDataGridBounds();
    }

    /// <summary>
    /// Cap each DataGrid's height at runtime so WPF row virtualization
    /// engages. Wpf.Ui's NavigationView hosts pages in a DynamicScrollViewer,
    /// which hands the page infinite vertical extent — without an explicit
    /// MaxHeight on the DataGrid the rows panel materializes every item
    /// (2000+ for chrome at 24h) instead of virtualizing.
    ///
    /// The 360 absolute cap above the <c>(window - 220) / 2</c> formula is
    /// part of the chart-hero layout in AppDetailPage.xaml: Row 5 is
    /// Auto-sized, so the grids row's measured desire equals this cap +
    /// chrome. Without the 360 ceiling, the formula scales linearly with
    /// window height (up to ~970 at 4K) and the grids row would outpace
    /// the chart row's residual share, putting grids visually on top of
    /// the chart at full screen. 360 keeps the grids visible row count at
    /// ~12 max — DataGrids scroll internally beyond that — while leaving
    /// the chart card to absorb all extra vertical real estate as the
    /// hero element. (Phase 4 merges the two grids into one tabbed card;
    /// re-tune the cap then.)
    /// </summary>
    private void EnforceDataGridBounds()
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        var cap = Math.Max(200, Math.Min(360, (window.ActualHeight - 220) / 2));
        ConnectionsGrid.MaxHeight = cap;
        SessionsGrid.MaxHeight = cap;
    }

    private void ApplyChartTheme() => ChartTheming.Apply(SeriesChart);

    /// <summary>
    /// Mutate the existing <see cref="_xAxis"/> / <see cref="_yAxis"/>
    /// instances in place with grain- and window-tailored Labeler, MinStep,
    /// and UnitWidth. Called from <see cref="ApplyDetail"/> as soon as the
    /// resolved grain is known.
    ///
    /// IMPORTANT: this method MUST NOT reassign <c>SeriesChart.XAxes</c> or
    /// <c>SeriesChart.YAxes</c>. Wholesale axis-array replacement in the same
    /// frame as a <c>Series</c> reassignment puts LC2 v2 into a state where
    /// the chart renders nothing. Phase 1's working pattern was axes-once-
    /// then-mutate; this method preserves that pattern.
    /// </summary>
    private void UpdateAxesForGrain(TrafficGrain grain, WindowPreset? preset)
    {
        _xAxis.Labeler   = ticks => ChartBuilder.FormatXAxisLabel((long)ticks, grain);
        _xAxis.MinStep   = ChartBuilder.MinStepFor(grain, preset);
        _xAxis.UnitWidth = ChartBuilder.UnitWidthFor(grain, preset);
        _yAxis.Labeler   = v => ChartBuilder.FormatYAxisLabel(v, grain);
    }

    private void OnAppIdReceived()
    {
        AppId = DataContext switch
        {
            int i => i,
            long l => (int)l,
            _ => null,
        };
        // Title shows the image name once ApplyDetail lands; before that we
        // show the page placeholder. AppId surfaces in the labeled chip
        // beside the title (Q4 lock — never inline in the title text).
        HeaderText.Text = "App detail";
        AppIdValue.Text = AppId is int id
            ? id.ToString(CultureInfo.InvariantCulture)
            : "—";
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        var nav = PerAppPage.FindNavigationView(this);
        nav?.GoBack();
    }

    // Phase 2.x — AppId chip and path row are now click-anywhere Borders
    // bound to MouseLeftButtonUp, not ui:Button.Click. The MouseButton event
    // delegate is MouseButtonEventHandler (MouseButtonEventArgs), not
    // RoutedEventHandler — handler signatures must match.

    private void OnCopyAppIdClick(object sender, MouseButtonEventArgs e)
    {
        if (AppId is not int id) return;
        TryCopyToClipboard(id.ToString(CultureInfo.InvariantCulture));
    }

    private void OnCopyPathClick(object sender, MouseButtonEventArgs e)
    {
        if (_lastSummary is { ImagePath: var path } && !string.IsNullOrEmpty(path))
        {
            TryCopyToClipboard(path);
        }
    }

    private void TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ShowCopiedToast();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard is OS-shared; another process holding the lock
            // surfaces as a COMException. Silently swallow — failing to
            // copy is recoverable (user can re-click), and we don't want
            // an unhandled-exception dialog over a copy button.
        }
    }

    /// <summary>
    /// Surface the "Copied to clipboard" toast and reset its auto-dismiss
    /// timer. Repeated calls within the timeout reset the visible window
    /// rather than stacking.
    /// </summary>
    private void ShowCopiedToast()
    {
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>
    /// Trust-line state classification. Computed after BOTH ApplyDetail and
    /// ApplyConnections have run so the alert combination — which depends on
    /// has-WAN-connection — sees the freshly loaded Connections list. Four
    /// mutually-exclusive Borders in the XAML; exactly one is Visible.
    /// </summary>
    private void RefreshTrustLine()
    {
        TrustAlertCombo.Visibility = Visibility.Collapsed;
        TrustSignedPersonal.Visibility = Visibility.Collapsed;
        TrustSignedSystem.Visibility = Visibility.Collapsed;
        TrustFallback.Visibility = Visibility.Collapsed;

        if (_lastSummary is not { } s) return;

        var hasWan = Connections.Any(c =>
            string.Equals(c.RemoteClass, "Wan", StringComparison.OrdinalIgnoreCase));

        switch (ClassifyTrust(s.SignatureStatus, s.IsUserWritablePath, hasWan))
        {
            case TrustState.AlertCombo:
                TrustAlertCombo.Visibility = Visibility.Visible;
                break;
            case TrustState.SignedPersonal:
                TrustSignedPersonal.Visibility = Visibility.Visible;
                break;
            case TrustState.SignedSystem:
                TrustSignedSystem.Visibility = Visibility.Visible;
                break;
            case TrustState.Fallback:
                var (headline, body, fallbackGlyph) = FallbackTrustCopy(s.SignatureStatus, s.IsUserWritablePath);
                TrustFallbackHeadline.Text = headline;
                TrustFallbackBody.Text = body;
                TrustFallbackGlyph.Symbol = fallbackGlyph;
                TrustFallback.Visibility = Visibility.Visible;
                break;
        }
    }

    // SignatureStatus is a string on the IPC contract (AppListEntry).
    // Canonical values shipped by the service: "Signed", "Unsigned",
    // "Invalid", "Unchecked". Compared case-insensitively here so an
    // unexpected case from a future server build still classifies cleanly
    // rather than falling all the way through to the Fallback branch.
    private const string SigSigned    = "Signed";
    private const string SigUnsigned  = "Unsigned";
    private const string SigInvalid   = "Invalid";
    private const string SigUnchecked = "Unchecked";

    private enum TrustState { AlertCombo, SignedPersonal, SignedSystem, Fallback }

    private static TrustState ClassifyTrust(string sig, bool writable, bool hasWan)
    {
        var isSigned   = string.Equals(sig, SigSigned,   StringComparison.OrdinalIgnoreCase);
        var isUnsigned = string.Equals(sig, SigUnsigned, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(sig, SigInvalid,  StringComparison.OrdinalIgnoreCase);

        if (isUnsigned && writable && hasWan) return TrustState.AlertCombo;
        if (isSigned   && writable)           return TrustState.SignedPersonal;
        if (isSigned   && !writable)          return TrustState.SignedSystem;
        return TrustState.Fallback;
    }

    private static (SymbolRegular glyph, string foregroundKey) SignatureBadge(string sig)
    {
        if (string.Equals(sig, SigSigned, StringComparison.OrdinalIgnoreCase))
            return (SymbolRegular.ShieldCheckmark20, "status.success");
        if (string.Equals(sig, SigUnsigned, StringComparison.OrdinalIgnoreCase)
         || string.Equals(sig, SigInvalid,  StringComparison.OrdinalIgnoreCase))
            return (SymbolRegular.ShieldError20, "status.caution");
        return (SymbolRegular.Info20, "text.tertiary");
    }

    /// <summary>
    /// Copy generators for the TrustFallback Border — used when the trust
    /// state doesn't fit one of the three locked treatments (signed-system,
    /// signed-personal, alert combination). Same neutral surface.subtle
    /// backdrop as the signed cases; the body content carries the signal.
    /// </summary>
    private static (string headline, string body, SymbolRegular glyph) FallbackTrustCopy(string sig, bool writable)
    {
        if (string.Equals(sig, SigUnsigned, StringComparison.OrdinalIgnoreCase))
        {
            return writable
                ? ("Unsigned app running from a personal folder",
                   "This binary isn't digitally signed and lives in a folder you can write to. No outbound connections detected in this window. Worth keeping an eye on if it starts talking.",
                   SymbolRegular.ShieldError20)
                : ("Unsigned app in a system folder",
                   "This binary isn't digitally signed. Unusual for a system folder; worth knowing.",
                   SymbolRegular.ShieldError20);
        }
        if (string.Equals(sig, SigInvalid, StringComparison.OrdinalIgnoreCase))
        {
            return writable
                ? ("Invalid signature, from a personal folder",
                   "This binary's signature didn't verify cleanly, and it lives in a folder you can write to. Worth confirming you recognise it.",
                   SymbolRegular.ShieldError20)
                : ("Invalid signature",
                   "This binary's signature didn't verify cleanly. Worth knowing.",
                   SymbolRegular.ShieldError20);
        }
        if (string.Equals(sig, SigUnchecked, StringComparison.OrdinalIgnoreCase))
        {
            return ("Signature unchecked",
                    "ZenVizor hasn't verified this binary's signature yet. The check happens once and is cached.",
                    SymbolRegular.Info20);
        }
        return ("Unknown signature state",
                $"Signature status: {sig}. No additional context.",
                SymbolRegular.Info20);
    }

    private async Task RefreshAsync()
    {
        if (AppId is not int id || WindowCombo.SelectedItem is not WindowPreset preset) return;

        try
        {
            StatusBanner.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = Cursors.Wait;

            var window = preset.ToWindow();
            var detailTask = _client.GetAppDetailAsync(id, window, TrafficGrain.Auto);
            var connectionsTask = _client.GetConnectionsAsync(id, window);
            await Task.WhenAll(detailTask, connectionsTask);

            ApplyDetail(await detailTask);
            ApplyConnections(await connectionsTask);
            RefreshTrustLine();
        }
        catch (Exception ex)
        {
            StatusBanner.Visibility = Visibility.Visible;
            StatusBannerText.Text = $"Query failed ({ex.GetType().Name}): {ex.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void ApplyDetail(AppDetailResult detail)
    {
        var s = detail.Summary;
        _lastSummary = s;

        // Title = image name only. The AppId lives in the labeled chip
        // beside the title; no "(app id N)" suffix here (Q4 lock).
        HeaderText.Text = s.ImageName;
        AppIdValue.Text = s.AppId.ToString(CultureInfo.InvariantCulture);

        // Identity summary block — fill values and swap each field's
        // Foreground from the text.tertiary placeholder colour to its
        // real colour. Up / Down totals get their chart-series brushes
        // (violet / teal); Publisher / Signature / Path go to text.primary.
        PublisherValue.Text = string.IsNullOrEmpty(s.Publisher) ? "(unknown)" : s.Publisher;
        PublisherValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");

        SignatureValue.Text = string.IsNullOrEmpty(s.SignatureStatus) ? "(unknown)" : s.SignatureStatus;
        SignatureValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");

        var (glyph, glyphForegroundKey) = SignatureBadge(s.SignatureStatus);
        SignatureGlyph.Symbol = glyph;
        SignatureGlyph.SetResourceReference(SymbolIcon.ForegroundProperty, glyphForegroundKey);

        UpTotalValue.Text = PerAppPage.FormatBytes(s.BytesUp);
        UpTotalValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "chart.upSeries");

        DownTotalValue.Text = PerAppPage.FormatBytes(s.BytesDown);
        DownTotalValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "chart.downSeries");

        PathValue.Text = s.ImagePath;
        PathValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");

        var bucketsUp = new SortedDictionary<long, long>();
        var bucketsDown = new SortedDictionary<long, long>();
        foreach (var p in detail.Series)
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
        // adjacent buckets here would make the displayed numbers ~N× too high
        // relative to the labeled unit. Caps at ChartSeriesDownsampler.MaxBuckets
        // for the very dense 24h Samples case (1440 → 240 buckets, each carrying
        // the average bytes/min over its 6-minute group).
        (upPoints, downPoints) = ChartSeriesDownsampler.DownsampleAverage(upPoints, downPoints);

        // Bar-grain density control: 7d Hourly (168 buckets) and 90d Daily
        // (90 buckets) coalesce 2× so the 8 px MaxBarWidth in ChartBuilder
        // renders at a comfortable visual density. 30d Daily (30 buckets) and
        // 24h Samples (already capped to 240 by DownsampleAverage above) do
        // not need extra coalescing. Subtitle copy in
        // ChartBuilder.DescribeView mirrors this policy (returns "2-hour
        // buckets" / "2-day buckets" for the coalesced cases).
        if ((detail.GrainUsed == TrafficGrain.Hourly || detail.GrainUsed == TrafficGrain.Daily)
            && Math.Max(upPoints.Count, downPoints.Count) > 60)
        {
            (upPoints, downPoints) = ChartSeriesDownsampler.Coalesce(upPoints, downPoints, factor: 2);
        }

        var preset = WindowCombo.SelectedItem as WindowPreset;
        // Mutate axis properties (in-place — see UpdateAxesForGrain doc) BEFORE
        // assigning Series so the new Labeler / MinStep / UnitWidth are in
        // place when LC2 lays out the upcoming redraw triggered by the Series
        // assignment.
        UpdateAxesForGrain(detail.GrainUsed, preset);
        SeriesChart.Series = ChartBuilder.BuildSeries(detail.GrainUsed, upPoints, downPoints);
        // Re-paint Up/Down with brand chart.upSeries / chart.downSeries.
        // ChartTheming.Apply in the ctor ran BEFORE SeriesChart.Series was
        // assigned, so its internal ApplyToSeries pass no-op'd on the
        // still-null Series array; without this re-call the fresh line /
        // bar series here would render in LC2's default palette.
        ChartTheming.ApplyToSeries(SeriesChart.Series);
        ChartSubtitle.Text = ChartBuilder.DescribeView(detail.GrainUsed, preset);

        NoDataOverlay.Visibility = upPoints.Count == 0 && downPoints.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        Sessions.Clear();
        foreach (var sess in detail.RecentSessions)
        {
            Sessions.Add(SessionRowViewModel.From(sess));
        }
    }

    private void ApplyConnections(ConnectionListResult result)
    {
        Connections.Clear();
        foreach (var c in result.Connections)
        {
            Connections.Add(ConnectionRowViewModel.From(c));
        }
    }
}

public sealed record ConnectionRowViewModel(
    string Protocol,
    string RemoteAddress,
    int RemotePort,
    string RemoteClass,
    string UpText,
    string DownText)
{
    public static ConnectionRowViewModel From(ConnectionRow c) => new(
        Protocol: c.Protocol,
        RemoteAddress: c.RemoteAddress,
        RemotePort: c.RemotePort,
        RemoteClass: c.RemoteClass,
        UpText: PerAppPage.FormatBytes(c.BytesUp),
        DownText: PerAppPage.FormatBytes(c.BytesDown));
}

public sealed record SessionRowViewModel(
    long SessionId,
    int Pid,
    string StartText,
    string EndText,
    string HostedServices)
{
    public static SessionRowViewModel From(SessionInfo s) => new(
        SessionId: s.SessionId,
        Pid: s.Pid,
        StartText: FormatLocal(s.StartTimeUnixMs),
        EndText: s.EndTimeUnixMs is long e ? FormatLocal(e) : "(running)",
        HostedServices: s.HostedServices ?? "");

    private static string FormatLocal(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
