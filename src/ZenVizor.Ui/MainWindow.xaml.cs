using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using H.NotifyIcon.Core;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;
using ZenVizor.Ui.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using IpcAppTheme = ZenVizor.Ipc.Contracts.Dto.AppTheme;

namespace ZenVizor.Ui;

public partial class MainWindow : FluentWindow
{
    private readonly ServiceStatusPoller _poller;
    private readonly ActivitySnapshotPoller _activityPoller;
    private readonly AlertsClient _alertsClient;
    private readonly SettingsClient _settingsClient;
    private bool _exiting;

    // Phase 6.2: cached "show desktop toast on AlertRaised" preference.
    // Hit by OnAlertRaised on every push so we keep it local instead of
    // round-tripping IPC per alert. Hydrated on Loaded from the service
    // and refreshed by SettingsPage via SetToastEnabled when the user
    // toggles the switch. Default true matches the seeded setting row.
    private bool _toastEnabled = true;

    // Per-severity active counts tracked LOCALLY in MainWindow so the
    // nav-rail badge updates on AlertRaised push regardless of whether
    // AlertsPage is currently loaded. Authoritative when MainWindow is
    // the only mutator; resynced from AlertsPage's view-model via
    // UpdateAlertsBadge when the page provides a known total.
    //
    // Drift envelope (documented Phase 6.1a trade-off): dismissals via
    // zvctl while AlertsPage is unloaded won't decrement these. Bounded —
    // Phase 7+ refactors to lift the alerts VM to app-level scope or add
    // a count-summary push from the service, see sprint plan A2.
    private int _badgeCritical;
    private int _badgeWarning;
    private int _badgeInfo;

    // Tracked across StatusChanged ticks so we fire ServiceReconnected
    // only on the disconnected→connected transition, not on every steady
    // "still connected" tick.
    private bool _serviceWasConnected;

    /// <summary>
    /// Fires on the dispatcher AFTER MainWindow has force-reconnected
    /// the shared <see cref="AlertsClient"/> following a service restart.
    /// Pages with stale per-page query clients subscribe and re-issue a
    /// refresh to pick up any data raised in the gap. General-purpose by
    /// design (not Alerts-specific) — see sprint plan §"Pre-v1
    /// architectural follow-ups" A1/A2 for the planned Scope 2/3
    /// adoption across HistoryPage / ReportsPage / PerAppPage /
    /// AppDetailPage.
    /// </summary>
    public event EventHandler? ServiceReconnected;

    /// <summary>
    /// Fires on the dispatcher AFTER the Settings page's Reset history
    /// flow has successfully wiped the service-side DB. Data pages
    /// subscribe to re-issue their existing <c>RefreshAsync</c> so the
    /// user sees the empty state immediately rather than after the next
    /// page navigation. Shape-mirrors <see cref="ServiceReconnected"/>
    /// so future A1 follow-up consolidation can fold both into a single
    /// "data invalidated" signal without churn at the page level.
    /// </summary>
    public event EventHandler? HistoryWiped;

