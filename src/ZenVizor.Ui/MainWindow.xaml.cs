using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ZenVizor.Ui.Services;
using ZenVizor.Ui.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ZenVizor.Ui;

public partial class MainWindow : FluentWindow
{
    private readonly ServiceStatusPoller _poller;
    private readonly ActivitySnapshotPoller _activityPoller;
    private bool _exiting;

    /// <summary>
    /// Fires on every <see cref="ActivitySnapshotPoller"/> tick (~2 s).
    /// Subscribed by <see cref="Views.DashboardPage"/> so it can drive its
    /// chart and talkers list off the MainWindow-scoped poller instance —
    /// the poller now lives here so the bottom-bar rate mirror keeps
    /// updating on every screen, not just Dashboard. Internal because
    /// <see cref="ActivitySnapshotUpdate"/> is internal; the only intended
    /// subscriber lives in the same assembly.
    /// </summary>
    internal event EventHandler<ActivitySnapshotUpdate>? ActivitySnapshotReceived;

    public MainWindow()
    {
        // SystemThemeWatcher.Watch MUST run before the window is Loaded.
        // The Watch() implementation has a bug where its initial
        // ApplySystemTheme call is guarded by `_observedWindows.Count == 0`,
        // but when called from a window that is already Loaded the window
        // is added to _observedWindows BEFORE that count check — so the
        // initial theme apply is skipped and the app stays on whatever the
        // placeholder ThemesDictionary in App.xaml specified (Light).
        // Subsequent OS theme flips still work via the WndProc hook, which
        // doesn't have the count guard. Calling here in the ctor takes the
        // "deferred until Loaded" branch which works correctly; this matches
        // the Wpf.Ui Gallery sample's call site.
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica);

        InitializeComponent();

        _poller = new ServiceStatusPoller();
        _poller.StatusChanged += OnStatusChanged;

        _activityPoller = new ActivitySnapshotPoller();
        _activityPoller.SnapshotReceived += OnActivitySnapshot;

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
        _poller.Start();
        _activityPoller.Start();

