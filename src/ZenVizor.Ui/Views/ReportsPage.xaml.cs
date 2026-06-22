using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
public sealed partial class ReportsPage : Page
{
    // Date the page opens on before the user picks one. Snapshotted at
    // construction time from the Clock seam so each fresh page instance
    // reflects "today" — a new navigation to Reports always lands on the
    // current date. The seam is overrideable from tests (InternalsVisibleTo
    // grants ZenVizor.Integration.Tests access) so the default-date wiring
    // is verifiable without time-of-day flake.
    internal static Func<DateTime> Clock { get; set; } = () => DateTime.Today;
    private readonly DateTime _initialDate = Clock();

    // Assigned from MainWindow.HistoryQueryClient in OnLoaded.
    private HistoryQueryClient _client = null!;
    private readonly DailyReportCsvWriter _csvWriter = new();
    private readonly DailyReportHtmlWriter _htmlWriter = new();

    // Latest IPC result, stashed so the CSV / HTML export handlers have
    // data to serialize. Null until the first successful refresh; the
    // Export button is IsEnabled=false in every state that would leave
    // this null (Loading / Empty / Disconnected / Error), so the click
    // handlers can assume non-null when invoked.
    private DailyReportResult? _lastResult;

    // Currently selected anchor (drives the delta-vs-baseline figures).
    // Mockup Q2b lock: default 7-day average. Updated by OnAnchorSelected;
    // refresh fires on every change.
    private AnchorMode _anchor = AnchorMode.Avg7d;
    // Comparison date when the user picks "A specific date" from the anchor
    // menu. Null when any rolling-average anchor is active. Set by
    // OnAnchorDateSelected when the user picks a date in AnchorDatePopup;
    // cleared by OnAnchorSelected when the user switches back to a
    // rolling-average anchor. Flows through to LoadAnchorBaseline as the
    // window-of-one comparison target.
    private DateOnly? _anchorSpecificDate;

    // Per-refresh chart state, recomputed each refresh from the
    // DailyReportHourPoint series the service returns. _chartReportDate
    // seeds at _initialDate so FormatHourTick renders the empty chart's
    // 00:00 tick correctly before the first refresh lands.
    private DateTime _chartReportDate;
    private int _peakHour;
    private double _peakValue;
    private double _maxYValue = 1;

    // State-machine field. RefreshAsync drives transitions: Loading on
    // entry; Default/Empty/QuietDay from result classification on success;
    // Disconnected/Error from catch.
    private enum ReportsState { Default, Empty, QuietDay, Loading, Disconnected, Error }
    private ReportsState _state = ReportsState.Default;
    private DispatcherTimer? _loadingCaptionTimer;

    // Axis instances persist for the page's lifetime per
    // _chart-implementation-notes.md §3 — wholesale axis-array replacement
    // combined with same-frame Series reassignment leaves LC2 v2 in an
    // inconsistent state and the chart renders blank.
    private Axis? _xAxis;
    private Axis? _yAxis;

    public ReportsPage()
    {
        InitializeComponent();

        _chartReportDate = _initialDate;
        InitDatePicker();
        InitAnchorMenu();
        InitSparkline();
        UpdateHeroEyebrow(_initialDate);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SparklineChart.SizeChanged += (_, _) => RepositionPeakOverlay();
        // Wpf.Ui's NavigationView wraps hosted pages in a DynamicScrollViewer
        // (infinite vertical extent), so any DataGrid that wants to bound
        // its row panel must have MaxHeight set programmatically — XAML
        // bindings up the chain don't propagate a finite measure constraint.
        // (Pattern locked: PerAppPage.EnforceAppsGridBound,
        // AppDetailPage.EnforceDataGridBounds.)
        SizeChanged += (_, _) => EnforceTopAppsGridBound();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ChartTheming.Apply(SparklineChart);
        StyleSparklineAxes();
        RepositionPeakOverlay();
        ChartTheming.Changed += OnThemeChanged;

        // Pick up the shared query client from MainWindow. Subscribe to the
        // wipe fan-out so the user sees an empty state immediately after
        // Reset history, and to ServiceReconnected so a service restart
        // refreshes the page automatically.
        if (Application.Current.MainWindow is MainWindow mw)
        {
            _client = mw.HistoryQueryClient;
            mw.HistoryWiped += OnHistoryWiped;
            mw.ServiceReconnected += OnServiceReconnected;
        }

        // Apply the initial MaxHeight bound BEFORE the first IPC fetch — the
        // backfill of TopApps rows must measure against a finite cap or the
        // virtualizer materializes every row at once.
        EnforceTopAppsGridBound();

        // First IPC fetch — populates Hero / sparkline / Top Apps /
        // Uncommon Talkers with real data from the service.
        await RefreshAsync();
    }

    private async void OnHistoryWiped(object? sender, EventArgs e) => await RefreshAsync();

    private async void OnServiceReconnected(object? sender, EventArgs e)
    {
        // MainWindow.OnStatusChanged force-reconnects the shared client
        // before raising this event.
        await RefreshAsync();
    }

