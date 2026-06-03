using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class PerAppPage : Page
{
    private readonly HistoryQueryClient _client = new();
    public ObservableCollection<AppRowViewModel> Rows { get; } = new();

    public PerAppPage()
    {
        InitializeComponent();
        WindowCombo.ItemsSource = WindowPreset.All;
        WindowCombo.DisplayMemberPath = nameof(WindowPreset.Label);
        WindowCombo.SelectedIndex = 1; // Last 24 hours
        AppsGrid.ItemsSource = Rows;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => EnforceAppsGridBound();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnforceAppsGridBound();
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
        if (!IsLoaded) return;
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (WindowCombo.SelectedItem is not WindowPreset preset) return;

        try
        {
            StatusBanner.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = Cursors.Wait;

            var result = await _client.GetAppListAsync(preset.ToWindow());
            Rows.Clear();
            foreach (var entry in result.Apps)
            {
                Rows.Add(AppRowViewModel.From(entry));
            }
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

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRowViewModel row) return;
        var nav = FindNavigationView(this);
        nav?.Navigate(typeof(AppDetailPage), row.AppId);
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
    string PublisherDisplay,
    string SignatureStatus,
    string UpText,
    string DownText,
    long TotalBytes)
{
    public static AppRowViewModel From(AppListEntry e) => new(
        AppId: e.AppId,
        ImageName: e.ImageName,
        PublisherDisplay: string.IsNullOrEmpty(e.Publisher) ? "(unknown)" : e.Publisher,
        SignatureStatus: e.SignatureStatus,
        UpText: PerAppPage.FormatBytes(e.BytesUp),
        DownText: PerAppPage.FormatBytes(e.BytesDown),
        TotalBytes: e.BytesUp + e.BytesDown);
}
