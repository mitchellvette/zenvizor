// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;
using ZenVizor.Ui.Views.Controls;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class PerAppPage : Page
{
    private const double DimmedOpacity = 0.6;

    // A2: assigned from MainWindow.HistoryQueryClient in OnLoaded.
    private HistoryQueryClient _client = null!;
    private readonly DispatcherTimer _loadingCaptionTimer;
    private readonly DispatcherTimer _filterDebounceTimer;
    private readonly CollectionViewSource _rowsView = new();

    // Epic A — combo items are WindowSelection (5 rolling presets + a
    // "Custom range…" sentinel pinned at the end + an optional ephemeral
    // Custom fixed entry inserted at position 0 when active).
    // ObservableCollection so the Custom fixed entry can be inserted /
    // removed in place on nav arrival / flyout Apply / preset pick.
    private readonly ObservableCollection<WindowSelection> _windowItems;

    // Re-entrancy guard for the sentinel revert in OnWindowSelectionChanged:
    // when the user picks "Custom range…" we set SelectedItem back to the
    // previous selection before opening the flyout, and that programmatic
    // revert would otherwise re-fire SelectionChanged.
    private bool _isInternalSelectionChange;

    // Backdrop dismiss tracking — a click counts as "outside dismiss" only
    // if BOTH MouseDown AND MouseUp landed on the backdrop. This naturally
    // ignores leaked MouseUp events from calendar date picks (the
    // CalendarDatePicker popup closes during the date MouseDown, the
    // matching MouseUp leaks to the now-exposed backdrop; since MouseDown
    // wasn't on backdrop, this flag stays false and dismissal is skipped).
    // Cleared on MouseLeave so a "drag-out without releasing on backdrop"
    // can't leave a stale true that fires on a later unrelated MouseUp.
    private bool _clickStartedOnBackdrop;

    // The custom-range flyout content. Built in code-behind (see ctor),
    // not declared in XAML, to sidestep the WPF SDK same-assembly
    // UserControl metadata gap.
    private readonly CustomRangeFlyout _customRangeContent;

    // _hasLoadedOnce gates the summary em-dash placeholder. Brief §4: the
    // em-dash only paints "until first paint completes" — subsequent
    // refreshes keep the previous values visible while the new query is in
    // flight, so the values don't flash em-dash on every refresh.
    private bool _hasLoadedOnce;

    // Cached normalized filter text — the source of truth the predicate
    // reads. FilterInput.Text is debounced into this field on the timer
    // Tick, so the predicate never runs on every keystroke.
    private string _filterText = string.Empty;

    public ObservableCollection<AppRowViewModel> Rows { get; } = new();

    public PerAppPage()
    {
        InitializeComponent();
        _windowItems = new ObservableCollection<WindowSelection>(
            WindowSelection.Presets.Append(WindowSelection.CustomSentinel));
        WindowCombo.ItemsSource = _windowItems;
        // Display is driven by the ComboBox.ItemTemplate in PerAppPage.xaml
        // (shorthand text + per-item ToolTip with the long Label). Setting
        // DisplayMemberPath here would throw InvalidOperationException because
        // ItemTemplate and DisplayMemberPath are mutually exclusive.
        WindowCombo.SelectedIndex = 1; // Last 24 hours

        // Epic A — accept a PerAppNavParams DataContext from the History
        // popover's "+N more" deep-link. Inserting a Custom entry happens
        // here (pre-Loaded), so the OnLoaded RefreshAsync picks it up
        // without an extra round-trip.
        DataContextChanged += (_, _) => OnNavParamsReceived();

        // Build the custom-range flyout in code-behind and assign it as
        // the chrome ContentControl's content. The flyout's UserControl
        // can't be referenced directly from this page's XAML — the
        // same-assembly MarkupCompilePass1 metadata gap drops UserControl
        // types from the _wpftmp temp project. Hosting via
        // ContentControl.Content in code sidesteps that.
        _customRangeContent = new CustomRangeFlyout();
        _customRangeContent.Applied   += OnCustomRangeApplied;
        _customRangeContent.Cancelled += OnCustomRangeCancelled;
        CustomRangeChromeContent.Content = BuildFlyoutChrome(_customRangeContent);

        // CollectionViewSource over Rows so the filter predicate layers on
        // top of the underlying ObservableCollection without touching it.
        // The DataGrid binds to the view (not Rows directly); filter changes
        // flow through View.Refresh() rather than mutating Rows. Summary
        // strip totals still compute from Rows so they describe the
        // UNFILTERED window (brief §4 lock).
        _rowsView.Source = Rows;
        _rowsView.Filter += OnFilter;
        AppsGrid.ItemsSource = _rowsView.View;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => { EnforceAppsGridBound(); PositionOverlay(); };

        _loadingCaptionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _loadingCaptionTimer.Tick += OnLoadingCaptionTick;

        // 150 ms debounce so the predicate runs once after a burst of
        // keystrokes, not on every TextChanged. ~150 ms is the brief's
        // tuning starting point; well below perceptible latency at human
        // typing speeds, well above keystroke-burst granularity.
        _filterDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _filterDebounceTimer.Tick += OnFilterDebounceTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            // A2: pick up the shared query client from MainWindow.
            _client = mw.HistoryQueryClient;
            mw.HistoryWiped += OnHistoryWiped;
            // A1: refresh on the disconnected→connected transition so
            // a service restart doesn't leave the page on the stale
            // pipe + stale data until the user re-navigates.
            mw.ServiceReconnected += OnServiceReconnected;
        }
        EnforceAppsGridBound();
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.HistoryWiped -= OnHistoryWiped;
            mw.ServiceReconnected -= OnServiceReconnected;
        }
    }

    private async void OnHistoryWiped(object? sender, EventArgs e) => await RefreshAsync();

    private async void OnServiceReconnected(object? sender, EventArgs e)
    {
        // A2: MainWindow.OnStatusChanged force-reconnected the shared
        // client before raising this event.
        await RefreshAsync();
    }

    /// <summary>
    /// See AppDetailPage.EnforceDataGridBounds for the rationale.
    /// Wpf.Ui's NavigationView hands its hosted pages unbounded vertical
    /// extent; setting MaxHeight explicitly is the most reliable way to make
    /// the inner DataGrid virtualize.
    /// </summary>
    private void EnforceAppsGridBound()
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        var cap = Math.Max(200, window.ActualHeight - 220);
        AppsGrid.MaxHeight = cap;
    }

    private async void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalSelectionChange) return;

        var selected = WindowCombo.SelectedItem as WindowSelection;

        // Sentinel — revert to the prior selection (carried in
        // e.RemovedItems) and open the custom-range overlay. The revert
        // is guarded so it doesn't re-fire SelectionChanged.
        if (selected is { IsSentinel: true })
        {
            var previous = e.RemovedItems.Count > 0
                ? e.RemovedItems[0] as WindowSelection
                : null;
            OpenCustomRangeFlyout(previous);
            return;
        }

        // Picking a real (rolling) preset retires any Custom entry that
        // the popover deep-link or a prior flyout Apply added — Custom is
        // ephemeral. The removed Fixed entry was never the selected item
        // (selection is the new preset), so this doesn't re-fire
        // SelectionChanged. Also dismiss the overlay if it's still up:
        // the WindowCombo stays clickable through/around the overlay, so
        // a user mid-overlay can pick a preset directly and expects the
        // overlay to close.
        if (selected is { IsFixed: false, IsSentinel: false })
        {
            RemoveCustomEntry();
            CustomRangeOverlay.Visibility = Visibility.Collapsed;
        }

        UpdateWindowRangeCaption(selected);

        if (!IsLoaded) return;
        await RefreshAsync();
    }

    /// <summary>
    /// Surface the active custom window's range as small caption text next
    /// to the WindowCombo. Visible only for Fixed selections (Custom from
    /// the flyout / popover deep-link); rolling presets are self-describing
    /// via the combo label itself, so the caption stays collapsed.
    /// </summary>
    private void UpdateWindowRangeCaption(WindowSelection? sel)
    {
        if (sel is { IsFixed: true, FixedWindow: { } fixedWin })
        {
            WindowRangeCaption.Text = WindowSelection.FormatRangeShort(fixedWin);
            WindowRangeCaption.Visibility = Visibility.Visible;
        }
        else
        {
            WindowRangeCaption.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenCustomRangeFlyout(WindowSelection? previous)
    {
        // Pre-populate the flyout from the currently active fixed window
        // (if any), otherwise from defaults (To = now snapped to 15 min,
        // From = To - 1 h).
        var currentFixed = previous?.FixedWindow;
        _isInternalSelectionChange = true;
        try
        {
            WindowCombo.SelectedItem = previous;
        }
        finally
        {
            _isInternalSelectionChange = false;
        }
        _customRangeContent.Open(currentFixed);
        CustomRangeOverlay.Visibility = Visibility.Visible;
        PositionOverlay();
    }

    /// <summary>
    /// Anchor the flyout chrome top-left under the WindowCombo's bottom-left
    /// corner. Runs on overlay open and on SizeChanged (in case the user
    /// resizes the window mid-flyout). TransformToVisual converts the
    /// combo's local coords to the overlay's parent coordinate space so the
    /// nesting doesn't matter.
    /// </summary>
    private void PositionOverlay()
    {
        if (CustomRangeOverlay.Visibility != Visibility.Visible) return;
        if (!WindowCombo.IsLoaded || !CustomRangeOverlay.IsLoaded) return;
        var origin = WindowCombo.TransformToVisual(CustomRangeOverlay)
            .Transform(new Point(0, WindowCombo.ActualHeight));
        // Round to whole pixels — TransformToVisual returns doubles, and
        // a fractional Margin shifts the chrome's children to sub-pixel
        // positions, which blurs small text (12px eyebrows) noticeably.
        // UseLayoutRounding on the ContentControl is the other half of
        // this fix; we round here too so the Margin itself is exact.
        CustomRangeChromeContent.Margin = new Thickness(
            Math.Round(origin.X), Math.Round(origin.Y + 4), 0, 0);
    }

    /// <summary>
    /// Down half of the backdrop dismiss pair. Sets the flag that
    /// <see cref="OnBackdropMouseUp"/> checks. e.Handled stops the
    /// MouseDown from bubbling to siblings (e.g. AppsGrid) — modal
    /// surfaces should fully absorb input that lands on the backdrop.
    /// </summary>
    private void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        _clickStartedOnBackdrop = true;
        e.Handled = true;
    }

    /// <summary>
    /// Up half of the backdrop dismiss pair. Dismisses only if the
    /// matching MouseDown also landed on the backdrop. Anything else
    /// (leaked MouseUp from a calendar date pick, drag-in from chrome,
    /// stray events) is ignored.
    /// </summary>
    private void OnBackdropMouseUp(object sender, MouseButtonEventArgs e)
    {
        var startedHere = _clickStartedOnBackdrop;
        _clickStartedOnBackdrop = false;
        if (!startedHere) return;
        e.Handled = true;
        OnCustomRangeCancelled(sender, EventArgs.Empty);
    }

    /// <summary>
    /// If a backdrop-down was followed by a drag onto chrome or a
    /// calendar popup (mouse leaves the backdrop), clear the flag so a
    /// later unrelated MouseUp on backdrop doesn't fire stale.
    /// </summary>
    private void OnBackdropMouseLeave(object sender, MouseEventArgs e)
    {
        _clickStartedOnBackdrop = false;
    }

    /// <summary>
    /// Wrap the flyout UserControl in the canonical card chrome
    /// (metal/border/radius/shadow) — same recipe as the InfoPopup at
    /// AppDetailPage.xaml:1339. Built in code (not XAML) for the same
    /// reason as the flyout itself: avoid the same-assembly UserControl
    /// metadata gap.
    ///
    /// Theme-flippable properties (Background / BorderBrush / Effect) use
    /// SetResourceReference rather than static FindResource so the chrome
    /// updates on runtime Light↔Dark switches. The chrome is built ONCE in
    /// the page ctor and reused across flyout opens; a static FindResource
    /// snapshot would freeze the chrome at construction-time theme, which
    /// produced a stale dark-mode rendering for pages constructed in light
    /// mode and viewed in dark (or vice versa). CornerRadius is a value
    /// type and theme-invariant, so it stays static.
    /// </summary>
    private static System.Windows.Controls.Border BuildFlyoutChrome(CustomRangeFlyout content)
    {
        var b = new System.Windows.Controls.Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20),
            Child = content,
            CornerRadius = (CornerRadius)Application.Current.FindResource("radius.card"),
        };
        b.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "surface.card");
        b.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "border.card");
        b.SetResourceReference(System.Windows.UIElement.EffectProperty, "shadow.card");
        return b;
    }

    private async void OnCustomRangeApplied(object? sender, QueryWindow window)
    {
        CustomRangeOverlay.Visibility = Visibility.Collapsed;
        // Replace any prior Custom entry and select the new one — this
        // fires SelectionChanged → RefreshAsync.
        RemoveCustomEntry();
        var custom = WindowSelection.FromFixedWindow(window);
        _windowItems.Insert(0, custom);
        WindowCombo.SelectedItem = custom;
        if (!IsLoaded) await RefreshAsync();
    }

    private void OnCustomRangeCancelled(object? sender, EventArgs e)
    {
        CustomRangeOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnNavParamsReceived()
    {
        if (DataContext is not PerAppNavParams nav) return;
        // Re-nav with a different window: scrub any prior Custom entry first
        // so we never accumulate.
        RemoveCustomEntry();
        var custom = WindowSelection.FromFixedWindow(nav.Window);
        _windowItems.Insert(0, custom);
        WindowCombo.SelectedItem = custom;
    }

    private void RemoveCustomEntry()
    {
        for (var i = _windowItems.Count - 1; i >= 0; i--)
        {
            if (_windowItems[i].IsFixed) _windowItems.RemoveAt(i);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (WindowCombo.SelectedItem is not WindowSelection sel) return;

        StartLoadingState();
        try
        {
            var result = await _client.GetAppListAsync(sel.ToWindow());
            ApplySuccessState(result.Apps);
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            ApplyDisconnectedState();
        }
        catch (Exception ex)
        {
            ApplyErrorState(ex);
        }
    }

    /// <summary>
    /// Flips the page into Loading: ProgressRing visible over the card body,
    /// caption hidden (DispatcherTimer reveals it after 1 s so quick refreshes
    /// don't flash text). Banner / empty-text collapsed; Opacity restored to 1
    /// in case we're entering Loading from a dimmed Disconnected/Error.
    /// Summary placeholder only paints on the very first load — after that
    /// the previous values stay visible until the new query lands.
    /// </summary>
    private void StartLoadingState()
    {
        StatusBanner.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        EmptyFilteredText.Visibility = Visibility.Collapsed;
        SummaryStrip.Opacity = 1.0;
        AppsGrid.Opacity = 1.0;
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingCaption.Visibility = Visibility.Collapsed;

        if (!_hasLoadedOnce)
        {
            SetSummaryToPlaceholder();
        }

        _loadingCaptionTimer.Stop();
        _loadingCaptionTimer.Start();
    }

    private void OnLoadingCaptionTick(object? sender, EventArgs e)
    {
        _loadingCaptionTimer.Stop();
        LoadingCaption.Visibility = Visibility.Visible;
    }

    private void ApplySuccessState(IReadOnlyList<AppListEntry> apps)
    {
        _loadingCaptionTimer.Stop();
        _hasLoadedOnce = true;

        Rows.Clear();
        foreach (var entry in apps)
        {
            Rows.Add(AppRowViewModel.From(entry));
        }

        UpdateSummary();

        LoadingOverlay.Visibility = Visibility.Collapsed;
        StatusBanner.Visibility = Visibility.Collapsed;
        SummaryStrip.Opacity = 1.0;
        AppsGrid.Opacity = 1.0;
        // EmptyText vs EmptyFilteredText resolved by UpdateOverlayVisibility:
        // success-with-zero (no rows from server) paints EmptyText; success
        // with rows but a filter that excludes all of them paints
        // EmptyFilteredText. Mutually exclusive.
        UpdateOverlayVisibility();
    }

    /// <summary>
    /// Pipe down (caught via HistoryQueryClient.IsConnectionLost). Phase 6.5
    /// standardized this to caution-amber + PlugDisconnected20 glyph across
    /// every page. Rows + summary retain last-known values dimmed to
    /// DimmedOpacity — NOT cleared — so the user can still see the most
    /// recent state while disconnected.
    /// </summary>
    private void ApplyDisconnectedState()
    {
        _loadingCaptionTimer.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        EmptyFilteredText.Visibility = Visibility.Collapsed;

        StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
        StatusBannerGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.PlugDisconnected20;
        StatusBannerGlyph.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "status.caution.text");
        StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
        StatusBannerText.Text = "Service disconnected. Last refresh stale.";
        StatusBanner.Visibility = Visibility.Visible;

        SummaryStrip.Opacity = DimmedOpacity;
        AppsGrid.Opacity = DimmedOpacity;
    }

    /// <summary>
    /// Non-connection query failure (e.g. SqliteException). Caution paint
    /// with the Warning20 glyph and the technical exception text — operators
    /// read this to triage.
    /// </summary>
    private void ApplyErrorState(Exception ex)
    {
        _loadingCaptionTimer.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        EmptyFilteredText.Visibility = Visibility.Collapsed;

        StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
        StatusBannerGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning20;
        StatusBannerGlyph.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "status.caution.text");
        StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
        StatusBannerText.Text = $"Query failed ({ex.GetType().Name}): {ex.Message}";
        StatusBanner.Visibility = Visibility.Visible;

        SummaryStrip.Opacity = DimmedOpacity;
        AppsGrid.Opacity = DimmedOpacity;
    }

    /// <summary>
    /// CollectionViewSource filter predicate — case-insensitive substring
    /// match against ImageName + PublisherDisplay. Runs once per row per
    /// View.Refresh(); reads from _filterText (set on the debounce tick).
    /// </summary>
    private void OnFilter(object sender, FilterEventArgs e)
    {
        if (string.IsNullOrEmpty(_filterText))
        {
            e.Accepted = true;
            return;
        }
        if (e.Item is not AppRowViewModel row)
        {
            e.Accepted = false;
            return;
        }
        e.Accepted = row.ImageName.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
                  || row.PublisherDisplay.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterInputChanged(object sender, TextChangedEventArgs e)
    {
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void OnFilterDebounceTick(object? sender, EventArgs e)
    {
        _filterDebounceTimer.Stop();
        _filterText = FilterInput.Text?.Trim() ?? string.Empty;
        _rowsView.View?.Refresh();
        UpdateOverlayVisibility();
    }

    /// <summary>
    /// Resolves EmptyText vs EmptyFilteredText vs neither. Three cases:
    ///  * Rows.Count == 0 — server returned no apps. EmptyText.
    ///  * Filter non-empty + view filtered to zero — EmptyFilteredText with
    ///    the typed filter interpolated.
    ///  * Otherwise — both collapsed (DataGrid carries the visible rows).
    /// </summary>
    private void UpdateOverlayVisibility()
    {
        var hasFilter = !string.IsNullOrEmpty(_filterText);
        var viewIsEmpty = _rowsView.View is null || _rowsView.View.IsEmpty;
        var rowsAreEmpty = Rows.Count == 0;

        if (rowsAreEmpty)
        {
            EmptyText.Visibility = Visibility.Visible;
            EmptyFilteredText.Visibility = Visibility.Collapsed;
        }
        else if (hasFilter && viewIsEmpty)
        {
            EmptyText.Visibility = Visibility.Collapsed;
            EmptyFilteredText.Text = $"No apps match \"{_filterText}\".";
            EmptyFilteredText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
            EmptyFilteredText.Visibility = Visibility.Collapsed;
        }
    }

    private void SetSummaryToPlaceholder()
    {
        SummaryAppsValue.Text = "—";
        SummaryUpValue.Text = "—";
        SummaryDownValue.Text = "—";

        SummaryAppsValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.tertiary");
        SummaryUpValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.tertiary");
        SummaryDownValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.tertiary");
    }

    /// <summary>
    /// Populates the row-0 summary strip from the currently loaded rows. Counts
    /// apps and sums raw bytes (NOT formatted strings) so totals never carry the
    /// rounding error baked into per-row humanization.
    /// </summary>
    private void UpdateSummary()
    {
        SummaryAppsValue.Text = Rows.Count.ToString(CultureInfo.InvariantCulture);
        SummaryUpValue.Text = FormatBytes(Rows.Sum(r => r.BytesUp));
        SummaryDownValue.Text = FormatBytes(Rows.Sum(r => r.BytesDown));

        SummaryAppsValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");
        SummaryUpValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");
        SummaryDownValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "text.primary");
    }

    /// <summary>
    /// Single-click drill — replaces the original double-click pattern.
    /// PreviewMouseLeftButtonUp fires before the cell consumes the click,
    /// so we capture every left-up over the grid and walk the visual tree
    /// up looking for a DataGridRow. Clicks on column headers or scrollbar
    /// parts won't have a DataGridRow ancestor, so they short-circuit.
    /// See memory: feedback_drill_grid_pattern.md.
    /// </summary>
    private void OnGridLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Epic A — while the custom-range overlay is up, the
        // CalendarDatePicker's calendar popup (a separate
        // AllowsTransparency HWND) can leak its date-cell clicks down to
        // the WPF window underneath. The grid is meant to be inert during
        // modal interaction; skip the drill so a calendar date pick
        // doesn't navigate to an app detail page.
        if (CustomRangeOverlay.Visibility == Visibility.Visible) return;

        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
            element = VisualTreeHelper.GetParent(element);

        if (element is DataGridRow { DataContext: AppRowViewModel row })
        {
            NavigateToDetail(row.AppId);
        }
    }

    /// <summary>
    /// Keyboard parity — Enter drills the focused row, mirroring single-click.
    /// Arrow keys move the SelectedItem via the DataGrid's default
    /// behavior; Enter on the focused row navigates.
    /// </summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AppsGrid.SelectedItem is AppRowViewModel row)
        {
            NavigateToDetail(row.AppId);
            e.Handled = true;
        }
    }

    private void NavigateToDetail(int appId)
    {
        var nav = FindNavigationView(this);
        nav?.Navigate(typeof(AppDetailPage), appId);
    }

    internal static NavigationView? FindNavigationView(DependencyObject element)
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
        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>