    /// <summary>
    /// Bound the Top Apps DataGrid's height at runtime so WPF row
    /// virtualization engages. Same rationale as
    /// <see cref="AppDetailPage"/>.EnforceDataGridBounds and
    /// <see cref="PerAppPage"/>.EnforceAppsGridBound: Wpf.Ui's NavigationView
    /// hands the page infinite vertical extent and the DataGrid can't
    /// inherit a finite measure from XAML alone.
    /// <para>
    /// Formula mirrors PerAppPage's <c>window - 220</c> floor; the 220 covers
    /// chrome (page title row + filter strip + footer card padding) on a
    /// typical ZenVizor frame. Floor at 200 so the grid stays usable on
    /// short windows.
    /// </para>
    /// </summary>
    private void EnforceTopAppsGridBound()
    {
        if (TopAppsGrid is null) return;
        var window = Window.GetWindow(this);
        if (window is null) return;
        var cap = Math.Max(200, window.ActualHeight - 220);
        TopAppsGrid.MaxHeight = cap;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ChartTheming.Changed -= OnThemeChanged;
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.HistoryWiped -= OnHistoryWiped;
            mw.ServiceReconnected -= OnServiceReconnected;
        }
    }

    private void OnThemeChanged()
    {
        ChartTheming.Apply(SparklineChart);
        StyleSparklineAxes();
    }

    private void InitDatePicker()
    {
        PrimaryDatePicker.Date = _initialDate;

        // Wpf.Ui's CalendarDatePicker exposes Date as a DependencyProperty
        // but doesn't surface a public DateChanged event. Listen on the DP
        // directly via System.ComponentModel — DependencyPropertyDescriptor
        // attaches a value-changed handler that fires on every Date write.
        // This is the same pattern WPF uses internally for its
        // DependencyProperty notifications and is documented in the
        // Wpf.Ui samples for cases the typed Date setter alone can't see.
        var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            CalendarDatePicker.DateProperty,
            typeof(CalendarDatePicker));
        dpd?.AddValueChanged(PrimaryDatePicker, OnPrimaryDateChanged);
    }

    private void InitAnchorMenu()
    {
        // The menu items are statically authored in XAML; populate their
        // per-item date-range captions and prime the button face to the
        // default "7-day average" selection. Captions refresh on every
        // date change via RefreshAnchorCaptions.
        RefreshAnchorCaptions(_initialDate);
        ApplyAnchorSelection("Avg7d");
    }

    private void RefreshAnchorCaptions(DateTime forDate)
    {
        // Anchor ranges resolve against the primary date - 1 day (the day
        // before the report date), so the 7-day window ends the day prior
        // to keep self-consistent — today's report is incomplete by
        // definition.
        var dayBefore = forDate.AddDays(-1);
        // SpecificDate caption shows the actual picked date so the menu
        // accurately previews the comparison target. "Pick a date" before
        // any selection invites the user into the popover.
        AnchorSpecificCaption.Text = _anchorSpecificDate is { } sd
            ? sd.ToString("ddd, MMM d", CultureInfo.InvariantCulture)
            : "Pick a date";
        Anchor7DayCaption.Text = FormatRange(dayBefore.AddDays(-6), dayBefore);
        Anchor30DayCaption.Text = FormatRange(dayBefore.AddDays(-29), dayBefore);
        Anchor90DayCaption.Text = FormatRange(dayBefore.AddDays(-89), dayBefore);
    }

    // En-dash (–) in the date-range caption is not an em-dash and IS
    // permitted — feedback_no_emdash_in_ui_copy.md bans em-dash in prose,
    // not en-dash in numeric ranges.
    private static string FormatRange(DateTime start, DateTime end) =>
        $"{start.ToString("MMM d", CultureInfo.InvariantCulture)} – {end.ToString("MMM d", CultureInfo.InvariantCulture)}";

    private void UpdateHeroEyebrow(DateTime date)
    {
        HeroEyebrow.Text = $"TOTAL TRAFFIC · {date.ToString("ddd, MMM d yyyy", CultureInfo.InvariantCulture)}".ToUpperInvariant();
    }

    private async void OnPrimaryDateChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        var d = PrimaryDatePicker.Date ?? _initialDate;
        RefreshAnchorCaptions(d);
        UpdateHeroEyebrow(d);
        await RefreshAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    //  Anchor menu — ui:Button + ContextMenu pattern (same as Export
    //  below). Click opens the menu right-anchored; the MenuItem.Click
    //  handler reads the Tag and re-skins the button face.
    //  ────────────────────────────────────────────────────────────────────

    private void OnAnchorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.ContextMenu is { } cm)
        {
            cm.PlacementTarget = el;
            cm.Placement = PlacementMode.Custom;
            cm.CustomPopupPlacementCallback = RightAnchoredBelow;
            cm.IsOpen = true;
        }
    }

    private async void OnAnchorSelected(object sender, RoutedEventArgs e)
    {
        // Fully-qualified System.Windows.Controls.MenuItem — Wpf.Ui ships
        // its own MenuItem in Wpf.Ui.Controls and the bare name is ambiguous
        // under the `using Wpf.Ui.Controls;` at the top of this file. The
        // XAML authors plain <MenuItem> (no ui: prefix), so the WPF type is
        // what arrives here.
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string tag)
        {
            _anchor = ParseAnchor(tag);

            if (_anchor == AnchorMode.SpecificDate)
            {
                // Don't refresh yet — wait for the user to pick a comparison
                // date in the popup. OpenAnchorDatePicker seeds the calendar
                // with the prior pick (or yesterday) and opens the popup;
                // OnAnchorDateSelected handles the picked-date storage and
                // the deferred refresh. Re-selecting "A specific date" from
                // the menu re-opens the popup every time per the UX brief.
                ApplyAnchorSelection(tag);
                OpenAnchorDatePicker();
                return;
            }

            // Leaving SpecificDate — clear the picked date so a subsequent
            // re-pick starts from the yesterday default rather than the
            // stale prior choice.
            _anchorSpecificDate = null;
            ApplyAnchorSelection(tag);
            if (IsLoaded) await RefreshAsync();
        }
    }

    private void OpenAnchorDatePicker()
    {
        // Comparison must be earlier than the report date itself — anchoring
        // a "vs" comparison to today or the future doesn't read sensibly.
        // PrimaryDatePicker.Date is the user-selected report date.
        var reportDate = PrimaryDatePicker.Date ?? _initialDate;
        AnchorDateCalendar.DisplayDateEnd = reportDate.AddDays(-1);

        var initial = _anchorSpecificDate?.ToDateTime(TimeOnly.MinValue)
                      ?? DateTime.Today.AddDays(-1);
        // Clamp to the picker's MaxDate so the calendar opens on a visible
        // month even if the prior pick is now after the new MaxDate
        // (e.g., the user shrank the report date).
        if (initial > AnchorDateCalendar.DisplayDateEnd)
            initial = AnchorDateCalendar.DisplayDateEnd.Value;

        AnchorDateCalendar.DisplayDate  = initial;
        AnchorDateCalendar.SelectedDate = _anchorSpecificDate?.ToDateTime(TimeOnly.MinValue);
        AnchorDatePopup.IsOpen = true;
    }

    private async void OnAnchorDateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AnchorDateCalendar.SelectedDate is not { } picked) return;
        _anchorSpecificDate = DateOnly.FromDateTime(picked);
        AnchorDatePopup.IsOpen = false;
        // Refresh the button face label so it now reads "vs {date}" and the
        // menu caption so the next open shows the just-picked date.
        ApplyAnchorSelection("SpecificDate");
        RefreshAnchorCaptions(PrimaryDatePicker.Date ?? _initialDate);
        if (IsLoaded) await RefreshAsync();
    }

    private static AnchorMode ParseAnchor(string tag) => tag switch
    {
        "SpecificDate" => AnchorMode.SpecificDate,
        "Avg30d"       => AnchorMode.Avg30d,
        "Avg90d"       => AnchorMode.Avg90d,
        _              => AnchorMode.Avg7d,
    };

    private void ApplyAnchorSelection(string tag)
    {
        SymbolRegular glyph;
        string label;
        switch (tag)
        {
            case "SpecificDate":
                glyph = SymbolRegular.CalendarLtr20;
                // Once a date is picked, the button face reads "vs {date}"
                // so the chrome answers the question the menu asked. Before
                // any pick (popup still pending), keep the menu's label.
                label = _anchorSpecificDate is { } sd
                    ? $"vs {sd.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}"
                    : "A specific date";
                break;
            case "Avg30d":
                glyph = SymbolRegular.History24;
                label = "30-day average";
                break;
            case "Avg90d":
                glyph = SymbolRegular.History24;
                label = "90-day average";
                break;
            default:
                glyph = SymbolRegular.History24;
                label = "7-day average";
                break;
        }
        AnchorGlyph.Symbol = glyph;
        AnchorLabel.Text = label;
    }

    // Place a popup directly beneath the trigger, with the popup's right
    // edge aligned to the trigger's right edge. PrimaryAxis = Horizontal so
    // the auto-reposition fallback (when off-screen) shifts on X, not Y.
    private static CustomPopupPlacement[] RightAnchoredBelow(Size popupSize, Size targetSize, Point offset)
    {
        return new[]
        {
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height),
                PopupPrimaryAxis.Horizontal),
        };
    }

    // ────────────────────────────────────────────────────────────────────
    //  RefreshAsync — single entry point for state transitions.
    //  Loading → IPC → result classification (Default / QuietDay / Empty)
    //  or catch → Disconnected / Error. Mirrors the
    //  HistoryQueryClient.IsConnectionLost pattern from
    //  HistoryPage / AppDetailPage / PerAppPage.
    //  ────────────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        ApplyState(ReportsState.Loading);
        try
        {
            var date = PrimaryDatePicker.Date is { } d
                ? DateOnly.FromDateTime(d)
                : DateOnly.FromDateTime(_initialDate);
            var result = await _client.GetDailyReportAsync(date, _anchor, _anchorSpecificDate);
            ApplyResult(result);
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            ApplyState(ReportsState.Disconnected);
        }
        catch (Exception ex)
        {
            ApplyState(ReportsState.Error, ex.Message);
        }
    }

    private void ApplyResult(DailyReportResult result)
    {
        _lastResult = result;
        var reportDate = result.Date.ToDateTime(new TimeOnly(0), DateTimeKind.Local);
        UpdateHeroEyebrow(reportDate);
        ApplyHero(result.Hero);
        ApplySparkline(result.HourlyTraffic, reportDate);

        TopAppsGrid.ItemsSource = result.TopApps.Select(MapTopAppRow).ToArray();
        UncommonNewTodayList.ItemsSource = result.UncommonTalkers
            .Where(t => t.Category == UncommonCategory.NewToday)
            .Select(MapTalker).ToArray();
        UncommonUnusualVolumeList.ItemsSource = result.UncommonTalkers
            .Where(t => t.Category == UncommonCategory.UnusualVolume)
            .Select(MapTalker).ToArray();
        UncommonRiskyPathsList.ItemsSource = result.UncommonTalkers
            .Where(t => t.Category == UncommonCategory.RiskyPaths)
            .Select(MapTalker).ToArray();
        ApplyNotable(result.Notable);

        ApplyState(ClassifyResult(result));
    }

    // Filter + dispatch Notable items into the three severity sections.
    // Sections with zero items collapse entirely (header + cards). The MVP
    // emits Critical entries only (UnsignedFromUserPath rule); Warning +
    // Info exist in the enum for forward compat and will typically be
    // empty on a real machine.
    private void ApplyNotable(IReadOnlyList<DailyReportNotable> items)
    {
        var critical = items.Where(n => n.Severity == NotableSeverity.Critical).Select(MapNotable).ToArray();
        var warning  = items.Where(n => n.Severity == NotableSeverity.Warning).Select(MapNotable).ToArray();
        var info     = items.Where(n => n.Severity == NotableSeverity.Info).Select(MapNotable).ToArray();

        NotableCriticalList.ItemsSource = critical;
        NotableWarningList.ItemsSource  = warning;
        NotableInfoList.ItemsSource     = info;

        NotableCriticalCount.Text = critical.Length.ToString(CultureInfo.InvariantCulture);
        NotableWarningCount.Text  = warning.Length.ToString(CultureInfo.InvariantCulture);
        NotableInfoCount.Text     = info.Length.ToString(CultureInfo.InvariantCulture);

        NotableCriticalSection.Visibility = critical.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotableWarningSection.Visibility  = warning.Length  > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotableInfoSection.Visibility     = info.Length     > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static NotableCardViewModel MapNotable(DailyReportNotable n)
    {
        // Format event time as HH:mm in the user's local zone — the entity-ref
        // row's whole point is to give the user a "where to look" anchor and
        // local time is what they're scanning for.
        var time = DateTimeOffset.FromUnixTimeMilliseconds(n.EventTimeUnixMs)
            .ToLocalTime()
            .ToString("HH:mm", CultureInfo.InvariantCulture);
        return new NotableCardViewModel(
            Severity:   n.Severity,
            Title:      n.Title,
            Detail:     n.Detail,
            EntityRef:  $"App · {n.ImageName} · pid {n.Pid} · {time}",
            AlertsText: $"Alerts · #{n.AlertId}",
            AlertId:    n.AlertId);
    }

    // Decide the data-bearing state from the result shape: Empty when no
    // traffic and no apps; QuietDay when traffic is sparse and there's
    // nothing notable or uncommon to surface; Default otherwise.
    private static ReportsState ClassifyResult(DailyReportResult r)
    {
        var hasTraffic = r.HourlyTraffic.Any(p => p.BytesUp + p.BytesDown > 0);
        if (!hasTraffic && r.TopApps.Count == 0)
            return ReportsState.Empty;
        if (r.TopApps.Count <= 6 && r.Notable.Count == 0 && r.UncommonTalkers.Count == 0)
            return ReportsState.QuietDay;
        return ReportsState.Default;
    }

    // ────────────────────────────────────────────────────────────────────
    //  DTO → view-model mapping. The XAML DataTemplates bind against
    //  TopAppRow / UncommonTalker (page-local records) — the wire types
    //  DailyReportAppRow / DailyReportTalker are mapped via these methods
    //  so the XAML doesn't have to change shape per phase. Icon glyph and
    //  secondary-line presentation logic stays UI-side.
    //  ────────────────────────────────────────────────────────────────────

    private static TopAppRow MapTopAppRow(DailyReportAppRow r)
    {
        var glyph = IsUnsignedish(r.SignatureStatus)
            ? SymbolRegular.ShieldError24
            : r.ImageName.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase)
                ? SymbolRegular.Home24
                : SymbolRegular.Globe24;
        var secondary = r.IsUserWritablePath ? AbbreviatePath(r.ImagePath) : "";
        return new TopAppRow(
            AppId:           r.AppId,
            ImageName:       r.ImageName,
            Publisher:       string.IsNullOrEmpty(r.Publisher) ? "(unknown)" : r.Publisher,
            SignatureStatus: r.SignatureStatus,
            BytesUp:         r.BytesUp,
            BytesDown:       r.BytesDown,
            IconGlyph:       glyph,
            SecondaryLine:   secondary,
            HasOverlap:      r.HasOverlap);
    }

    private static UncommonTalker MapTalker(DailyReportTalker t)
    {
        var glyph = IsUnsignedish(t.SignatureStatus)
            ? SymbolRegular.ShieldError24
            : SymbolRegular.Globe24;
        return new UncommonTalker(
            AppId:           t.AppId,
            ImageName:       t.ImageName,
            Publisher:       string.IsNullOrEmpty(t.Publisher) ? "(unknown)" : t.Publisher,
            SignatureStatus: t.SignatureStatus,
            Reason:          t.Reason,
            IconGlyph:       glyph,
            HasOverlap:      t.HasOverlap);
    }

    private static bool IsUnsignedish(string sig) => sig == "Unsigned" || sig == "Invalid";

    // Replace expanded env-vars with %VAR% tokens for display. Order
    // matters: LOCALAPPDATA lives under USERPROFILE, so the longer / more-
    // specific tokens have to be tried first. The mockup shows
    // %TEMP%\updater_x.exe for the canonical Unsigned row.
    private static string AbbreviatePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        foreach (var key in new[] { "TEMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE" })
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val)
                && path.StartsWith(val, StringComparison.OrdinalIgnoreCase))
            {
                return $"%{key}%" + path[val.Length..];
            }
        }
        return path;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Hero / sparkline application.
    //  ────────────────────────────────────────────────────────────────────

    private void ApplyHero(DailyReportHero hero)
    {
        var (totalStr, totalUnit) = FormatBytesPart(hero.TotalUpBytes + hero.TotalDownBytes);
        HeroTotalValue.Text = totalStr;
        HeroTotalUnit.Text  = totalUnit;

        var (upStr, upUnit) = FormatBytesPart(hero.TotalUpBytes);
        HeroUpValue.Text = upStr;
        HeroUpUnit.Text  = upUnit;

        var (downStr, downUnit) = FormatBytesPart(hero.TotalDownBytes);
        HeroDownValue.Text = downStr;
        HeroDownUnit.Text  = downUnit;

        HeroTotalDeltaText.Text = FormatDeltaPct(hero.TotalDeltaPct);
        HeroUpDeltaText.Text    = FormatDeltaPct(hero.UpDeltaPct);
        HeroDownDeltaText.Text  = FormatDeltaPct(hero.DownDeltaPct);

        ApplyBaselineSufficiency(hero.BaselineDaysAvailable);

        var wanPct   = (int)Math.Round(hero.WanRatio * 100);
        var localPct = Math.Max(0, 100 - wanPct);
        HeroWanPercent.Text   = $"{wanPct}%";
        HeroLocalPercent.Text = $"{localPct}%";
        // GridLength must be > 0 to render a column; floor at 1 to avoid
        // collapsing the bar when a side is 0% (would otherwise produce
        // an invisible WAN or Local bar segment).
        WanBarCol.Width   = new GridLength(Math.Max(1, hero.WanRatio * 100),   GridUnitType.Star);
        LocalBarCol.Width = new GridLength(Math.Max(1, hero.LocalRatio * 100), GridUnitType.Star);
    }

    // Phase 9.3 — fresh-install hero-deltas guard. The deltas come back from
    // the server already computed; this method picks the visual treatment
    // based on how many days of pre-report history the service has actually
    // observed (capped at the anchor's nominal size — see
    // DailyReportRepository.LoadBaselineDaysAvailable).
    //
    // Both partial-baseline warnings route through HeroBaselineNote (amber
    // status.caution.text on its own line below the headline) so the
    // "no comparison available" and "comparison is incomplete" cases read
    // with equal visual weight — both are honesty markers the user needs
    // to see, not severity-graded.
    //
    // < 3 days → treatment (a): chips collapsed, inline anchor caption
    // collapsed (the "vs 7-day avg" tag has nothing to reference), amber
    // note reads "Comparisons unlock on {date}".
    // 3..anchor-1 → treatment (b): chips visible, inline anchor caption
    // visible, amber note reads "Comparison based on N days of history…".
    // ≥ anchor → normal display, note collapsed.
    // SpecificDate is a UI-only placeholder for the MVP and is exempt.
    private void ApplyBaselineSufficiency(int available)
    {
        if (_anchor == AnchorMode.SpecificDate)
        {
            ShowDeltasAndAnchorCaption();
            HeroBaselineNote.Visibility = Visibility.Collapsed;
            return;
        }

        var required = AnchorRequiredDays(_anchor);
        if (available >= required)
        {
            ShowDeltasAndAnchorCaption();
            HeroBaselineNote.Visibility = Visibility.Collapsed;
        }
        else if (available < 3)
        {
            HeroTotalDeltaChip.Visibility  = Visibility.Collapsed;
            HeroUpDeltaChip.Visibility     = Visibility.Collapsed;
            HeroDownDeltaChip.Visibility   = Visibility.Collapsed;
            HeroAnchorCaption.Visibility   = Visibility.Collapsed;
            var unlockDate = DateTime.Today.AddDays(3 - available);
            HeroBaselineNote.Text =
                $"Comparisons unlock on {unlockDate.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}.";
            HeroBaselineNote.Visibility = Visibility.Visible;
        }
        else
        {
            ShowDeltasAndAnchorCaption();
            HeroBaselineNote.Text =
                $"Comparison based on {available} days of history. May not reflect typical usage.";
            HeroBaselineNote.Visibility = Visibility.Visible;
        }
    }

    private void ShowDeltasAndAnchorCaption()
    {
        HeroTotalDeltaChip.Visibility = Visibility.Visible;
        HeroUpDeltaChip.Visibility    = Visibility.Visible;
        HeroDownDeltaChip.Visibility  = Visibility.Visible;
        HeroAnchorCaption.Visibility  = Visibility.Visible;
        HeroAnchorCaption.Text        = AnchorVsCaption(_anchor);
    }

    private static int AnchorRequiredDays(AnchorMode mode) => mode switch
    {
        AnchorMode.Avg7d  => 7,
        AnchorMode.Avg30d => 30,
        AnchorMode.Avg90d => 90,
        _ => 1,
    };

    // Instance, not static, so the SpecificDate case can read
    // _anchorSpecificDate and render the actual chosen date. "vs yesterday"
    // is the fallback wording when SpecificDate is active but no date is
    // picked yet — matches the server's null → reportDate - 1 fallback.
    private string AnchorVsCaption(AnchorMode mode) => mode switch
    {
        AnchorMode.Avg30d       => "vs 30-day avg",
        AnchorMode.Avg90d       => "vs 90-day avg",
        AnchorMode.SpecificDate => _anchorSpecificDate is { } sd
            ? $"vs {sd.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}"
            : "vs yesterday",
        _                       => "vs 7-day avg",
    };

    private static (string Value, string Unit) FormatBytesPart(long bytes)
    {
        if (bytes <= 0) return ("0", "B");
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024.0 && u < units.Length - 1) { v /= 1024.0; u++; }
        return (v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture), units[u]);
    }

    private static string FormatDeltaPct(double pct)
    {
        var rounded = (int)Math.Round(pct, MidpointRounding.AwayFromZero);
        if (rounded > 0) return $"▲+{rounded}%";
        if (rounded < 0) return $"▼{Math.Abs(rounded)}%";
        return "0%";
    }

    private void ApplySparkline(IReadOnlyList<DailyReportHourPoint> hourly, DateTime reportDate)
    {
        _chartReportDate = reportDate;
        if (hourly.Count == 0)
        {
            SparklineChart.Series = Array.Empty<ISeries>();
            return;
        }

        var points = hourly
            .Select(p => new DateTimePoint(reportDate.AddHours(p.Hour), p.BytesDown))
            .ToList();
        var peak = hourly.OrderByDescending(p => p.BytesDown).First();
        _peakHour  = peak.Hour;
        _peakValue = peak.BytesDown;
        // 10 % head-room above the peak keeps the curve clear of the X-axis
        // label strip without compressing the trend at the top of the chart.
        _maxYValue = Math.Max(1, peak.BytesDown * 1.10);

        SparklineChart.Series = new ISeries[]
        {
            new LineSeries<DateTimePoint>
            {
                Name = "Down",
                Values = points,
                GeometrySize = 0,
                LineSmoothness = 0.5,
            },
        };

        // Re-prime the brand teal stroke + alpha-60 area fill. ChartTheming
        // documents this requirement (Services/ChartTheming.cs §ApplyToSeries):
        // Apply() in OnLoaded ran before any Series existed, so without an
        // explicit re-call after Series assignment, LC2 paints the new line
        // in its default palette. Apply on every refresh keeps theme flips
        // working too.
        ChartTheming.ApplyToSeries(SparklineChart.Series);

        if (_xAxis is not null)
        {
            _xAxis.MinLimit = reportDate.Ticks;
            _xAxis.MaxLimit = reportDate.AddHours(24).Ticks;
        }
        if (_yAxis is not null) _yAxis.MaxLimit = _maxYValue;

        // ChartTheming re-applies its own SeparatorsPaint when series
        // change; null them again so the X-axis labels paint without
        // vertical gridlines through the curve.
        StyleSparklineAxes();
        PeakLabel.Text = $"peak {peak.Hour:D2}:00";
        RepositionPeakOverlay();
    }

    private void InitSparkline()
    {
        // Chart axes / margin / chrome only. Data comes from ApplySparkline
        // once the first refresh result lands. Initial MinLimit/MaxLimit
        // point at _initialDate so the empty chart shows the correct hour
        // ticks (00:00 … 24:00) before any data arrives.
        _xAxis = new Axis
        {
            Labeler   = FormatHourTick,
            MinStep   = TimeSpan.FromHours(6).Ticks,
            UnitWidth = TimeSpan.FromHours(1).Ticks,
            MinLimit  = _initialDate.Ticks,
            MaxLimit  = _initialDate.AddHours(24).Ticks,
            TextSize  = 11,
        };
        _yAxis = new Axis
        {
            IsVisible = false,
            MinLimit  = 0,
            MaxLimit  = 1,
        };
        SparklineChart.XAxes = new[] { _xAxis };
        SparklineChart.YAxes = new[] { _yAxis };
        SparklineChart.Series = Array.Empty<ISeries>();
        SparklineChart.TooltipPosition = TooltipPosition.Hidden;
        SparklineChart.DrawMargin = new Margin(8, 18, 8, 30);
    }

    // After ChartTheming.Apply paints axis labels + tooltip + ApplyToSeries
    // sets the sparkline stroke + area fill, null out the X axis
    // SeparatorsPaint so the labels paint without vertical gridlines through
    // the curve.
    private void StyleSparklineAxes()
    {
        if (SparklineChart.XAxes is { } xa)
            foreach (var a in xa) a.SeparatorsPaint = null;
        if (SparklineChart.YAxes is { } ya)
            foreach (var a in ya) a.SeparatorsPaint = null;
    }

    // Compute peak (X, Y) in chart coords from the plot rectangle (the
    // chart canvas minus DrawMargin), then position the dashed guide, dot,
    // and label there. Fires on Loaded (initial layout) and SizeChanged
    // (resize / chrome reflow), and on every refresh result via
    // ApplySparkline.
    private void RepositionPeakOverlay()
    {
        var w = SparklineChart.ActualWidth;
        var h = SparklineChart.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var dm = SparklineChart.DrawMargin;
        if (dm is null) return;
        var plotW = w - dm.Left - dm.Right;
        var plotH = h - dm.Top - dm.Bottom;
        if (plotW <= 0 || plotH <= 0) return;

        var peakX = dm.Left + (_peakHour / 24.0) * plotW;
        var peakY = dm.Top + (1.0 - _peakValue / _maxYValue) * plotH;

        PeakLine.X1 = peakX;
        PeakLine.X2 = peakX;
        PeakLine.Y1 = dm.Top;
        PeakLine.Y2 = h - dm.Bottom;

        Canvas.SetLeft(PeakDot, peakX - PeakDot.Width / 2);
        Canvas.SetTop(PeakDot, peakY - PeakDot.Height / 2);

        PeakLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelW = PeakLabel.DesiredSize.Width;
        Canvas.SetLeft(PeakLabel, Math.Max(0, peakX - labelW + 4));
        Canvas.SetTop(PeakLabel, 0);
    }

    private string FormatHourTick(double ticks)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return string.Empty;
        var dt = new DateTime((long)ticks);
        // The day's right edge sits at _chartReportDate + 24h, which is
        // the next day at 00:00. Render that as "24:00" so the axis reads
        // as a full day, not as crossing midnight into tomorrow.
        if (dt.Date > _chartReportDate.Date && dt.Hour == 0) return "24:00";
        return dt.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.ContextMenu is { } cm)
        {
            cm.PlacementTarget = el;
            cm.Placement = PlacementMode.Custom;
            cm.CustomPopupPlacementCallback = RightAnchoredBelow;
            cm.IsOpen = true;
        }
    }

    // CSV export. SaveFileDialog seeds the brief §17 filename template
    // (zenvizor-report-YYYY-MM-DD.csv) and the user's Documents folder.
    // Errors surface via the existing Error state banner so the user
    // notices without a modal interrupting their flow.
    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        var report = _lastResult;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName       = $"zenvizor-report-{report.Date:yyyy-MM-dd}.csv",
            DefaultExt     = ".csv",
            Filter         = "CSV (Comma delimited)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            using var stream = File.Create(dlg.FileName);
            _csvWriter.Write(report, stream);
        }
        catch (Exception ex)
        {
            ApplyState(ReportsState.Error, ex.Message);
        }
    }

    // HTML export. After save, open in the default browser via
    // ShellExecute (matches the brief's intent: the file is the deliverable
    // and the user inspects it immediately). The HTML itself is
    // self-contained — opening it fires zero network requests (the brief's
    // §15 hard contract, verifiable via DevTools' network panel).
    private void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        var report = _lastResult;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName       = $"zenvizor-report-{report.Date:yyyy-MM-dd}.html",
            DefaultExt     = ".html",
            Filter         = "HTML document|*.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            using (var stream = File.Create(dlg.FileName))
            {
                _htmlWriter.Write(report, stream);
            }
            // UseShellExecute=true defers to the OS's default-handler
            // resolution — Edge, Chrome, Firefox, whichever the user picked
            // for .html. Without UseShellExecute the Process.Start API
            // expects an executable path, which doesn't work for documents.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ApplyState(ReportsState.Error, ex.Message);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Drill handlers — Top Apps row + Uncommon Talker row both navigate
    //  to AppDetailPage with the report date pre-applied (see
    //  DrillToAppDetail below).
    //  ────────────────────────────────────────────────────────────────────

    private void OnTopAppsGridLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
            element = VisualTreeHelper.GetParent(element);
        if (element is DataGridRow { DataContext: TopAppRow row })
            DrillToAppDetail(row.AppId);
    }

    private void OnTopAppsGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TopAppsGrid.SelectedItem is TopAppRow row)
        {
            DrillToAppDetail(row.AppId);
            e.Handled = true;
        }
    }

    // WPF DataGrid wraps its content in an internal ScrollViewer that
    // captures wheel events even when it has no rows to scroll past, which
    // traps the wheel and keeps the page from scrolling whenever the cursor
    // is over the grid. With EnforceTopAppsGridBound now applying a finite
    // MaxHeight (so virtualization engages), the grid CAN have internal
    // scroll to do — only forward to PageScroll when it doesn't, otherwise
    // the wheel can't scroll the grid's own rows.
    private void OnTopAppsGridPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var inner = FindDescendantScrollViewer(TopAppsGrid);
        if (inner is not null && CanScrollInDirection(inner, e.Delta))
        {
            // Let the grid handle its own wheel.
            return;
        }

        e.Handled = true;
        var fwd = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        };
        PageScroll.RaiseEvent(fwd);
    }

    private static bool CanScrollInDirection(ScrollViewer sv, int delta)
    {
        // delta > 0 means wheel-up (scroll toward the start). The viewer can
        // consume that gesture iff its current offset can still move toward
        // the requested edge — VerticalOffset > 0 for wheel-up,
        // VerticalOffset < ScrollableHeight for wheel-down. Without this
        // check, the forwarder hijacks the wheel even when the grid has
        // visible scroll headroom, which is precisely the bug the old
        // unconditional forwarder produced once MaxHeight kicked in.
        if (sv.ScrollableHeight <= 0) return false;
        return delta > 0
            ? sv.VerticalOffset > 0
            : sv.VerticalOffset < sv.ScrollableHeight;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindDescendantScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnUncommonRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        // The clicked element's DataContext is the bound UncommonTalker
        // (the DataTemplate root inherits the item's DataContext). Walk to a
        // FrameworkElement carrying it and read AppId.
        if (sender is FrameworkElement fe && fe.DataContext is UncommonTalker t)
        {
            DrillToAppDetail(t.AppId);
        }
        e.Handled = true;
    }

    // Drill to AppDetailPage with the report date so the user arrives
    // looking at that app's 24-hour traffic on the report day. The
    // navigation parameter is an AppDetailNavParams record so AppDetailPage
    // can disambiguate from the legacy "DataContext = bare int appId" path
    // (PerAppPage still uses that). Date passed only when _lastResult is
    // available; otherwise drill is best-effort with no date override.
    private void DrillToAppDetail(int appId)
    {
        var nav = FindNavigationView(this);
        if (nav is null) return;
        var date = _lastResult?.Date;
        nav.Navigate(typeof(AppDetailPage), new AppDetailNavParams(appId, date));
    }

    // Reports → Alerts deep-link. The chip is the only affordance on a
    // Notable card that targets the Alerts page; clicking anywhere else on
    // the card stays in Reports (no full-card drill). No-op when
    // AlertId == 0 — the LEFT JOIN didn't find a matching row, so there's
    // nothing to deep-link to.
    private void OnAlertsChipClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not NotableCardViewModel vm) return;
        if (vm.AlertId <= 0) return;

        var nav = FindNavigationView(this);
        if (nav is null) return;
        nav.Navigate(typeof(AlertsPage), new AlertsNavParams(vm.AlertId));
    }

    private static NavigationView? FindNavigationView(DependencyObject element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is NavigationView nv) return nv;
            current = VisualTreeHelper.GetParent(current)
                   ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  State machine — driven by RefreshAsync / ApplyResult. ApplyState is
    //  the single code path that toggles Visibility / Opacity / banner
    //  content / Export.IsEnabled; data values are not mutated by state
    //  methods — ApplyResult owns that.
    //  ────────────────────────────────────────────────────────────────────

    private void ApplyState(ReportsState s, string? errorMessage = null)
    {
        _state = s;
        ResetToBaseline();

        switch (s)
        {
            case ReportsState.Default:      ApplyDefaultState(); break;
            case ReportsState.Empty:        ApplyEmptyState(); break;
            case ReportsState.QuietDay:     ApplyQuietDayState(); break;
            case ReportsState.Loading:      ApplyLoadingState(); break;
            case ReportsState.Disconnected: ApplyDisconnectedState(); break;
            case ReportsState.Error:        ApplyErrorState(errorMessage ?? "unknown error."); break;
        }
    }

    private void ResetToBaseline()
    {
        _loadingCaptionTimer?.Stop();
        LoadingCaption.Visibility = Visibility.Collapsed;

        HeroCard.Visibility    = Visibility.Visible;
        NotableRow.Visibility  = Visibility.Visible;
        TopAppsCard.Visibility = Visibility.Visible;
        UncommonRow.Visibility = Visibility.Visible;
        HeroCard.Opacity    = 1.0;
        NotableRow.Opacity  = 1.0;
        TopAppsCard.Opacity = 1.0;
        UncommonRow.Opacity = 1.0;

        NotableNormal.Visibility  = Visibility.Visible;
        NotableQuiet.Visibility   = Visibility.Collapsed;
        UncommonNormal.Visibility = Visibility.Visible;
        UncommonQuiet.Visibility  = Visibility.Collapsed;

        LoadingPlaceholders.Visibility = Visibility.Collapsed;
        EmptyOverlay.Visibility        = Visibility.Collapsed;
        StatusBanner.Visibility        = Visibility.Collapsed;

        ExportButton.IsEnabled = true;
    }

    private static void ApplyDefaultState()
    {
        // Baseline IS the default — nothing extra to do.
    }

    private void ApplyEmptyState()
    {
        HeroCard.Visibility    = Visibility.Collapsed;
        NotableRow.Visibility  = Visibility.Collapsed;
        TopAppsCard.Visibility = Visibility.Collapsed;
        UncommonRow.Visibility = Visibility.Collapsed;

        EmptyTitle.Text =
            $"No traffic recorded on {PrimaryDatePicker.Date:ddd\\, MMM d yyyy}.";
        EmptyOverlay.Visibility = Visibility.Visible;
        ExportButton.IsEnabled  = false;
    }

    private void ApplyQuietDayState()
    {
        // Notable + Uncommon flip to their soft-success "quiet" variants.
        // Hero + Top Apps keep whatever ApplyResult painted — the small
        // numbers + reduced roster are characteristics of a quiet day's
        // payload, not chrome we apply on top.
        NotableNormal.Visibility  = Visibility.Collapsed;
        NotableQuiet.Visibility   = Visibility.Visible;
        UncommonNormal.Visibility = Visibility.Collapsed;
        UncommonQuiet.Visibility  = Visibility.Visible;
    }

    private void ApplyLoadingState()
    {
        HeroCard.Visibility    = Visibility.Collapsed;
        NotableRow.Visibility  = Visibility.Collapsed;
        TopAppsCard.Visibility = Visibility.Collapsed;
        UncommonRow.Visibility = Visibility.Collapsed;

        LoadingPlaceholders.Visibility = Visibility.Visible;
        ExportButton.IsEnabled         = false;

        _loadingCaptionTimer ??= CreateLoadingCaptionTimer();
        _loadingCaptionTimer.Start();
    }

    private void ApplyDisconnectedState()
    {
        // Service-disconnect is canonicalized to caution-amber +
        // PlugDisconnected20 glyph across every page. Don't reach for the
        // critical-red token here — "status.critical.text" doesn't exist,
        // SetResourceReference would silently leave the brush unresolved,
        // and banner text would paint with the inherited Foreground.
        SetBanner(SymbolRegular.PlugDisconnected20,
                  "status.caution.background",
                  "status.caution.text",
                  "Service disconnected. Last refresh stale.");
        DimDataRows(0.6);
        ExportButton.IsEnabled = false;
    }

    private void ApplyErrorState(string exceptionMessage)
    {
        SetBanner(SymbolRegular.Warning20,
                  "status.caution.background",
                  "status.caution.text",
                  $"Report failed: {exceptionMessage}");
        DimDataRows(0.6);
        ExportButton.IsEnabled = false;
    }

    private void SetBanner(SymbolRegular glyph, string backgroundKey, string foregroundKey, string text)
    {
        StatusBanner.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, backgroundKey);
        StatusBannerGlyph.Symbol = glyph;
        StatusBannerGlyph.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, foregroundKey);
        StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foregroundKey);
        StatusBannerText.Text = text;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void DimDataRows(double opacity)
    {
        HeroCard.Opacity    = opacity;
        NotableRow.Opacity  = opacity;
        TopAppsCard.Opacity = opacity;
        UncommonRow.Opacity = opacity;
    }

    private DispatcherTimer CreateLoadingCaptionTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            LoadingCaption.Visibility = Visibility.Visible;
            t.Stop();
        };
        return t;
    }
}

