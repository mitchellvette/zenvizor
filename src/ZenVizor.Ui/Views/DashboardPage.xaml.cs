using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class DashboardPage : Page
{
    // 60 polled points at 2 s cadence = ~2 min of trailing chart history.
    private const int ChartHistoryPoints = 60;

    private readonly ActivitySnapshotPoller _poller;
    private readonly ObservableCollection<DateTimePoint> _upSeries = new();
    private readonly ObservableCollection<DateTimePoint> _downSeries = new();

    public ObservableCollection<TalkerRowViewModel> Talkers { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();

        RatesChart.Series = new ISeries[]
        {
            new LineSeries<DateTimePoint>
            {
                Name = "Up B/s",
                Values = _upSeries,
                GeometrySize = 0,
            },
            new LineSeries<DateTimePoint>
            {
                Name = "Down B/s",
                Values = _downSeries,
                GeometrySize = 0,
            },
        };
        RatesChart.XAxes = new[]
        {
            new Axis { Labeler = ticks => new DateTime((long)ticks).ToString("HH:mm:ss") },
        };
        RatesChart.YAxes = new[]
        {
            new Axis { Labeler = v => FormatRate(v) },
        };

        TalkersList.ItemsSource = Talkers;

        _poller = new ActivitySnapshotPoller();
        _poller.SnapshotReceived += OnSnapshotReceived;

        Loaded += (_, _) => _poller.Start();
        Unloaded += (_, _) => _poller.Dispose();
    }

    private void OnSnapshotReceived(object? sender, ActivitySnapshotUpdate update)
    {
        Dispatcher.Invoke(() => ApplyUpdate(update));
    }

    private void ApplyUpdate(ActivitySnapshotUpdate update)
    {
        if (!update.IsConnected || update.Envelope is null)
        {
            DisconnectedBanner.Visibility = Visibility.Visible;
            DisconnectedText.Text = $"service disconnected ({update.FailureReason})";
            // Keep the existing chart history visible — don't blank it just because
            // one poll failed. The banner makes the staleness explicit.
            return;
        }
        DisconnectedBanner.Visibility = Visibility.Collapsed;

        var snap = update.Envelope.Payload;
        if (snap.WindowSeconds <= 0 || snap.Apps.Count == 0)
        {
            WarmingBanner.Visibility = Visibility.Visible;
            Talkers.Clear();
            return;
        }
        WarmingBanner.Visibility = Visibility.Collapsed;

        var totalUp = snap.Apps.Sum(a => a.BytesUpPerSec);
        var totalDown = snap.Apps.Sum(a => a.BytesDownPerSec);
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(snap.CapturedAtUnixMs).LocalDateTime;

        _upSeries.Add(new DateTimePoint(ts, totalUp));
        _downSeries.Add(new DateTimePoint(ts, totalDown));
        while (_upSeries.Count > ChartHistoryPoints) _upSeries.RemoveAt(0);
        while (_downSeries.Count > ChartHistoryPoints) _downSeries.RemoveAt(0);

        var top = snap.Apps
            .OrderByDescending(a => a.BytesUpTotal + a.BytesDownTotal)
            .ThenBy(a => a.ImageName, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(TalkerRowViewModel.From)
            .ToList();

        Talkers.Clear();
        foreach (var row in top)
        {
            Talkers.Add(row);
        }
    }

    internal static string FormatRate(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || bytesPerSec <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        var value = bytesPerSec;
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        var formatted = value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return formatted + " " + units[unit];
    }
}

public sealed record TalkerRowViewModel(
    string AppLabel,
    string Publisher,
    string SignatureStatus,
    string UpRateText,
    string DownRateText)
{
    public static TalkerRowViewModel From(AppActivity app)
    {
        var label = string.IsNullOrEmpty(app.HostedServices)
            ? app.ImageName
            : $"{app.ImageName} [{app.HostedServices}]";
        var publisher = string.IsNullOrEmpty(app.Publisher) ? "(unknown)" : app.Publisher;
        return new TalkerRowViewModel(
            AppLabel: label,
            Publisher: publisher,
            SignatureStatus: app.SignatureStatus,
            UpRateText: DashboardPage.FormatRate(app.BytesUpPerSec),
            DownRateText: DashboardPage.FormatRate(app.BytesDownPerSec));
    }
}
