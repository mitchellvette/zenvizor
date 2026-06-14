using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;
using ZenVizor.Ui.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ZenVizor.Ui;

public partial class MainWindow : FluentWindow
{
    private readonly ServiceStatusPoller _poller;
    private readonly ActivitySnapshotPoller _activityPoller;
    private readonly AlertsClient _alertsClient;
    private bool _exiting;

    // Alerts nav-rail badge — one-shot pulse storyboard built in code-behind
    // because the motion tokens are sys:TimeSpan / IEasingFunction resources,
    // and Storyboard.Duration is the WPF `Duration` struct that XAML
    // attribute parsing recognizes for string literals but NOT for typed
    // resource lookups (TimeSpan does not implicitly convert to Duration
    // during the resource-binding's set accessor). Built once in OnLoaded
    // when the target element is in the visual tree; Begin() restarts on
    // each PulseAlertsBadge call.
    private Storyboard? _alertsBadgePulseStoryboard;

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

        // The alerts client owns the persistent push-subscription connection.
        // Constructed up-front so AlertsPage (cached, NavigationCacheMode.Enabled)
        // can reach it on first nav; subscribed here so the nav-rail badge
        // receives AlertRaised pushes regardless of whether the user has
        // opened the Alerts page. OnAlertRaised marshals to dispatcher.
        _alertsClient = new AlertsClient();
        _alertsClient.AlertRaised += OnAlertRaised;

        // Cache page instances so picker state (window, grain, scroll position)
        // survives navigation away and back. Without this, each nav rail click
        // constructs a fresh page and resets every picker to its default.
        NavDashboard.NavigationCacheMode = NavigationCacheMode.Enabled;
        NavPerApp.NavigationCacheMode = NavigationCacheMode.Enabled;
        NavHistory.NavigationCacheMode = NavigationCacheMode.Enabled;
        NavReports.NavigationCacheMode = NavigationCacheMode.Enabled;
        NavAlerts.NavigationCacheMode = NavigationCacheMode.Enabled;
        NavSettings.NavigationCacheMode = NavigationCacheMode.Enabled;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _poller.Start();
        _activityPoller.Start();

        BuildAlertsBadgePulseStoryboard();

        // Establish the alerts-push subscription eagerly so the nav-rail
        // badge fires immediately on AlertRaised even if the user has not
        // opened the Alerts page. Connection failures here are non-fatal —
        // the service may be down; the next user-driven query will surface
        // a status banner. Fire-and-forget so OnLoaded doesn't block on
        // pipe handshake (a slow connect would stall window display).
        _ = Task.Run(async () =>
        {
            try
            {
                await _alertsClient.EnsureConnectedAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort — the AlertsPage / nav-badge surface will
                // re-attempt the connect on their own query path.
            }
        });