    /// <summary>
    /// Called by SettingsPage after a successful
    /// <c>WipeHistoryAsync</c>. Resets the nav-rail badge counters
    /// directly (no fan-out hop) so the badge clears even on subscribers
    /// that haven't loaded yet, then raises the <see cref="HistoryWiped"/>
    /// event so pages currently in the visual tree refresh their data.
    /// Must be called on the dispatcher thread — SettingsPage runs on it
    /// already.
    /// </summary>
    public void RaiseHistoryWiped()
    {
        _badgeCritical = 0;
        _badgeWarning = 0;
        _badgeInfo = 0;
        UpdateAlertsBadgeInternal(activeCount: 0, highestSeverity: null);

        HistoryWiped?.Invoke(this, EventArgs.Empty);
    }

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
        //
        // Phase 6.2 gating: skip Watch when the user has pinned an explicit
        // theme (Light or Dark). The cached preference was already applied
        // in App.OnStartup; Watch here would re-attach the OS-listener and
        // stomp the user's choice on the next OS theme flip. Mica backdrop
        // is still applied either way via the FluentWindow chrome — Watch's
        // backdrop wiring isn't the only path that sets it.
        if (ThemePreferenceStore.Load() == IpcAppTheme.System)
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica);
        }

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

        // Owned by MainWindow because both the toast-on-alert preference
        // (read in OnAlertRaised) and SettingsPage's apply path share it.
        // Disposed alongside _alertsClient on window close.
        _settingsClient = new SettingsClient();

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

        // Phase 6.3 — silent-launch path. SourceInitialized fires after
        // the HWND is created but BEFORE the first frame paints, so
        // Hide() here prevents the window from ever appearing on screen
        // (no flash). Loaded still fires afterward, so pollers /
        // AlertsClient subscription / nav routing all run normally — the
        // user just doesn't see a window until they click the tray icon.
        //
        // Reading StartMinimizedStore here (not in App.OnStartup) because
        // WPF's StartupUri processing constructs MainWindow AFTER
        // App.OnStartup returns; at that earlier point Application.MainWindow
        // is null and any Hide() call no-ops.
        if (StartMinimizedStore.Load())
        {
            SourceInitialized += OnSourceInitializedHideForSilentLaunch;
        }
    }

    private void OnSourceInitializedHideForSilentLaunch(object? sender, EventArgs e)
    {
        // One-shot — never re-fires for the lifetime of the window. The
        // user explicitly summons via tray, which ShowAndActivate handles.
        SourceInitialized -= OnSourceInitializedHideForSilentLaunch;
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
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
                // P2: seed nav-rail badge from the active-alerts set so
                // launch with pre-existing alerts shows the badge
                // immediately, not only after the user opens AlertsPage.
                await SeedBadgeFromActiveAlertsAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort — the AlertsPage / nav-badge surface will
                // re-attempt the connect on their own query path.
            }
        });

        // Hydrate the toast preference from the service so the very first
        // AlertRaised after launch honours the saved choice. Failure
        // leaves the field at its default (true); SettingsPage will
        // reconcile on its first GetSettingsAsync.
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await _settingsClient.GetSettingsAsync().ConfigureAwait(false);
                Dispatcher.Invoke(() => _toastEnabled = snapshot.ToastOnAlert);
            }
            catch
            {
                // Best-effort hydrate; default of true is the safe fallback.
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

    /// <summary>
    /// Shared <c>ContentPresenter</c> that hosts any in-app Wpf.Ui
    /// <c>ContentDialog</c>. Pages assign their dialog's
    /// <c>DialogHost</c> property to this before calling
    /// <c>ShowAsync</c>. Phase 6.2 SettingsPage's Reset history confirm
    /// is the first user.
    /// </summary>
    public System.Windows.Controls.ContentPresenter DialogHost => RootContentDialog;

    /// <summary>
    /// SettingsPage hands the updated preference here after a successful
    /// UpdateSettings round-trip so subsequent <c>OnAlertRaised</c>
    /// invocations honour the new value without re-fetching from IPC.
    /// </summary>
    internal void SetToastEnabled(bool enabled) => _toastEnabled = enabled;

    /// <summary>
    /// Fires a one-off test desktop notification through the same
    /// <c>Tray.ShowNotification</c> path real alerts use. Driven by the
    /// SettingsPage "Send test notification" button so users can verify the
    /// OS-level toast plumbing without waiting for a real alert to land.
    /// Swallows exceptions silently — a broken toast must not crash the UI.
    /// </summary>
    internal void ShowTestToast()
    {
        try
        {
            Tray.ShowNotification(
                title: "ZenVizor: Test notification",
                message: "If you can see this, desktop notifications are working.",
                icon: NotificationIcon.Info,
                sound: true);
        }
        catch
        {
            // Best-effort — same posture as OnAlertRaised's toast path.
        }
    }

    /// <summary>
    /// Settings client shared with the SettingsPage so the page doesn't
    /// open a second pipe per page-load. The page's view-model still
    /// owns its own debounce / VM state — this is just the IPC surface.
    /// </summary>
    internal SettingsClient SettingsClient => _settingsClient;

    /// <summary>
    /// P2 (sprint plan, Pre-MVP polish): seed the nav-rail badge from
    /// the authoritative server-side active-alerts set. Without this,
    /// only <see cref="OnAlertRaised"/> mutates the badge — so alerts
    /// already in the DB at launch stay invisible until the user opens
    /// the Alerts page (which calls <see cref="UpdateAlertsBadge"/>
    /// authoritatively).
    /// <para>
    /// Re-runs on the <c>ServiceReconnected</c> transition per the Q1
    /// decision: a restart can change the active-alerts set out from
    /// under us (retention purge, <c>zvctl alerts dismiss</c> while the
    /// UI was running), and the local per-severity counters would
    /// otherwise drift from the service truth across the gap. This
    /// snaps them back.
    /// </para>
    /// <para>
    /// Best-effort. Exceptions are swallowed — the next <c>AlertRaised</c>
    /// push or page-driven <see cref="UpdateAlertsBadge"/> call brings
    /// the badge back in sync.
    /// </para>
    /// </summary>
    private async Task SeedBadgeFromActiveAlertsAsync()
    {
        AlertsResult result;
        try
        {
            result = await _alertsClient
                .GetAlertsAsync(new AlertsFilter(AlertState.Active))
                .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            // Reset before counting — re-seed on ServiceReconnected
            // must not double-count alerts that survived through the
            // local OnAlertRaised stream. The server set is authoritative.
            _badgeCritical = 0;
            _badgeWarning = 0;
            _badgeInfo = 0;
            foreach (var alert in result.Alerts)
            {
                switch (alert.Severity)
                {
                    case NotableSeverity.Critical: _badgeCritical++; break;
                    case NotableSeverity.Warning:  _badgeWarning++;  break;
                    case NotableSeverity.Info:     _badgeInfo++;     break;
                }
            }
            RenderBadgeFromLocalCounts();
        });
    }

    private void OnAlertRaised(object? sender, AlertDto alert)
    {
        // Marshal to dispatcher — StreamJsonRpc invokes the callback on
        // a thread-pool thread; visual-tree mutations must run on the UI
        // thread. Matches the OnActivitySnapshot pattern above.
        Dispatcher.Invoke(() =>
        {
            // Phase 6.1a: MainWindow now owns the badge-state update on
            // push so users see the nav-rail badge appear regardless of
            // which page they're on. Previously this only pulsed an
            // already-visible badge (no-op when count was zero) and
            // delegated the actual count to the page VM via
            // UpdateAlertsBadge — which only fires when AlertsPage is
            // loaded.
            switch (alert.Severity)
            {
                case NotableSeverity.Critical: _badgeCritical++; break;
                case NotableSeverity.Warning:  _badgeWarning++;  break;
                case NotableSeverity.Info:     _badgeInfo++;     break;
            }
            RenderBadgeFromLocalCounts();
            PulseAlertsBadge();

            // Phase 6.2: optional desktop toast. Off by default-not, on
            // by default-yes (matches the seeded toast.on_alert = '1'
            // setting). Severity drives the system-icon glyph; the
            // tray-balloon-click handler brings the window back and
            // navigates to Alerts. We swallow exceptions because a
            // broken toast must not corrupt the badge update above.
            if (_toastEnabled)
            {
                try
                {
                    Tray.ShowNotification(
                        title: $"ZenVizor: {AlertCatalogLookups.SeverityDisplayName(alert.Severity)}",
                        message: alert.Title,
                        icon: SeverityToToastIcon(alert.Severity),
                        sound: true);
                }
                catch
                {
                    // Swallow — toast is a notification, not a feature.
                }
            }
        });
    }

    /// <summary>
    /// Tray balloon click handler — wired in MainWindow.xaml. Restores the
    /// window if hidden and navigates to the Alerts page. The Alerts page
    /// is NavigationCacheMode.Enabled so this lands on its existing
    /// instance.
    /// </summary>
    private void OnTrayBalloonTipClicked(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        try { RootNavigation.Navigate(typeof(AlertsPage)); }
        catch { /* navigation is best-effort — already on Alerts is fine */ }
    }

    private static NotificationIcon SeverityToToastIcon(NotableSeverity severity) =>
        severity switch
        {
            NotableSeverity.Critical => NotificationIcon.Error,
            NotableSeverity.Warning  => NotificationIcon.Warning,
            _                        => NotificationIcon.Info,
        };

    /// <summary>
    /// Compute the badge count + highest-severity tint from the
    /// per-severity local counters and render via the existing
    /// <see cref="UpdateAlertsBadge"/> path. Pulls the highest non-zero
    /// severity in the locked Critical &gt; Warning &gt; Info ordering.
    /// </summary>
    private void RenderBadgeFromLocalCounts()
    {
        var total = _badgeCritical + _badgeWarning + _badgeInfo;
        NotableSeverity? highest =
            _badgeCritical > 0 ? NotableSeverity.Critical
            : _badgeWarning > 0 ? NotableSeverity.Warning
            : _badgeInfo    > 0 ? NotableSeverity.Info
            : null;
        UpdateAlertsBadgeInternal(total, highest);
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
        // Page-authoritative update from AlertsViewModel — collapse the
        // local per-severity breakdown to match. We don't have full
        // per-severity detail from the page's caller, so attribute the
        // total to the highest-severity tier; subsequent push events
        // will increment from this baseline and RenderBadgeFromLocalCounts
        // will surface a higher tier if a more-severe push lands. The
        // visible badge (total + tint) matches the page view; the
        // per-severity collapse is internal bookkeeping that converges
        // back to authoritative on the next page-driven update.
        _badgeCritical = highestSeverity == NotableSeverity.Critical ? activeCount : 0;
        _badgeWarning  = highestSeverity == NotableSeverity.Warning  ? activeCount : 0;
        _badgeInfo     = highestSeverity == NotableSeverity.Info     ? activeCount : 0;
        UpdateAlertsBadgeInternal(activeCount, highestSeverity);
    }

    private void UpdateAlertsBadgeInternal(int activeCount, NotableSeverity? highestSeverity)
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
    /// alerts) or when motion is disabled (OS animation flag OR Windows
    /// High Contrast — see <see cref="MotionPolicy"/>). Idempotent:
    /// calling while a previous pulse is still in flight restarts the
    /// animation, never overlaps it.
    /// </summary>
    public void PulseAlertsBadge()
    {
        if (_alertsBadgePulseStoryboard is null) return;
        if (AlertsBadgeCount.Visibility != Visibility.Visible) return;
        if (!MotionPolicy.AnimationsEnabled) return;

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

        // First-close balloon — let the user know the app is still
        // running rather than fully exited. Idempotent via a local
        // marker file so we never re-prompt on subsequent closes.
        if (!FirstCloseShownStore.HasBeenShown())
        {
            try
            {
                Tray.ShowNotification(
                    title: "ZenVizor is still running",
                    message: "Right-click the tray icon to show the window or exit.",
                    icon: NotificationIcon.Info,
                    sound: false);
            }
            catch
            {
                // Notification is courtesy, not load-bearing. Swallowing
                // failures keeps close-to-tray reliable even when the OS
                // refuses the toast (focus-assist, group policy, etc.).
            }
            FirstCloseShownStore.MarkShown();
        }
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

    /// <summary>
    /// Rewrites the first context-menu item's header just before the popup
    /// shows so it reads "Show ZenVizor" when the window is hidden and
    /// "Hide to tray" when it's visible. WPF binds the header at template
    /// build time; without this re-read the menu would lag the actual
    /// window state across hide/show cycles.
    /// </summary>
    private void OnTrayContextMenuOpened(object sender, RoutedEventArgs e)
    {
        TrayShowHideItem.Header = IsVisible && WindowState != WindowState.Minimized
            ? "Hide to tray"
            : "Show ZenVizor";
    }

    private void OnTrayShowHideClicked(object sender, RoutedEventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            // Mirrors the X-button path: cancel-and-hide is owned by
            // OnClosing; calling Hide() directly is the right verb when
            // the user explicitly asks "hide to tray" from the menu.
            Hide();
        }
        else
        {
            ShowAndActivate();
        }
    }

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
        // Re-show in the taskbar — App.OnStartup may have hidden the
        // taskbar entry under the silent-launch path (Phase 6.3
        // start_minimized). Once the user has summoned the window we want
        // it visible everywhere the OS shows running apps.
        ShowInTaskbar = true;
        Activate();
    }

    private void OnStatusChanged(object? sender, ServiceStatusUpdate update)
    {
        Dispatcher.Invoke(() =>
        {
            if (update.IsConnected)
            {
                ServiceStatusDot.Fill = (Brush)FindResource("status.connected");
                // Display-clean version: strip the +commit-hash suffix that
                // AssemblyInformationalVersion stamps under Deterministic
                // builds. Diagnostic version still available via zvctl.
                var clean = VersionFormatter.Display(update.ServiceVersion);
                ServiceStatusText.Text =
                    $"Service: connected · v{clean} · proto {update.ProtocolVersion}";
            }
            else
            {
                ServiceStatusDot.Fill = (Brush)FindResource("status.disconnected");
                ServiceStatusText.Text = $"Service: {update.Message}";
            }

            // Phase 6.1a: detect the disconnected→connected transition and
            // trigger an AlertsClient force-reconnect so the push subscription
            // survives a service restart. Fire ServiceReconnected after the
            // reconnect succeeds so subscribing pages (currently just
            // AlertsPage; sprint plan A1 extends to other pages) can run a
            // fresh RefreshAsync to pick up anything raised in the gap
            // between service start and this reconnect. Fire-and-forget so
            // the status visual update isn't blocked on pipe handshake; the
            // event fires on the dispatcher after the reconnect completes.
            if (update.IsConnected && !_serviceWasConnected)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _alertsClient.ForceReconnectAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort. If the reconnect fails the next IPC call
                        // from any page will re-attempt via the lazy path.
                        return;
                    }
                    // P2 (Q1): re-seed the badge before firing
                    // ServiceReconnected. The service-side active set
                    // can change across a restart (retention purge,
                    // zvctl dismissals while the UI was running) and
                    // the local per-severity counters would otherwise
                    // drift from server truth.
                    await SeedBadgeFromActiveAlertsAsync().ConfigureAwait(false);
                    Dispatcher.Invoke(() => ServiceReconnected?.Invoke(this, EventArgs.Empty));
                });
            }
            _serviceWasConnected = update.IsConnected;
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
