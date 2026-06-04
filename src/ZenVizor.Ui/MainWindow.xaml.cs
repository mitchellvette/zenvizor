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
    private bool _exiting;

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
        SystemThemeWatcher.UnWatch(this);
        _poller.Dispose();
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
