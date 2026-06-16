using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

/// <summary>
/// Alerts page. Phase 4b — real <c>AlertsClient.GetAlertsAsync</c>
/// round-trip on page Loaded, push subscription for <c>AlertRaised</c>,
/// status-banner wiring for disconnected / query-failure states, and
/// nav-rail badge updates driven from the view-model's KPI counts.
/// Page-level VM still owns the active set; Phase 5+ may lift it to
/// MainWindow scope so the badge stays authoritative regardless of which
/// State chip the user has selected.
/// </summary>
public partial class AlertsPage : Page
{
    private readonly AlertsViewModel _vm = new();
    private AlertsClient? _alertsClient;

    public AlertsPage()
    {
        InitializeComponent();
        DataContext = _vm;

        // Subscribe/unsubscribe paired on Loaded/Unloaded so navigating
        // away from a cached page (NavigationCacheMode.Enabled) tears
        // down the push subscription cleanly. Subsequent navigation back
        // re-attaches and triggers a fresh RefreshAsync so the feed
        // catches up on alerts that arrived while we were detached.
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Resolve the shared AlertsClient owned by MainWindow. Both the
        // nav-rail badge handler (MainWindow.OnAlertRaised) and this page
        // subscribe to the same instance's events so they see identical
        // pushes at the same moment.
        if (_alertsClient is null)
        {
            _alertsClient = (Application.Current.MainWindow as MainWindow)?.AlertsClient;
        }
        if (_alertsClient is null) return;

        _alertsClient.AlertRaised += OnServiceAlertRaised;
        _vm.PropertyChanged += OnVmPropertyChanged;

        await RefreshAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_alertsClient is not null)
        {
            _alertsClient.AlertRaised -= OnServiceAlertRaised;
        }
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    // ────────────────────────────────────────────────────────────────────
    //  RefreshAsync — single entry point. Loading → IPC → result classify
    //  (LoadAlerts) or catch → Disconnected / Error. Mirrors the
    //  HistoryQueryClient.IsConnectionLost pattern from
    //  HistoryPage / AppDetailPage / PerAppPage / ReportsPage.
    // ────────────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (_alertsClient is null) return;

        _vm.SetLoading();
        // Capture the State filter we're requesting so we can drop a
        // stale response if the user flips the SHOW chip mid-flight.
        // OnVmPropertyChanged(SelectedState) re-fires RefreshAsync on the
        // new state immediately, so the latest request always wins.
        var requestState = _vm.SelectedState;
        var filter = new AlertsFilter(requestState);

