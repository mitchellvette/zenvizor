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
public partial class HistoryPage : Page
{
    private readonly HistoryQueryClient _client = new();

    public HistoryPage()
    {
        InitializeComponent();

        WindowCombo.ItemsSource = WindowPreset.All;
        WindowCombo.DisplayMemberPath = nameof(WindowPreset.Label);
        WindowCombo.SelectedIndex = 1; // Last 24h

        // Axes set once; Series reassigned wholesale on each refresh below.
        HistoryChart.XAxes = new[]
        {
            new Axis { Labeler = ticks => new DateTime((long)ticks).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) },
        };
        HistoryChart.YAxes = new[]
        {
            new Axis { Labeler = v => PerAppPage.FormatBytes((long)v) + "/bucket" },
        };
        ApplyChartTheme();
        ChartTheming.Changed += () => Dispatcher.Invoke(ApplyChartTheme);

        Loaded += async (_, _) => await RefreshAsync();
    }

    private void ApplyChartTheme() => ChartTheming.Apply(HistoryChart);

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
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

            // Always Auto — the server picks the right tier for the window span.
            var result = await _client.GetTrafficHistoryAsync(preset.ToWindow(), TrafficGrain.Auto);
            ApplyResult(result);
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

    private void ApplyResult(TrafficHistoryResult result)
    {
        var bucketsUp = new SortedDictionary<long, long>();
        var bucketsDown = new SortedDictionary<long, long>();
        foreach (var p in result.Series)
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

        HistoryChart.Series = ChartBuilder.BuildSeries(result.GrainUsed, upPoints, downPoints);
        ChartSubtitle.Text = ChartBuilder.DescribeView(result.GrainUsed,
            WindowCombo.SelectedItem as WindowPreset);

        NoDataOverlay.Visibility = upPoints.Count == 0 && downPoints.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var totalUp = result.Series.Sum(p => p.BytesUp);
        var totalDown = result.Series.Sum(p => p.BytesDown);
        SummaryLine.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0} buckets   |   Up: {1}   |   Down: {2}",
            result.Series.Count,
            PerAppPage.FormatBytes(totalUp),
            PerAppPage.FormatBytes(totalDown));
    }
}
