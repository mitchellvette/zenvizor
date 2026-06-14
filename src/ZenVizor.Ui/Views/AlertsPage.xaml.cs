using System.Windows;
using System.Windows.Controls;
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

        // Phase 4a synthetic seed — Phase 4b removes this and pulls from
        // AlertsClient. The seed populates six sample alerts (one per
        // catalog type) so the heterogeneous feed renders for visual audit.
        _vm.SeedSyntheticForPhase4aPreview();
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