/// Epic A (1.1.0) — navigation parameter for opening <c>PerAppPage</c>
/// scoped to an arbitrary fixed <see cref="QueryWindow"/>. Used by the
/// History popover's "+N more" deep-link, where the popover's clicked
/// window becomes the windowed Per-App view. Bare-int / null DataContext
/// continues to land on the default rolling preset (Last 24 hours).
/// </summary>
public sealed record PerAppNavParams(QueryWindow Window);

public sealed record AppRowViewModel(
    int AppId,
    string ImageName,
    string ImagePath,
    string PublisherDisplay,
    string SignatureStatus,
    string UpText,
    string DownText,
    long BytesUp,
    long BytesDown,
    long TotalBytes)
{
    public static AppRowViewModel From(AppListEntry e) => new(
        AppId: e.AppId,
        ImageName: e.ImageName,
        ImagePath: e.ImagePath,
        PublisherDisplay: string.IsNullOrEmpty(e.Publisher) ? "(unknown)" : e.Publisher,
        SignatureStatus: e.SignatureStatus,
        UpText: PerAppPage.FormatBytes(e.BytesUp),
        DownText: PerAppPage.FormatBytes(e.BytesDown),
        BytesUp: e.BytesUp,
        BytesDown: e.BytesDown,
        TotalBytes: e.BytesUp + e.BytesDown);
}