        // Gallery's canonical initial-selection pattern: Navigate(Type)
        // from the window's Loaded handler. With TargetPageType set per
        // item via OnNavItemInitialized BEFORE NavigationView's own
        // OnInitialized populated the type->item lookup, this call
        // resolves to the real NavDashboard and routes through
        // NavigateInternal, which updates SelectedItem and NavigationStack
        // so the next user click correctly deactivates Dashboard.
        RootNavigation.Navigate(typeof(DashboardPage));
    }

    // NavigationView builds its type->item lookup inside its OnInitialized,
    // which fires during InitializeComponent. The XAML Initialized event
    // fires per-item BEFORE the parent's EndInit — early enough to set
    // TargetPageType in time for that lookup, unlike the ctor body. Without
    // this, Navigate(Type) routes to an orphan item not in the visual tree
    // and the real menu item never visually selects on launch.
    private void OnNavItemInitialized(object sender, EventArgs e)
    {
        var item = (NavigationViewItem)sender;
        item.TargetPageType = item.Name switch
        {
            nameof(NavDashboard) => typeof(DashboardPage),
            nameof(NavPerApp)    => typeof(PerAppPage),
            nameof(NavHistory)   => typeof(HistoryPage),
            nameof(NavReports)   => typeof(ReportsPage),
            nameof(NavAlerts)    => typeof(AlertsPage),
            nameof(NavSettings)  => typeof(SettingsPage),
            _ => null,
        };
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
        SystemThemeWatcher.UnWatch(this);
        _poller.Dispose();
        _activityPoller.Dispose();
        // Tray.Dispose() intentionally NOT called here. H.NotifyIcon.Wpf
        // auto-hooks Application.Exit (TaskbarIcon.DisposeAfterExit) and
        // disposes the tray AFTER the dispatcher fully drains. Calling
        // Dispose here destroys the message-window HWND that the
        // ContextMenu uses for activation tracking — if the popup is
        // still mid-dismiss, it gets stranded on screen.
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

        // The lingering tray popup is caused by ContextMenu inheriting the
        // system menu fade animation via SetResourceReference on its inner
        // Popup (SystemParameters.MenuPopupAnimationKey). That fade delays
        // the Popup's _asyncDestroy DispatcherTimer, so the popup HWND
        // stays on screen until the animation elapses — visible for "a
        // few seconds" if Windows has slow menu animations enabled.
        //
        // Two fixes layered:
        //   1. Override PopupAnimation on the inner Popup so dismissal is
        //      HWND-immediate (local value beats SetResourceReference).
        //      The inner Popup is the LOGICAL parent of the ContextMenu.
        //   2. Wait for ContextMenu.Closed — which fires from the
        //      _asyncDestroy tick AFTER DestroyWindow — before calling
        //      Close on the main window, so the popup HWND is genuinely
        //      gone before tearing down.
        //
        // Fully qualified MenuItem — Wpf.Ui.Controls also has a MenuItem
        // type, but the tray ContextMenu uses the stock WPF one.
        if (sender is System.Windows.Controls.MenuItem mi
            && ContextMenuService.GetContextMenu(mi) is { } cm)
        {
            if (LogicalTreeHelper.GetParent(cm) is Popup popup)
            {
                popup.PopupAnimation = PopupAnimation.None;
            }
            cm.Closed += OnTrayMenuClosed;
            cm.IsOpen = false;
        }
        else
        {
            Close();
        }
    }

    private void OnTrayMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu cm)
        {
            cm.Closed -= OnTrayMenuClosed;
        }
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
                ServiceStatusDot.Fill = (Brush)FindResource("status.connected");
                ServiceStatusText.Text =
                    $"Service: connected ({update.ServiceVersion}, proto {update.ProtocolVersion})";
            }
            else
            {
                ServiceStatusDot.Fill = (Brush)FindResource("status.disconnected");
                ServiceStatusText.Text = $"Service: {update.Message}";
            }
        });
    }

    private void OnActivitySnapshot(object? sender, ActivitySnapshotUpdate update)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateBottomBarRates(update);
            ActivitySnapshotReceived?.Invoke(this, update);
        });
    }

    private void UpdateBottomBarRates(ActivitySnapshotUpdate update)
    {
        // Disconnected: em-dash placeholders, everything tinted text.tertiary
        // (arrows AND values) so the bar reads as "no signal" at a glance.
        if (!update.IsConnected || update.Envelope is null)
        {
            var dim = (Brush)FindResource("text.tertiary");
            BottomBarUpArrow.Foreground = dim;
            BottomBarDownArrow.Foreground = dim;
            BottomBarUpRate.Foreground = dim;
            BottomBarDownRate.Foreground = dim;
            BottomBarUpRate.Text = "—";
            BottomBarDownRate.Text = "—";
            return;
        }

        var snap = update.Envelope.Payload;
        double totalUp = 0, totalDown = 0;
        if (snap.WindowSeconds > 0 && snap.Apps.Count > 0)
        {
            totalUp = snap.Apps.Sum(a => a.BytesUpPerSec);
            totalDown = snap.Apps.Sum(a => a.BytesDownPerSec);
        }
        // Connected (warming or steady): arrows recover their brand colors,
        // values render in text.primary. RateFormatter returns "0 B/s" for
        // zero/NaN, which is the correct read during warming.
        BottomBarUpArrow.Foreground = (Brush)FindResource("chart.upSeries");
        BottomBarDownArrow.Foreground = (Brush)FindResource("chart.downSeries");
        var primary = (Brush)FindResource("text.primary");
        BottomBarUpRate.Foreground = primary;
        BottomBarDownRate.Foreground = primary;
        BottomBarUpRate.Text = RateFormatter.FormatRate(totalUp);
        BottomBarDownRate.Text = RateFormatter.FormatRate(totalDown);
    }
}
