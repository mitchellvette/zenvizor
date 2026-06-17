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

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class PerAppPage : Page
{
    private const double DimmedOpacity = 0.6;

    private readonly HistoryQueryClient _client = new();
    private readonly DispatcherTimer _loadingCaptionTimer;
    private readonly DispatcherTimer _filterDebounceTimer;
    private readonly CollectionViewSource _rowsView = new();

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
        WindowCombo.ItemsSource = WindowPreset.All;
        // Display is driven by the ComboBox.ItemTemplate in PerAppPage.xaml
        // (shorthand text + per-item ToolTip with the long Label). Setting
        // DisplayMemberPath here would throw InvalidOperationException because
        // ItemTemplate and DisplayMemberPath are mutually exclusive.
        WindowCombo.SelectedIndex = 1; // Last 24 hours

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
        SizeChanged += (_, _) => EnforceAppsGridBound();

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
            mw.HistoryWiped += OnHistoryWiped;
        }
        EnforceAppsGridBound();
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.HistoryWiped -= OnHistoryWiped;
        }
    }

    private async void OnHistoryWiped(object? sender, EventArgs e) => await RefreshAsync();

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
        if (!IsLoaded) return;
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (WindowCombo.SelectedItem is not WindowPreset preset) return;

        StartLoadingState();
        try
        {
            var result = await _client.GetAppListAsync(preset.ToWindow());
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
    /// Pipe down (caught via HistoryQueryClient.IsConnectionLost). Critical
    /// paint per brief §4. Rows + summary retain last-known values dimmed
    /// to DimmedOpacity — NOT cleared — so the user can still see the
    /// most recent state while disconnected.
    /// </summary>
    private void ApplyDisconnectedState()
    {
        _loadingCaptionTimer.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        EmptyFilteredText.Visibility = Visibility.Collapsed;

        StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.critical.background");
        StatusBannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "status.critical");
        StatusBannerText.Text = "Service disconnected. Last refresh stale.";
        StatusBanner.Visibility = Visibility.Visible;

        SummaryStrip.Opacity = DimmedOpacity;
        AppsGrid.Opacity = DimmedOpacity;
    }

    /// <summary>
    /// Non-connection query failure (e.g. SqliteException). Caution paint
    /// with the technical exception text — operators read this to triage.
    /// </summary>
    private void ApplyErrorState(Exception ex)
    {
        _loadingCaptionTimer.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        EmptyFilteredText.Visibility = Visibility.Collapsed;

        StatusBanner.SetResourceReference(Border.BackgroundProperty, "status.caution.background");
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
