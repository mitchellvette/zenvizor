using System.Collections.ObjectModel;
using System.ComponentModel;
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
using ZenVizor.Core.Aggregation;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class AppDetailPage : Page
{
    // A2: assigned from MainWindow.HistoryQueryClient in OnPageLoaded.
    private HistoryQueryClient _client = null!;
    private readonly DispatcherTimer _toastTimer;

    public ObservableCollection<EndpointGroupViewModel> Connections { get; } = new();
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    public int? AppId { get; private set; }

    // Phase 5e — when a specific date is set (either by the user via the
    // chrome-row date picker, or via the Reports → AppDetail drill), the
    // chart's time window overrides the WindowCombo preset and shows the
    // 24-hour local day for that date. Null means "use WindowCombo".
    private DateOnly? _specificDate;

    // Latest detail summary, retained so RefreshTrustLine can re-classify after
    // a Connections refresh changes the has-WAN signal (the alert combination
    // is computed from both surfaces).
    private AppListEntry? _lastSummary;

    // Snapshot of InfoPopup.IsOpen captured in PreviewMouseDown on the
    // InfoButton, before WPF's Popup (StaysOpen=False) auto-dismisses on
    // the same mouse-down (button click is "outside" the popup). Without
    // this, OnInfoClick reads IsOpen=false post-dismissal and re-opens
    // the popup, defeating click-toggle behaviour.
    private bool _infoPopupWasOpen;

    // Phase 5 — state-coverage flags + delayed-reveal timer.
    // _isLoading is true while a RefreshAsync is in flight; gates the
    // empty-state overlays (loading > empty) and is checked by the
    // delay timer's Tick handler (race-safe — the timer can fire after
    // refresh finishes if cancellation is tight).
    // _inErrorState is true when the last refresh hit a banner-state
    // (disconnected OR error); gates empty-state overlays (error > empty).
    // _loadingDelayTimer runs once per refresh — if it ticks before the
    // refresh completes, both grid bodies + the chart body reveal a
    // centered ring + "Loading…" caption. Fast refreshes (<1s) never
    // flash a ring.
    private bool _isLoading;
    private bool _inErrorState;
    private readonly DispatcherTimer _loadingDelayTimer;

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

        // Loading-overlay reveal timer (Phase 5). RefreshAsync starts this
        // on entry; Tick (after 1s) reveals the chart / connections /
        // sessions rings IF the refresh is still in flight. RefreshAsync
        // also Stops it in the finally block, so a fast refresh races the
        // timer to Stop() before Tick fires — but ShowLoadingOverlays also
        // gates on _isLoading just in case the dispatch ordering loses.
        _loadingDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _loadingDelayTimer.Tick += (_, _) =>
        {
            _loadingDelayTimer.Stop();
            ShowLoadingOverlays();
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

        // Phase 4.1: tab header live counts. ObservableCollection.Count is
        // not a DependencyProperty so XAML bindings to it don't refresh on
        // CollectionChanged — wire updates manually. Refresh paths use
        // Clear + Add-per-item which fires CollectionChanged N+1 times,
        // but two TextBlock writes per event is cheap at ZenVizor's
        // typical connection / session counts (<100). Phase 5 piggy-backs
        // empty-state updates on the same hook.
        Connections.CollectionChanged += (_, _) =>
        {
            UpdateTabCounts();
            UpdateEmptyStates();
        };
        Sessions.CollectionChanged += (_, _) =>
        {
            UpdateTabCounts();
            UpdateEmptyStates();
        };
        UpdateTabCounts();

        // Close the info popover when the page scrolls. WPF Popup doesn't
        // reposition with its PlacementTarget when the host ScrollViewer
        // scrolls — an open popup would visibly drift away from the
        // now-moved InfoButton. Easiest correct behaviour is to dismiss.
        PageScroll.ScrollChanged += (_, _) => InfoPopup.IsOpen = false;

        DataContextChanged += (_, _) => OnAppIdReceived();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        SizeChanged += (_, _) => EnforceDataGridBounds();

        // Phase 5e — Wpf.Ui's CalendarDatePicker exposes Date as a DP but
        // doesn't surface a public DateChanged event. Subscribe via
        // DependencyPropertyDescriptor (same pattern as ReportsPage's
        // primary date picker).
        var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            CalendarDatePicker.DateProperty,
            typeof(CalendarDatePicker));
        dpd?.AddValueChanged(DatePicker, OnDatePickerDateChanged);
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            // A2: pick up the shared query client from MainWindow.
            _client = mw.HistoryQueryClient;
            mw.HistoryWiped += OnHistoryWiped;
            // A1: refresh on the disconnected→connected transition so a
            // service restart doesn't leave the per-app drill on the
            // stale pipe + stale data until the user re-navigates.
            mw.ServiceReconnected += OnServiceReconnected;
        }
        EnforceDataGridBounds();
        await RefreshAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.HistoryWiped -= OnHistoryWiped;
            mw.ServiceReconnected -= OnServiceReconnected;
        }
    }

    private async void OnHistoryWiped(object? sender, EventArgs e)
    {
        // Per-app drill is only meaningful with an AppId. Without one we
        // skip the refresh — the page will pick up the empty state on
        // the next nav with a valid id.
        if (AppId is int) await RefreshAsync();
    }

    private async void OnServiceReconnected(object? sender, EventArgs e)
    {
        // A2: MainWindow.OnStatusChanged force-reconnected the shared
        // client before raising this event. RefreshAsync stays
        // AppId-gated — see OnHistoryWiped for the rationale.
        if (AppId is int) await RefreshAsync();
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

    private void UpdateTabCounts()
    {
        ConnectionsCount.Text = Connections.Count.ToString(CultureInfo.InvariantCulture);
        SessionsCount.Text = Sessions.Count.ToString(CultureInfo.InvariantCulture);
    }

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
        // Two navigation-parameter shapes are accepted:
        //   * bare int / long — legacy PerAppPage drill (no date override).
        //   * AppDetailNavParams — Phase 5e drill from Reports with an
        //     optional date that pre-populates the chrome-row DatePicker
        //     and overrides the WindowCombo on first refresh.
        switch (DataContext)
        {
            case int i:
                AppId = i;
                ApplySpecificDate(null);
                break;
            case long l:
                AppId = (int)l;
                ApplySpecificDate(null);
                break;
            case AppDetailNavParams p:
                AppId = p.AppId;
                ApplySpecificDate(p.Date);
                break;
            default:
                AppId = null;
                ApplySpecificDate(null);
                break;
        }
        // Title shows the image name once ApplyDetail lands; before that we
        // show the page placeholder. AppId surfaces in the labeled chip
        // beside the title (Q4 lock — never inline in the title text).
        HeaderText.Text = "App detail";
        AppIdValue.Text = AppId is int id
            ? id.ToString(CultureInfo.InvariantCulture)
            : "—";
    }

    // Phase 5e — set the chrome-row DatePicker to a specific date and update
    // the WindowCombo / clear-button visibility accordingly. Setting
    // DatePicker.Date triggers OnDatePickerDateChanged which sinks the value
    // into _specificDate and refreshes.
    private void ApplySpecificDate(DateOnly? date)
    {
        if (date is null)
        {
            DatePicker.Date = null;
            return;
        }
        DatePicker.Date = date.Value.ToDateTime(new TimeOnly(0));
    }

    private async void OnDatePickerDateChanged(object? sender, EventArgs e)
    {
        _specificDate = DatePicker.Date is { } dt ? DateOnly.FromDateTime(dt) : null;
        var hasDate = _specificDate is not null;
        // Visually swap chrome state. DatePickerOverlay (placeholder) hides
        // when a date is set; DatePickerLabel (formatted date) shows in its
        // place — they sit at the same Grid layer.
        WindowCombo.IsEnabled        = !hasDate;
        ClearDateButton.Visibility   = hasDate ? Visibility.Visible : Visibility.Collapsed;
        DatePickerOverlay.Visibility = hasDate ? Visibility.Collapsed : Visibility.Visible;
        DatePickerLabel.Visibility   = hasDate ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && AppId is int) await RefreshAsync();
    }

    private void OnClearDateClick(object sender, RoutedEventArgs e)
    {
        // Clearing DatePicker.Date fires OnDatePickerDateChanged which
        // handles the visibility flips + RefreshAsync.
        DatePicker.Date = null;
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

    // Convert a local DateOnly into the UTC unix-ms window covering its
    // 24 local hours. Mirrors the boundary math in
    // DailyReportRepository.LocalDayWindowUtcMs so the AppDetailPage chart
    // and the source Reports row reconcile exactly.
    private static QueryWindow LocalDayWindow(DateOnly date)
    {
        var midnight     = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var midnightNext = midnight.AddDays(1);
        var startUtc     = TimeZoneInfo.ConvertTimeToUtc(midnight,     TimeZoneInfo.Local);
        var endUtc       = TimeZoneInfo.ConvertTimeToUtc(midnightNext, TimeZoneInfo.Local);
        return new QueryWindow(
            FromUnixMs: new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ToUnixMs:   new DateTimeOffset(endUtc,   TimeSpan.Zero).ToUnixTimeMilliseconds());
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

    private void OnCopyEndpointClick(object sender, MouseButtonEventArgs e)
    {
        // The hover-revealed copy chip in the Remote endpoint cell template
        // carries the row's endpoint identity in its Tag — that's the
        // hostname when one was resolved (most useful for whois / nslookup
        // / reputation lookups) and the IP otherwise. Cells in this grid
        // aren't selectable (no SelectionMode wired) so this is the user's
        // only path to grab the address for external diagnostics.
        if (sender is FrameworkElement fe && fe.Tag is string address && !string.IsNullOrEmpty(address))
        {
            TryCopyToClipboard(address);
        }
    }

    /// <summary>
    /// Toggle a Connections-grid row's expand-in-place children. Wired to
    /// the leading chevron cell's MouseLeftButtonUp — chevron is hidden
    /// for single-port groups (no children to reveal), so this handler
    /// only fires for multi-port rows. The two-way binding on the
    /// DataGridRow style mirrors IsExpanded into DetailsVisibility.
    /// </summary>
    private void OnEndpointExpandToggle(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is EndpointGroupViewModel vm)
        {
            vm.IsExpanded = !vm.IsExpanded;
            e.Handled = true;
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
    private void ShowCopiedToast() => ShowToast("Copied to clipboard");

    /// <summary>
    /// Surface a generic toast with the given text and reset the
    /// auto-dismiss timer. Used by both the clipboard copy flow (default
    /// copy of confirmation copy) and the Phase 4.1 Info button
    /// placeholder. When Phase 4.4 wires the real column-legend Popup the
    /// Info button's toast call goes away.
    /// </summary>
    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnInfoButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Capture popup state BEFORE WPF dismisses it via StaysOpen=False
        // on this same mouse-down (the button click counts as "outside"
        // the popup). OnInfoClick uses the snapshot to decide whether to
        // toggle closed or open.
        _infoPopupWasOpen = InfoPopup.IsOpen;
    }

    private void OnInfoClick(object sender, RoutedEventArgs e)
    {
        // Toggle: if the popup was open when the click started, close it;
        // otherwise open. The snapshot captured in
        // OnInfoButtonPreviewMouseDown wins over IsOpen at click time
        // (which is already false post-StaysOpen-dismissal). Content
        // auto-swaps Connections <-> Sessions via DataTriggers on
        // GridsTab.SelectedIndex.
        InfoPopup.IsOpen = !_infoPopupWasOpen;
        _infoPopupWasOpen = false;
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

        // Entry — set loading state, recover from any previous error.
        _isLoading = true;
        StatusBanner.Visibility = Visibility.Collapsed;
        SetDataOpacity(1.0);   // un-dim if previous refresh ended disconnected/error
        HideEmptyOverlays();   // loading > empty
        _loadingDelayTimer.Stop();
        _loadingDelayTimer.Start();

        try
        {
            // Phase 5e — when _specificDate is set, the chart and
            // connections queries cover that calendar day's 24-hour local
            // window for this app, overriding the WindowCombo preset. The
            // user's local zone is used for the day boundary so the data
            // matches their "what happened today" intent.
            var window = _specificDate is { } d
                ? LocalDayWindow(d)
                : preset.ToWindow();
            var detailTask = _client.GetAppDetailAsync(id, window, TrafficGrain.Auto);
            var connectionsTask = _client.GetConnectionsAsync(id, window);
            await Task.WhenAll(detailTask, connectionsTask);

            ApplyDetail(await detailTask);
            ApplyConnections(await connectionsTask);
            RefreshTrustLine();
            _inErrorState = false;
            UpdateEmptyStates();
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            // Pipe down — Phase 6.5 standardized to caution-amber +
            // PlugDisconnected20 glyph across every page. Last-known data
            // stays dimmed to 0.6 so the user can still read what was
            // there before the pipe broke.
            _inErrorState = true;
            StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
            StatusBannerGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.PlugDisconnected20;
            StatusBannerGlyph.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "status.caution.text");
            StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
            StatusBannerText.Text = "Service disconnected. Last refresh stale.";
            StatusBanner.Visibility = Visibility.Visible;
            SetDataOpacity(0.6);
            HideEmptyOverlays();   // error > empty
        }
        catch (Exception ex)
        {
            // Any other query failure — caution-amber banner with
            // Warning20 glyph; same dim.
            _inErrorState = true;
            StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
            StatusBannerGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning20;
            StatusBannerGlyph.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "status.caution.text");
            StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
            StatusBannerText.Text = $"Query failed ({ex.GetType().Name}): {ex.Message}";
            StatusBanner.Visibility = Visibility.Visible;
            SetDataOpacity(0.6);
            HideEmptyOverlays();
        }
        finally
        {
            _isLoading = false;
            _loadingDelayTimer.Stop();
            HideLoadingOverlays();
        }
    }

    /// <summary>
    /// Show the chart + connections + sessions loading rings. Called from
    /// the _loadingDelayTimer Tick handler after the 1s grace period;
    /// guards on _isLoading in case the refresh completed in the same
    /// dispatcher cycle and the timer Stop() lost the race.
    /// </summary>
    private void ShowLoadingOverlays()
    {
        if (!_isLoading) return;
        ChartLoadingOverlay.Visibility = Visibility.Visible;
        ConnectionsLoadingOverlay.Visibility = Visibility.Visible;
        SessionsLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoadingOverlays()
    {
        ChartLoadingOverlay.Visibility = Visibility.Collapsed;
        ConnectionsLoadingOverlay.Visibility = Visibility.Collapsed;
        SessionsLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Dim or restore all three data card surfaces (summary, chart, grids).
    /// 0.6 telegraphs stale data during disconnected/error states without
    /// clearing the last-known content; 1.0 restores on next successful
    /// refresh.
    /// </summary>
    private void SetDataOpacity(double opacity)
    {
        SummaryCard.Opacity = opacity;
        ChartCard.Opacity = opacity;
        GridsCard.Opacity = opacity;
    }

    private void HideEmptyOverlays()
    {
        ConnectionsEmptyOverlay.Visibility = Visibility.Collapsed;
        SessionsEmptyOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Show the per-grid empty-state overlay when its collection is empty
    /// and the page is not in a loading or error state. Loading and error
    /// states own the visual priority and own their own overlays / banner.
    /// </summary>
    private void UpdateEmptyStates()
    {
        if (_isLoading || _inErrorState)
        {
            HideEmptyOverlays();
            return;
        }
        ConnectionsEmptyOverlay.Visibility = Connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsEmptyOverlay.Visibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        // Phase 9.5 — flat per-(proto, IP, port) rows from the server
        // collapse to endpoint identity (hostname else IP) here in the
        // UI. The grouper lives in ZenVizor.Core; this method is the
        // single bridge between the IPC payload and the visual model.
        Connections.Clear();
        var groups = ConnectionGrouper.Collapse(result.Connections);
        foreach (var g in groups)
        {
            Connections.Add(EndpointGroupViewModel.From(g));
        }
    }
}

/// <summary>
/// One row in the Phase 9.5 collapsed Connections grid — an endpoint
/// identity rolled up across its underlying (proto, IP, port) triples.
/// A group with a single port and single address renders
/// indistinguishably from the pre-9.5 flat row; multi-port groups
/// surface their per-port detail in the DataGrid's RowDetailsTemplate
/// when the user expands the row (single click on the chevron).
/// </summary>
public sealed class EndpointGroupViewModel : INotifyPropertyChanged
{
    public EndpointGroupViewModel(
        string identity,
        string? resolvedHost,
        IReadOnlyList<string> addresses,
        string remoteClass,
        long bytesUp,
        long bytesDown,
        int distinctPortCount,
        IReadOnlyList<PortChildViewModel> ports)
    {
        Identity = identity;
        ResolvedHost = resolvedHost;
        Addresses = addresses;
        RemoteClass = remoteClass;
        UpText = PerAppPage.FormatBytes(bytesUp);
        DownText = PerAppPage.FormatBytes(bytesDown);
        DistinctPortCount = distinctPortCount;
        Ports = ports;
    }

    public static EndpointGroupViewModel From(EndpointGroup g) => new(
        identity:          g.Identity,
        resolvedHost:      g.ResolvedHost,
        addresses:         g.Addresses,
        remoteClass:       g.RemoteClass,
        bytesUp:           g.BytesUp,
        bytesDown:         g.BytesDown,
        distinctPortCount: g.DistinctPortCount,
        ports:             g.Ports.Select(PortChildViewModel.From).ToArray());

    public string Identity { get; }
    public string? ResolvedHost { get; }
    public IReadOnlyList<string> Addresses { get; }
    public string RemoteClass { get; }
    public string UpText { get; }
    public string DownText { get; }
    public int DistinctPortCount { get; }
    public IReadOnlyList<PortChildViewModel> Ports { get; }

    /// <summary>True when the identity is a resolved hostname (drives the
    /// dual-line address treatment in the cell template).</summary>
    public bool HasHostname => !string.IsNullOrWhiteSpace(ResolvedHost);

    /// <summary>
    /// IP shown as a subscript under the hostname. When the hostname fans
    /// across CDN edges, surfaces the first sorted address plus a "+N more"
    /// suffix; the full list lives in <see cref="AddressTooltip"/>.
    /// </summary>
    public string AddressSubscript => Addresses.Count switch
    {
        0 => string.Empty,
        1 => Addresses[0],
        _ => $"{Addresses[0]} +{Addresses.Count - 1} more",
    };

    /// <summary>Tooltip enumerating every IP the identity covered.</summary>
    public string AddressTooltip => string.Join(", ", Addresses);

    /// <summary>True when the identity covers more than one IP.</summary>
    public bool HasMultipleAddresses => Addresses.Count > 1;

    /// <summary>
    /// Protocol summary for the parent row. When every child shares a
    /// single protocol, that protocol is shown verbatim (matches the
    /// pre-9.5 flat row's Proto column for the dominant single-proto
    /// case). When a group mixes TCP and UDP children, "Mixed" surfaces
    /// — the per-protocol detail lives in the expanded children.
    /// </summary>
    public string ProtocolSummary
    {
        get
        {
            if (Ports.Count == 0) return string.Empty;
            var first = Ports[0].Protocol;
            for (int i = 1; i < Ports.Count; i++)
            {
                if (!string.Equals(Ports[i].Protocol, first, StringComparison.Ordinal))
                {
                    return "Mixed";
                }
            }
            return first;
        }
    }

    /// <summary>
    /// Port-column content. Single-port groups show the bare number
    /// (matching the flat-row case). Multi-port groups surface the
    /// distinct-port count; the per-port enumeration lives in the
    /// expand-in-place children and in <see cref="PortTooltip"/>.
    /// </summary>
    public string PortSummary => Ports.Count == 1
        ? Ports[0].Port.ToString(CultureInfo.InvariantCulture)
        : $"{DistinctPortCount} ports";

    /// <summary>
    /// Plain-language port caption — non-null only when the group covers
    /// a single well-known port (so single-port groups look identical to
    /// the pre-9.5 row). Multi-port groups suppress the caption.
    /// </summary>
    public string? PortServiceCaption => Ports.Count == 1
        ? WellKnownPort.Caption(Ports[0].Port)
        : null;

    /// <summary>Tooltip enumerating every (proto, port) in the group.</summary>
    public string PortTooltip => string.Join(", ",
        Ports.Select(p => $"{p.Protocol}/{p.Port}"));

    /// <summary>True when the parent row has more than one underlying
    /// port — the trigger for the leading chevron + RowDetailsTemplate.</summary>
    public bool HasMultiplePorts => Ports.Count > 1;

    /// <summary>True when the endpoint is classified as upstream (WAN).</summary>
    public bool IsWan => string.Equals(RemoteClass, "Wan", StringComparison.OrdinalIgnoreCase);

    /// <summary>"WAN" / "Local" display string for the Reach pill.</summary>
    public string ReachText => IsWan ? "WAN" : "Local";

    private bool _isExpanded;
    /// <summary>
    /// Two-way bound to the DataGridRow's DetailsVisibility via a style
    /// trigger; toggled by the chevron cell's MouseLeftButtonUp handler.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One per-port row inside an <see cref="EndpointGroupViewModel"/>'s
/// expand-in-place detail panel.
/// </summary>
public sealed record PortChildViewModel(
    string Protocol,
    int Port,
    string? PortServiceCaption,
    string UpText,
    string DownText)
{
    public static PortChildViewModel From(EndpointPortChild p) => new(
        Protocol:           p.Protocol,
        Port:               p.Port,
        PortServiceCaption: WellKnownPort.Caption(p.Port),
        UpText:             PerAppPage.FormatBytes(p.BytesUp),
        DownText:           PerAppPage.FormatBytes(p.BytesDown));
}

internal static class WellKnownPort
{
    /// <summary>
    /// Plain-language caption for well-known ports. Returns null for
    /// unknown ports — the rendering surface then shows the number alone.
    /// </summary>
    public static string? Caption(int port) => port switch
    {
        80 => "HTTP",
        443 => "HTTPS",
        53 => "DNS",
        5353 => "mDNS",
        8443 => "HTTPS-alt",
        8080 => "HTTP-alt",
        22 => "SSH",
        21 => "FTP",
        25 => "SMTP",
        587 => "SMTP-S",
        465 => "SMTPS",
        110 => "POP3",
        995 => "POP3S",
        143 => "IMAP",
        993 => "IMAPS",
        23 => "Telnet",
        1900 => "SSDP",
        5355 => "LLMNR",
        137 or 138 or 139 => "NetBIOS",
        445 => "SMB",
        3389 => "RDP",
        67 or 68 => "DHCP",
        123 => "NTP",
        161 or 162 => "SNMP",
        389 => "LDAP",
        636 => "LDAPS",
        _ => null,
    };
}

public sealed record SessionRowViewModel(
    long SessionId,
    int Pid,
    long StartTimeUnixMs,
    long? EndTimeUnixMs,
    string HostedServices)
{
    public static SessionRowViewModel From(SessionInfo s) => new(
        SessionId: s.SessionId,
        Pid: s.Pid,
        StartTimeUnixMs: s.StartTimeUnixMs,
        EndTimeUnixMs: s.EndTimeUnixMs,
        HostedServices: s.HostedServices ?? string.Empty);

    public string StartText => FormatLocal(StartTimeUnixMs);

    /// <summary>
    /// Formatted end timestamp for completed sessions; empty string for
    /// running sessions (the Ended-column template renders a green bullet
    /// + "running" caption instead, driven by <see cref="IsRunning"/>).
    /// </summary>
    public string EndedText => EndTimeUnixMs is long e ? FormatLocal(e) : string.Empty;

    public bool IsRunning => EndTimeUnixMs is null;

    /// <summary>
    /// Session length in milliseconds. For running sessions, computed
    /// against UTC "now" at access time — the value drifts but is fine
    /// for sort (DataGrid captures values once per sort, not continuously).
    /// </summary>
    public long LengthMs
    {
        get
        {
            var end = EndTimeUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Math.Max(0, end - StartTimeUnixMs);
        }
    }

    public string LengthText => FormatDuration(LengthMs);

    /// <summary>
    /// HostedServices split into individual tags for chip rendering. The
    /// server ships a raw comma-separated string; the chip ItemsControl
    /// uses this array. Empty / null inputs yield an empty array so the
    /// column cell renders blank for non-svchost rows.
    /// </summary>
    public IReadOnlyList<string> ServiceTags =>
        string.IsNullOrWhiteSpace(HostedServices)
            ? Array.Empty<string>()
            : HostedServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatLocal(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Format a session duration as <c>Hh Mm</c> (under 24h) or
    /// <c>Dd Hh</c> (24h+). Matches the mockup's "3h 40m" / "1d 5h" form.
    /// </summary>
    private static string FormatDuration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 24
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : $"{(int)span.TotalHours}h {span.Minutes}m";
    }
}

/// <summary>
/// Phase 5e — navigation parameter for opening AppDetailPage with an
/// optional date pre-populated in the chrome-row date picker. Passed via
/// <c>NavigationView.Navigate(typeof(AppDetailPage), new AppDetailNavParams(...))</c>.
/// The legacy bare-int DataContext path (PerAppPage → AppDetail) still
/// works; only the Reports drill uses this record.
/// </summary>
public sealed record AppDetailNavParams(int AppId, DateOnly? Date);