// ────────────────────────────────────────────────────────────────────
//  View-model records bound by the XAML DataTemplates. Top Apps mirrors
//  PerAppPage's AppRowViewModel vocabulary so the brand grid styles drop
//  in unchanged. IconGlyph is a placeholder until the real app-icon IPC
//  field lands (post-MVP); the small tile in the App cell uses it to
//  give each row a visual anchor.
//  ────────────────────────────────────────────────────────────────────

public sealed record TopAppRow(
    int AppId,
    string ImageName,
    string Publisher,
    string SignatureStatus,
    long BytesUp,
    long BytesDown,
    SymbolRegular IconGlyph,
    string SecondaryLine,
    bool HasOverlap)
{
    public string UpText => FormatBytes(BytesUp);
    public string DownText => FormatBytes(BytesDown);

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)
             + " " + units[unit];
    }
}

public sealed record UncommonTalker(
    int AppId,
    string ImageName,
    string Publisher,
    string SignatureStatus,
    string Reason,
    SymbolRegular IconGlyph,
    bool HasOverlap);

// Notable card view-model bound by reports.notable.card DataTemplate.
// EntityRef + AlertsText are pre-formatted strings so the DataTemplate can
// stay binding-only — no inline string formatting / converters in XAML.
// AlertId is the raw value used by the Alerts-chip deep-link click handler;
// AlertsText is the rendered "Alerts · #N" caption.
public sealed record NotableCardViewModel(
    NotableSeverity Severity,
    string Title,
    string Detail,
    string EntityRef,
    string AlertsText,
    int AlertId);
