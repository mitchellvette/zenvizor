using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class AppDetailPage : Page
{
    private readonly HistoryQueryClient _client = new();

    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = new();
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    public int? AppId { get; private set; }

    public AppDetailPage()
    {
        InitializeComponent();

        WindowCombo.ItemsSource = WindowPreset.All;
        WindowCombo.DisplayMemberPath = nameof(WindowPreset.Label);
        WindowCombo.SelectedIndex = 1;

        // Axes set once; Series reassigned wholesale on each refresh below.
        SeriesChart.XAxes = new[]
        {
            new Axis { Labeler = ticks => new DateTime((long)ticks).ToString("HH:mm", CultureInfo.InvariantCulture) },
        };
        SeriesChart.YAxes = new[]
        {
            new Axis { Labeler = v => PerAppPage.FormatBytes((long)v) + "/bucket" },
        };
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
    /// </summary>
    private void EnforceDataGridBounds()
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        var cap = Math.Max(200, (window.ActualHeight - 220) / 2);
        ConnectionsGrid.MaxHeight = cap;
        SessionsGrid.MaxHeight = cap;
    }

    private void ApplyChartTheme() => ChartTheming.Apply(SeriesChart);

    private void OnAppIdReceived()
    {
        AppId = DataContext switch
        {
            int i => i,
            long l => (int)l,
            _ => null,
        };
        HeaderText.Text = AppId is null
            ? "App detail"
            : $"App detail (app id {AppId})";
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
        HeaderText.Text = $"{s.ImageName} (app id {s.AppId})";
        SummaryLine1.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Publisher: {0}   |   Signature: {1}{2}   |   Grain: {3}",
            string.IsNullOrEmpty(s.Publisher) ? "(unknown)" : s.Publisher,
            s.SignatureStatus,
            s.IsUserWritablePath ? "  [user-writable path]" : "",
            detail.GrainUsed);
        SummaryLine2.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Path: {0}   |   Up: {1}   |   Down: {2}",
            s.ImagePath,
            PerAppPage.FormatBytes(s.BytesUp),
            PerAppPage.FormatBytes(s.BytesDown));

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
        (upPoints, downPoints) = ChartSeriesDownsampler.Downsample(upPoints, downPoints);

        SeriesChart.Series = ChartBuilder.BuildSeries(detail.GrainUsed, upPoints, downPoints);
        ChartSubtitle.Text = ChartBuilder.DescribeView(detail.GrainUsed,
            WindowCombo.SelectedItem as WindowPreset);

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