        try
        {
            var result = await _alertsClient.GetAlertsAsync(filter);

            // Stale-response guard.
            if (_vm.SelectedState != requestState) return;

            _vm.LoadAlerts(result.Alerts);
            _vm.ClearBanner();
            UpdateNavBadge();
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            if (_vm.SelectedState != requestState) return;
            _vm.SetBanner(
                AlertsViewModel.BannerState.Disconnected,
                "Service disconnected. Last refresh stale.");
        }
        catch (Exception ex)
        {
            if (_vm.SelectedState != requestState) return;
            _vm.SetBanner(
                AlertsViewModel.BannerState.Error,
                $"Query failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private void OnServiceAlertRaised(object? sender, AlertDto alert)
    {
        // StreamJsonRpc dispatches the server's NotifyAsync callback on a
        // thread-pool thread; mutating the VM (which raises PropertyChanged
        // → visual-tree updates) must run on the UI dispatcher. Matches
        // MainWindow.OnAlertRaised + OnActivitySnapshot pattern.
        Dispatcher.Invoke(() =>
        {
            _vm.OnAlertRaised(alert);
            UpdateNavBadge();
        });
    }

    private async void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AlertsViewModel.SelectedState):
                // State axis is server-applied per brief §14 — re-query
                // with the new filter. Severity + Type axes are filtered
                // in-memory by the VM setters and don't trigger a
                // round-trip.
                await RefreshAsync();
                break;
            case nameof(AlertsViewModel.Banner):
            case nameof(AlertsViewModel.BannerMessage):
                // Both cases fire ApplyBannerToUi as belt-and-suspenders:
                // SetBanner's intended order is BannerMessage→Banner so
                // the Banner case alone suffices, but listening for
                // BannerMessage too means a future caller setting them
                // in either order (or only BannerMessage) keeps the UI
                // in sync without re-introducing the order bug.
                ApplyBannerToUi();
                break;
        }
    }

    /// <summary>
    /// Pushes the VM's current KPI surface to the nav-rail badge.
    /// <para>
    /// Known limitation in Phase 4b: <c>VM.ActiveCount</c> derives from
    /// <c>_allAlerts.Count(non-dismissed)</c>, and <c>_allAlerts</c> only
    /// holds rows matching the current State filter. When the user views
    /// State=Dismissed, the badge reads 0 transiently. Acceptable for
    /// Phase 4b; Phase 5+ may lift the VM to app-level scope or add a
    /// count-summary IPC payload so the badge stays authoritative across
    /// view filters.
    /// </para>
    /// </summary>
    private void UpdateNavBadge()
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.UpdateAlertsBadge(_vm.ActiveCount, _vm.HighestActiveSeverity);
        }
    }

    /// <summary>
    /// Applies the VM's <see cref="AlertsViewModel.Banner"/> state to the
    /// inline <c>StatusBanner</c> Border above the feed. Follows the
    /// HistoryPage / AppDetailPage pattern of SetResourceReference for
    /// the background / glyph / text foreground brushes so the banner
    /// theme-swaps correctly in HC. No em-dash in copy
    /// (memory: feedback_no_emdash_in_ui_copy).
    /// </summary>
    private void ApplyBannerToUi()
    {
        switch (_vm.Banner)
        {
            case AlertsViewModel.BannerState.Disconnected:
                // Caution-amber, not critical-red. Alerts is the page where
                // disconnect is the LEAST sensational event we can be in
                // (the feed itself is non-critical state — there are no
                // alerts to show when the service is down because none can
                // be produced) so the brief's tiered convention puts this
                // at caution rather than critical. The PlugDisconnected20
                // glyph + the literal "Service disconnected" copy carry
                // the semantics; the amber tint says "informational, not
                // alarming". Other pages (History / AppDetail / Reports)
                // paint disconnect red because their data is operational
                // and going stale matters more.
                StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
                StatusBannerGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.PlugDisconnected20;
                StatusBannerGlyph.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "status.caution.text");
                StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
                StatusBannerText.Text = _vm.BannerMessage;
                StatusBanner.Visibility = Visibility.Visible;
                break;
            case AlertsViewModel.BannerState.Error:
                StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
                StatusBannerGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning20;
                StatusBannerGlyph.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "status.caution.text");
                StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.caution.text");
                StatusBannerText.Text = _vm.BannerMessage;
                StatusBanner.Visibility = Visibility.Visible;
                break;
            case AlertsViewModel.BannerState.None:
            default:
                StatusBanner.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>
    /// CustomPopupPlacementCallback that anchors the popup's RIGHT edge under
    /// the placement target's RIGHT edge. Returns one candidate position
    /// (X = -(popupWidth - targetWidth), Y = targetHeight) — WPF then handles
    /// on-screen clamping against the working area.
    /// </summary>
    private static CustomPopupPlacement[] RightAnchoredBelow(
        Size popupSize, Size targetSize, Point offset)
    {
        var x = -(popupSize.Width - targetSize.Width);
        var y = targetSize.Height;
        return new[]
        {
            new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal),
        };
    }

    // ---- Per-item "View app" drill -----------------------------------------
    //
    // Navigates to AppDetailPage scoped to the alert's source app. Reuses the
    // bare-int navigation parameter shape AppDetailPage already supports
    // (legacy PerApp path) — Alerts has no date override to apply, so the
    // AppDetailNavParams envelope isn't needed.

    private void OnViewAppClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not AlertVm av) return;
        if (av.SourceAppId is not int appId) return;
        var nav = FindNavigationView(this);
        if (nav is null) return;
        nav.Navigate(typeof(AppDetailPage), appId);
        e.Handled = true;
    }

    /// <summary>
    /// Walks the visual + logical tree from <paramref name="element"/> up to
    /// the hosting <see cref="Wpf.Ui.Controls.NavigationView"/>. Same shape
    /// as ReportsPage.FindNavigationView; kept local because the helper is
    /// trivial and duplicating it avoids a cross-page utility class
    /// solely for this one walk.
    /// </summary>
    private static Wpf.Ui.Controls.NavigationView? FindNavigationView(DependencyObject element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is Wpf.Ui.Controls.NavigationView nv) return nv;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                   ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    // ---- Per-item "Why this matters" expand --------------------------------
    //
    // The whole chevron+label row catches MouseLeftButtonUp via a transparent
    // hit-test Background on the wrapping StackPanel (without it, only the
    // glyph and text would catch clicks; the gap between them would fall
    // through). Handled=true stops the bubble before ListViewItem's default
    // selection handler runs — the toggle is a self-contained per-row
    // interaction, not a row selection event.

    private void OnWhyMattersClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not AlertVm av) return;
        av.ToggleExpanded();
        e.Handled = true;
    }

    // ---- Per-item Dismiss flow ---------------------------------------------
    //
    // Optimistic: flip the AlertVm immediately (parent VM re-runs KPI + filter,
    // so the card vanishes from State=Active views and the nav badge
    // decrements right away). Then await the server. On failure, roll back the
    // VM and surface the error in the inline status banner — the next
    // RefreshAsync replaces it.

    private async void OnDismissAlertClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not AlertVm av) return;
        if (_alertsClient is null) return;
        if (av.IsDismissed) return;  // re-entrancy / double-click guard

        var alertId = av.AlertId;
        var whenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _vm.MarkAlertDismissed(alertId, whenUnixMs);
        UpdateNavBadge();

        try
        {
            await _alertsClient.DismissAlertAsync(alertId);
        }
        catch (Exception ex)
        {
            // Roll the optimistic update back; the card returns to the
            // active state and the badge re-increments. Surface the error
            // in the banner so the user knows the dismiss didn't persist.
            // No special-case for IsConnectionLost — the same recovery
            // (rollback + visible message) applies either way; a subsequent
            // RefreshAsync will paint the Disconnected banner if relevant.
            _vm.RollbackDismiss(alertId);
            UpdateNavBadge();
            _vm.SetBanner(
                AlertsViewModel.BannerState.Error,
                $"Couldn't dismiss alert ({ex.GetType().Name}): {ex.Message}");
        }
    }

    // ---- State filter chips ------------------------------------------------
    //
    // RadioButton group with three options (Active / Dismissed / All).
    // The Click handler maps the named element to the AlertState enum and
    // assigns it to the view-model, which re-applies the filter.

    private void OnStateChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.IsChecked != true) return;
        _vm.SelectedState = rb.Name switch
        {
            nameof(StateActiveChip)    => AlertState.Active,
            nameof(StateDismissedChip) => AlertState.Dismissed,
            nameof(StateAllChip)       => AlertState.All,
            _                          => AlertState.Active,
        };
    }

    private void OnResetFilterClick(object sender, RoutedEventArgs e)
    {
        _vm.ResetFilter();
        // Reset the chip group to Active visually — the binding from the
        // VM property doesn't reach RadioButton.IsChecked when the chips
        // are templated to GroupName="State"; assigning IsChecked here
        // makes the visual state consistent with VM.SelectedState=Active.
        StateActiveChip.IsChecked = true;
        // Re-check every type menu item — the VM rebuilt EnabledTypes to
        // the full catalog set, but MenuItem.IsChecked is local visual state
        // not bound to the VM, so it needs to be re-synced here. Same
        // pattern the chip group uses above.
        TypeUnsignedItem.IsChecked = true;
        TypeInvalidSignatureItem.IsChecked = true;
        TypeFirstRunItem.IsChecked = true;
        TypeUnusualVolumeItem.IsChecked = true;
        TypeLargeDownloadItem.IsChecked = true;
        TypeOutboundHeavyItem.IsChecked = true;
    }

    // ---- Type filter ContextMenu -------------------------------------------
    //
    // The Wpf.Ui ComboBox does not support multi-select; the Reports page's
    // Anchor picker pattern (ui:Button + ContextMenu) is the canonical
    // multi-option dropdown idiom in this codebase. For Alerts we use
    // IsCheckable MenuItems with StaysOpenOnClick=True so the user can
    // toggle multiple types in a single open.

    private void OnTypeFilterButtonClick(object sender, RoutedEventArgs e)
    {
        // Set ALL FOUR placement properties together immediately before
        // IsOpen=true, mirroring the Reports Anchor / Export pattern
        // (ReportsPage.xaml.cs:213-222). Setting Placement and
        // CustomPopupPlacementCallback at XAML-load or page-ctor time
        // leaves them vulnerable to a WPF / Wpf.Ui lifecycle reset
        // between page-load and click — the popup positioning then sees
        // Placement=Custom with a null callback and throws an NRE deep
        // in the framework, crashing the process. Per-click assignment
        // guarantees both values are present at the moment of opening.
        if (sender is FrameworkElement el && el.ContextMenu is { } cm)
        {
            cm.PlacementTarget = el;
            cm.Placement = PlacementMode.Custom;
            cm.CustomPopupPlacementCallback = RightAnchoredBelow;
            cm.IsOpen = true;
        }
    }

    private void OnTypeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (mi.Tag is not string tag) return;
        if (!Enum.TryParse<AlertType>(tag, out var type)) return;
        // IsCheckable=True flips MenuItem.IsChecked BEFORE Click fires, so
        // mi.IsChecked here is the post-toggle value. Push to the VM, which
        // re-applies the filter and re-fires TypeFilterLabel / TypeFilterTooltip
        // PropertyChanged so the button label and hover tip update.
        _vm.SetTypeEnabled(type, mi.IsChecked);
    }

    // ---- Header-row bulk actions -------------------------------------------
    //
    // Both handlers walk the six named type-menu items, set IsChecked to the
    // target state, and push the matching VM toggle. The menu stays open
    // (StaysOpenOnClick=True on the type items) so the user sees the result
    // immediately. The header-row Buttons themselves don't carry
    // StaysOpenOnClick (they live inside a MenuItem.Header that's not a
    // checkable MenuItem) — WPF's default ContextMenu close-on-Button-click
    // behavior is fine since these are bulk actions; the user reviews the
    // closed-state button label to confirm.

    private void OnSelectAllTypesClick(object sender, RoutedEventArgs e)
    {
        TypeUnsignedItem.IsChecked = true;
        TypeInvalidSignatureItem.IsChecked = true;
        TypeFirstRunItem.IsChecked = true;
        TypeUnusualVolumeItem.IsChecked = true;
        TypeLargeDownloadItem.IsChecked = true;
        TypeOutboundHeavyItem.IsChecked = true;
        foreach (var type in Enum.GetValues<AlertType>())
        {
            _vm.SetTypeEnabled(type, true);
        }
    }

    private void OnClearAllTypesClick(object sender, RoutedEventArgs e)
    {
        TypeUnsignedItem.IsChecked = false;
        TypeInvalidSignatureItem.IsChecked = false;
        TypeFirstRunItem.IsChecked = false;
        TypeUnusualVolumeItem.IsChecked = false;
        TypeLargeDownloadItem.IsChecked = false;
        TypeOutboundHeavyItem.IsChecked = false;
        foreach (var type in Enum.GetValues<AlertType>())
        {
            _vm.SetTypeEnabled(type, false);
        }
    }

    // ---- ListView virtualization gate --------------------------------------
    //
    // Wpf.Ui's NavigationView wraps hosted pages in a DynamicScrollViewer
    // that grants infinite vertical measure (memory:
    // project_wpfui_navigationview_scrollviewer.md). Without an explicit
    // MaxHeight, the ListView's VirtualizingStackPanel materializes every
    // item at once and breaks under load. The MaxHeight tracks the page's
    // available height at Loaded + SizeChanged.

    private void OnAlertsListLoaded(object sender, RoutedEventArgs e)
        => UpdateAlertsListMaxHeight();

    private void OnAlertsListSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateAlertsListMaxHeight();

    private void UpdateAlertsListMaxHeight()
    {
        // Available height = page height − header row − filter bar row −
        // banner row − margins. Approximated here as ActualHeight minus a
        // fixed safety constant; the value gets tighter as Phase 4b binds
        // the actual chrome heights. The ListView itself MaxHeight-clamps
        // its inner VirtualizingStackPanel; an exact value isn't required
        // for virtualization to engage, only a finite one.
        if (ActualHeight <= 0) return;
        AlertsList.MaxHeight = Math.Max(0, ActualHeight - 280);
    }
}
