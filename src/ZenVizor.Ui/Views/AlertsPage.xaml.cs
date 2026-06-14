using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Views;

/// <summary>
/// Alerts page. Phase 4a — page chrome + KPI strip + filter bar + state
/// shells + per-item template, populated with synthetic seed data from
/// <see cref="AlertsViewModel.SeedSyntheticForPhase4aPreview"/> so the
/// layout is visually verifiable without a Phase 6 producer running.
/// Phase 4b replaces the seed with a real
/// <c>AlertsClient.GetAlertsAsync</c> call on page load and wires the
/// <c>AlertRaised</c> push subscription.
/// </summary>
public partial class AlertsPage : Page
{
    private readonly AlertsViewModel _vm = new();

    public AlertsPage()
    {
        InitializeComponent();
        DataContext = _vm;

        // Right-anchor the Type filter ContextMenu under the button's right
        // edge so it stays on-screen when the button is near the window's
        // right edge. Without this the default Bottom placement anchors
        // LEFT-to-LEFT and the wider menu spills off-screen. Same pattern
        // the Reports Export menu uses.
        TypeFilterMenu.CustomPopupPlacementCallback = RightAnchoredBelow;

        // Phase 4a synthetic seed — Phase 4b removes this and pulls from
        // AlertsClient. The seed populates six sample alerts (one per
        // catalog type) so the heterogeneous feed renders for visual audit.
        _vm.SeedSyntheticForPhase4aPreview();
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
        // Placement="Custom" + the RightAnchoredBelow callback handle the
        // dropdown position; we just need to set the target and open it.
        if (sender is FrameworkElement el && el.ContextMenu is not null)
        {
            el.ContextMenu.PlacementTarget = el;
            el.ContextMenu.IsOpen = true;
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
