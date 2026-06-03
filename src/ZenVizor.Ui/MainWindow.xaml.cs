using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ZenVizor.Ui.Services;
using ZenVizor.Ui.Views;
using Wpf.Ui.Controls;

namespace ZenVizor.Ui;

public partial class MainWindow : FluentWindow
{
    private readonly ServiceStatusPoller _poller;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        _poller = new ServiceStatusPoller();
        _poller.StatusChanged += OnStatusChanged;

        // Wire navigation targets in code so XAML doesn't need to resolve
        // same-assembly view types via x:Type (BAML pass-1 can't see them).
        NavDashboard.TargetPageType = typeof(DashboardPage);
        NavPerApp.TargetPageType    = typeof(PerAppPage);
        NavHistory.TargetPageType   = typeof(HistoryPage);
        NavReports.TargetPageType   = typeof(ReportsPage);
        NavAlerts.TargetPageType    = typeof(AlertsPage);
        NavSettings.TargetPageType  = typeof(SettingsPage);

        // Cache page instances so picker state (window, grain, scroll position)
        // survives navigation away and back. Without this, each nav rail click
        // constructs a fresh page and resets every picker to its default.
        NavDashboard.NavigationCacheMode = NavigationCacheMode.Enabled;
        NavPerApp.NavigationCacheMode    = NavigationCacheMode.Enabled;
        NavHistory.NavigationCacheMode   = NavigationCacheMode.Enabled;
        NavReports.NavigationCacheMode   = NavigationCacheMode.Enabled;
        NavAlerts.NavigationCacheMode    = NavigationCacheMode.Enabled;
        NavSettings.NavigationCacheMode  = NavigationCacheMode.Enabled;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(DashboardPage));
        _poller.Start();
    }

    // Close-to-tray: cancel the close and hide the window. Only the explicit
    // tray Exit menu (which sets _exiting = true) gets through to real shutdown.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _poller.Dispose();
        Tray.Dispose();
        Application.Current.Shutdown();
    }

    private void OnTitleBarCloseClicked(TitleBar sender, RoutedEventArgs args)
    {
        // Wpf.Ui raises this and then issues its own Close(); the Closing
        // handler is what actually intercepts and hides. Kept wired so that
        // any future telemetry/log of the close intent has a hook.
    }

    private void OnTrayLeftClick(object sender, RoutedEventArgs e) => ShowAndActivate();

    private void OnTrayShowClicked(object sender, RoutedEventArgs e) => ShowAndActivate();

    private void OnTrayExitClicked(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Close();
    }

    private void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void OnStatusChanged(object? sender, ServiceStatusUpdate update)
    {
        Dispatcher.Invoke(() =>
        {
            if (update.IsConnected)
            {
                ServiceStatusDot.Fill = Brushes.MediumSeaGreen;
                ServiceStatusText.Text =
                    $"Service: connected ({update.ServiceVersion}, proto {update.ProtocolVersion})";
            }
            else
            {
                ServiceStatusDot.Fill = Brushes.DarkOrange;
                ServiceStatusText.Text = $"Service: {update.Message}";
            }
        });
    }
}