        // Gallery's canonical initial-selection pattern: Navigate(Type)
        // from the window's Loaded handler. With TargetPageType set per
        // item via OnNavItemInitialized BEFORE NavigationView's own
        // OnInitialized populated the type->item lookup, this call
        // resolves to the real NavDashboard and routes through
        // NavigateInternal, which updates SelectedItem and NavigationStack
        // so the next user click correctly deactivates Dashboard.
        RootNavigation.Navigate(typeof(DashboardPage));
    }

    /// <summary>
    /// The single shared <see cref="AlertsClient"/> for this UI process.
    /// AlertsPage's view-model subscribes to its <c>AlertRaised</c> event
    /// alongside the nav-rail badge handler here, so both surfaces see the
    /// same push payload at the same moment.
    /// </summary>
    internal AlertsClient AlertsClient => _alertsClient;

    private void OnAlertRaised(object? sender, AlertDto alert)
    {
        // Marshal to dispatcher — StreamJsonRpc invokes the callback on
        // a thread-pool thread; visual-tree mutations must run on the UI
        // thread. Matches the OnActivitySnapshot pattern above.
        Dispatcher.Invoke(() =>
        {
            // Phase 3: simple "+1 to count, take this severity as the highest
            // observed" cue. Phase 4 wires the real ViewModel that owns the
            // active-set count and the running highest-severity calculation;
            // for now this gives a visible signal that push works end-to-end.
            // The AlertsViewModel (when authored) will own the count + severity
            // state and call UpdateAlertsBadge itself.
            PulseAlertsBadge();
        });
    }

    /// <summary>
    /// Updates the Alerts nav-rail badge to reflect the current active
    /// alert count and highest severity. Hides the badge when count is 0.
    /// Called from the AlertsViewModel as it recomputes the active set
    /// (Phase 4 wiring) and from any place that wants to force-refresh
    /// the badge surface.
    /// </summary>
    /// <param name="activeCount">Number of currently-active (undismissed) alerts.</param>
    /// <param name="highestSeverity">The worst severity present in the active
    /// set; drives the badge tint. Null when count is 0 (badge hidden).</param>
    public void UpdateAlertsBadge(int activeCount, NotableSeverity? highestSeverity)
    {
        if (activeCount <= 0)
        {
            AlertsBadgeCount.Visibility = Visibility.Collapsed;
            return;
        }

        AlertsBadgeCount.Visibility = Visibility.Visible;
        AlertsBadgeCountText.Text = activeCount.ToString();
        AlertsBadgeCount.Background = (Brush)FindResource(
            SeverityToBackgroundKey(highestSeverity ?? NotableSeverity.Info));
    }

    /// <summary>
    /// Fires the one-shot pulse-ring animation on the Alerts nav-rail
    /// badge. Skipped silently when the badge is hidden (no active
    /// alerts) or when OS animation is disabled
    /// (<see cref="SystemParameters.ClientAreaAnimation"/>). Idempotent:
    /// calling while a previous pulse is still in flight restarts the
    /// animation, never overlaps it.
    /// </summary>
    public void PulseAlertsBadge()
    {
        if (_alertsBadgePulseStoryboard is null) return;
        if (AlertsBadgeCount.Visibility != Visibility.Visible) return;
        if (!SystemParameters.ClientAreaAnimation) return;

        // Stop a still-running pulse before starting a fresh one — Begin()
        // alone restarts in place, but Stop()+Begin() ensures the From
        // value reseeds cleanly for back-to-back arrivals.
        _alertsBadgePulseStoryboard.Stop(AlertsBadgePulse);
        _alertsBadgePulseStoryboard.Begin(AlertsBadgePulse, isControllable: true);
    }

    // Locked severity → status background-brush mapping per the Alerts
    // catalog §1.4. The badge tint uses the SOLID severity brushes
    // (status.critical, status.caution, status.neutral), not the
    // .background tints — the mockup paints fully-saturated pills with
    // white text. status.neutral is overridden in BrandAccent.{Light,Dark}
    // from Wpf.Ui's gray to the brand cool blue per the Phase 1 token work.
    private static string SeverityToBackgroundKey(NotableSeverity severity) => severity switch
    {
        NotableSeverity.Critical => "status.critical",
        NotableSeverity.Warning => "status.caution",
        NotableSeverity.Info => "status.neutral",
        _ => "status.neutral",
    };

    // Build the badge pulse storyboard once, after Loaded fires (so the
    // target element is in the visual tree and Storyboard.SetTarget
    // resolves). Three parallel animations: opacity 0.85 → 0, ScaleX
    // 1 → 2.6, ScaleY 1 → 2.6 — all sharing the same duration + easing.
    // Spec source: alerts mockup page 9 ("scale ≈ 1 → 2.6, opacity ≈
    // 0.85 → 0"). Honors prefers-reduced-motion via
    // SystemParameters.ClientAreaAnimation at PulseAlertsBadge call
    // time (skip rather than build a no-op).
    private void BuildAlertsBadgePulseStoryboard()
    {
        var duration = (TimeSpan)FindResource("motion.duration.arrival");
        var ease = (IEasingFunction)FindResource("motion.ease.glide");
        var wpfDuration = new Duration(duration);

        var sb = new Storyboard();

        var opacityAnim = new DoubleAnimation
        {
            From = 0.85,
            To = 0.0,
            Duration = wpfDuration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(opacityAnim, AlertsBadgePulse);
        Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(opacityAnim);

        var scaleXAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 2.6,
            Duration = wpfDuration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(scaleXAnim, AlertsBadgePulse);
        Storyboard.SetTargetProperty(scaleXAnim,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        sb.Children.Add(scaleXAnim);

        var scaleYAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 2.6,
            Duration = wpfDuration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(scaleYAnim, AlertsBadgePulse);
        Storyboard.SetTargetProperty(scaleYAnim,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        sb.Children.Add(scaleYAnim);

        _alertsBadgePulseStoryboard = sb;
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
            nameof(NavPerApp) => typeof(PerAppPage),
            nameof(NavHistory) => typeof(HistoryPage),
            nameof(NavReports) => typeof(ReportsPage),
            nameof(NavAlerts) => typeof(AlertsPage),
            nameof(NavSettings) => typeof(SettingsPage),
            _ => null,
        };
    }

    // Close-to-tray: cancel the close and hide the window. Only the explicit
    // tray Exit menu (which sets _exiting = true) gets through to real shutdown.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            // UnWatch must happen here, NOT in OnClosed: OnClosed runs after
            // WM_DESTROY, when the window handle is already IntPtr.Zero, and
            // UnWatch throws InvalidOperationException without a live HWND —
            // which historically skipped Application.Current.Shutdown() and
            // left the process (and tray popup) stranded.
            try { SystemThemeWatcher.UnWatch(this); } catch { }
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _poller.Dispose(); } catch { }
        try { _activityPoller.Dispose(); } catch { }
        // Fire-and-forget the alerts pipe disposal — the named pipe + RPC
        // session free themselves quickly; we don't await because OnClosed
        // is running on the dispatcher and a blocking wait would deadlock
        // any in-flight callback that still expects the dispatcher.
        try { _ = _alertsClient.DisposeAsync().AsTask(); } catch { }
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
