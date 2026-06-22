using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;
using IpcAppTheme = ZenVizor.Ipc.Contracts.Dto.AppTheme;

namespace ZenVizor.Ui.Views;

/// <summary>
/// Phase 6.2 Settings page. Hybrid apply policy:
///   * Booleans / enums (autostart, toast, theme) — apply on change
///   * Retention NumberBoxes — 500ms debounce after last edit
///   * Reset history — explicit ContentDialog confirmation
///
/// Service-reconnect: subscribes to MainWindow.ServiceReconnected so a
/// service restart triggers a fresh GetSettingsAsync. Disconnected /
/// error states render via the StatusBanner + VM.Content state matrix
/// (Alerts page pattern).
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = new();

    // Owned by MainWindow so the toast-on-alert preference cache + this
    // page's IPC share one pipe. Resolved lazily on first use because
    // MainWindow may not be ready in the page ctor (designer hosts page
    // without a real Application.Current.MainWindow).
    private SettingsClient? _settingsRef;
    private SettingsClient Settings =>
        _settingsRef ??= (Application.Current.MainWindow as MainWindow)?.SettingsClient
            ?? new SettingsClient();

    // Debounce timer for retention NumberBox edits. 500ms after the last
    // value change runs an apply; spin-button storms and arrow-key holds
    // collapse to a single round-trip per "settle" event.
    private readonly DispatcherTimer _retentionDebounce;

    // Phase 6.7 — separate debounce for the three alert-threshold rows.
    // Same 500ms shape; separate timer so a retention edit doesn't
    // bundle alert fields into the same RPC and vice-versa (two
    // semantically-distinct apply paths in the IPC handler).
    private readonly DispatcherTimer _alertThresholdDebounce;

    // Suppresses re-entrant change handlers while we're populating the
    // form from a fresh snapshot. Without this, Hydrate would trip every
    // checked / unchecked / SelectionChanged handler and fire spurious
    // UpdateSettingsAsync calls back to the service.
    //
    // Initialised to TRUE so that change events deferred by WPF past the
    // constructor (notably ComboBox.SelectionChanged firing after the
    // initial SelectedIndex="0" from XAML lands) don't run their
    // handlers' local side effects — OnThemeChanged in particular calls
    // ApplyThemeImmediate + ThemePreferenceStore.Save BEFORE its
    // ApplyAsync (and thus before the _hasHydrated belt). The flag is
    // cleared at the end of the first successful RefreshAsync.
    private bool _suppressApply = true;

    // Set after the first successful GetSettingsAsync round-trip. Until
    // this flips true, ApplyAsync short-circuits — a defensive belt for
    // any control whose ValueChanged / SelectionChanged event fires
    // outside the _suppressApply synchronous window (e.g. WPF deferring an
    // event past the binding's source update). Prevents the "Couldn't
    // save change" banner on first load when nothing was edited.
    private bool _hasHydrated;

    // Row VMs for the Retention ItemsControl. One per tier; each carries
    // its label, description, max-for-unit, and the underlying
    // SettingsViewModel.RetentionField. Stored on the page so the
    // ItemsControl re-bind on Hydrate doesn't lose references.
    private readonly List<RetentionRowVm> _retentionRows;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = _vm;

        // Row copy intentionally avoids "ZenVizor keeps" — the section
        // header already establishes "stays on your machine; nothing is
        // sent." Each row's prose stays in that frame ("stays on your
        // machine") so the privacy posture stays loud across every row,
        // not just the header.
        _retentionRows = new List<RetentionRowVm>
        {
            new("Recent traffic samples",
                "How long each high-resolution traffic sample stays on your machine. Powers the Dashboard live view and the per-app drill-down at sample-grain.",
                _vm.Samples),
            new("Connection records",
                "How long per-endpoint connection rows stay on your machine. Shown on the per-app drill-down.",
                _vm.Connections),
            new("Hourly rollups",
                "How long the hourly summarized traffic stays on your machine. Used by the History page for the past week or two.",
                _vm.HourlyRollups),
            new("Daily rollups",
                "How long the daily summarized traffic stays on your machine. Powers long-range reports and trend lines.",
                _vm.DailyRollups),
            new("Dismissed alerts",
                "After you dismiss an alert, how long the record stays on your machine before being removed.",
                _vm.AlertsAfterDismiss),
        };
        RetentionRows.ItemsSource = _retentionRows;

        _retentionDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _retentionDebounce.Tick += OnRetentionDebounceTick;

        _alertThresholdDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _alertThresholdDebounce.Tick += OnAlertThresholdDebounceTick;

        PopulateAbout();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged += OnVmContentChanged;
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.ServiceReconnected += OnServiceReconnected;
        }

        // Phase 6.5 — HC notice on the Theme card. Show the notice when
        // Windows HC is active and subscribe to flips while the page is
        // mounted so the notice appears/disappears in real time.
        RefreshThemeCardHighContrastNotice();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        await RefreshAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnVmContentChanged;
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.ServiceReconnected -= OnServiceReconnected;
        }
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _retentionDebounce.Stop();
        _alertThresholdDebounce.Stop();
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        // StaticPropertyChanged fires on a worker thread; marshal to the UI.
        _ = Dispatcher.BeginInvoke(new Action(RefreshThemeCardHighContrastNotice));
    }

    private void RefreshThemeCardHighContrastNotice()
    {
        HighContrastNotice.Visibility = SystemParameters.HighContrast
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnServiceReconnected(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _vm.Content = SettingsViewModel.PageContent.Loading;

        // _suppressApply stays TRUE for the whole Refresh — including
        // across the GetSettingsAsync await. Splitting it into two
        // suppress windows (one for pre-hydrate, another for hydrate)
        // left a gap where WPF-deferred SelectionChanged events for
        // ThemePicker could fire unguarded — OnThemeChanged would then
        // run ApplyThemeImmediate + ThemePreferenceStore.Save against a
        // stale SelectedIndex (e.g. the XAML "Follow system" default),
        // visibly flipping a Light user to System on the first nav to
        // this page and silently overwriting the local theme cache.
        // Keeping the gate closed across the await holds the line.
        _suppressApply = true;
        try
        {
            // Pre-hydrate the theme picker from the local cache BEFORE
            // awaiting IPC so it never renders blank if the service is
            // slow or down. ThemePreferenceStore.Load returns System on
            // any error, so this is always safe.
            ThemePicker.SelectedIndex = (int)ThemePreferenceStore.Load();

            try
            {
                var snapshot = await Settings.GetSettingsAsync();
                _vm.Hydrate(snapshot);
                // Sync the non-bindable controls (ComboBox SelectedIndex
                // doesn't survive a XAML-binding path because we want
                // SelectedIndex semantics, not SelectedItem). Capture-tier
                // descriptions are VM-bound (FlushIntervalDescription /
                // BucketSizeDescription) so they refresh automatically.
                ThemePicker.SelectedIndex = (int)snapshot.Theme;
                // Mirror the authoritative server value back to the local
                // cache so an out-of-band edit (zvctl, direct SQL) flows
                // into the next App.OnStartup synchronous read.
                StartMinimizedStore.Save(snapshot.StartMinimized);
                foreach (var row in _retentionRows)
                {
                    row.RefreshAfterHydrate();
                }
                _hasHydrated = true;
                _vm.Content = SettingsViewModel.PageContent.Populated;
                HideBanner();
            }
            catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
            {
                _vm.Content = SettingsViewModel.PageContent.Disconnected;
                ShowBanner(critical: false,
                    "Service disconnected. Settings can be viewed but not changed.",
                    glyph: SymbolRegular.PlugDisconnected20);
            }
            catch (Exception ex) when (SettingsClient.IsMethodNotFound(ex))
            {
                // Service binary predates Phase 6.2 — the settings IPC isn't
                // exposed yet. Surface as a calm informational banner;
                // defaults remain visible (theme came from the local cache)
                // and the user knows how to fix.
                _vm.Content = SettingsViewModel.PageContent.Error;
                ShowBanner(critical: false,
                    "Settings can't be loaded. The ZenVizor service is older than this app; " +
                    "restart the service (Services.msc, ZenVizor, Restart) to enable changes.");
            }
            catch (Exception ex)
            {
                _vm.Content = SettingsViewModel.PageContent.Error;
                ShowBanner(critical: false, $"Couldn't load settings ({ex.GetType().Name}).");
            }
        }
        finally
        {
            _suppressApply = false;
        }
    }

    // ── Apply-on-change handlers ────────────────────────────────────────

    private async void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressApply) return;
        var mode = _vm.AutostartEnabled
            ? ServiceStartMode.Automatic
            : ServiceStartMode.Manual;
        await ApplyAsync(new SettingsUpdate { AutostartMode = mode });
    }

    private async void OnStartMinimizedChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressApply) return;
        // Update the local cache eagerly so a crash before the IPC
        // round-trip completes still gives the next launch a correct
        // value. Same pattern as theme.
        StartMinimizedStore.Save(_vm.StartMinimized);
        await ApplyAsync(new SettingsUpdate { StartMinimized = _vm.StartMinimized });
    }

    private async void OnToastChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressApply) return;
        await ApplyAsync(new SettingsUpdate { ToastOnAlert = _vm.ToastOnAlert });
        // Mirror the new value into MainWindow's cached field so the
        // very next AlertRaised push honours the toggle without
        // waiting for an IPC round-trip.
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.SetToastEnabled(_vm.ToastOnAlert);
        }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressApply) return;
        if (ThemePicker.SelectedIndex < 0) return;

        var picked = (IpcAppTheme)ThemePicker.SelectedIndex;
        _vm.Theme = picked;

        // Apply the theme locally BEFORE the IPC call so the user sees
        // the change immediately. Cache it to disk too so the next launch
        // boots into the same theme without waiting for the service.
        ApplyThemeImmediate(picked);
        ThemePreferenceStore.Save(picked);

        // Persist server-side. If the call fails the local override stays
        // (cache and ApplicationThemeManager both already moved); a
        // subsequent successful save reconciles. This is the right
        // trade-off for theme — bouncing back to old theme on transient
        // pipe error feels worse than the brief divergence.
        await ApplyAsync(new SettingsUpdate { Theme = picked });
    }

    private async void OnSmoothChartAnimationsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressApply) return;
        // No local cache mirror: unlike theme + start-minimized, this
        // setting isn't read on boot — DashboardPage re-reads it from
        // the service on every page load, so a persisted value lands in
        // effect the next time the user navigates to Dashboard.
        await ApplyAsync(new SettingsUpdate { SmoothChartAnimations = _vm.SmoothChartAnimations });
    }

    // ── Retention composite — debounced apply ───────────────────────────

    // NumberBoxValueChangedEvent delegate signature is
    // (object sender, NumberBoxValueChangedEventArgs args) — confirmed by
    // reflection against Wpf.Ui 4.0.2. XAML handler matching is strict on
    // the exact delegate Invoke signature so we mirror it here.
    private void OnRetentionValueChanged(object sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressApply) return;
        // Restart the debounce window. Multiple changes within 500ms
        // collapse to one apply at the end.
        _retentionDebounce.Stop();
        _retentionDebounce.Start();
    }

    private void OnRetentionUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressApply) return;
        if (sender is not ComboBox cb || cb.Tag is not RetentionRowVm row) return;

        // The ComboBox's bound UnitIndex setter triggers RetentionField
        // unit setter, which recomputes UnitScopedValue. The Hydrate-path
        // suppression keeps Unit setting from firing apply during load.
        row.RefreshAfterUnitChange();
        _retentionDebounce.Stop();
        _retentionDebounce.Start();
    }

    // Phase 6.7 — same debounce shape as retention. NumberBox edits feed
    // a single SettingsUpdate with the three current threshold values.
    // The settings cache on the service side refreshes atomically when
    // the apply lands so per-flush rules pick up the new thresholds on
    // the next flush.
    private void OnAlertThresholdValueChanged(object sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressApply) return;
        _alertThresholdDebounce.Stop();
        _alertThresholdDebounce.Start();
    }

    private async void OnAlertThresholdDebounceTick(object? sender, EventArgs e)
    {
        _alertThresholdDebounce.Stop();
        var update = new SettingsUpdate
        {
            AlertLargeDownloadMb            = _vm.AlertLargeDownloadMb,
            AlertOutboundHeavyFloorMb       = _vm.AlertOutboundHeavyFloorMb,
            // VM exposes k as a decimal; wire format is integer × 10.
            // Round to nearest 0.1 to avoid floating-point drift creating
            // off-by-one wire values from arrow-key edits.
            AlertUnusualDailyVolumeKTimesTen = (int)Math.Round(_vm.AlertUnusualDailyVolumeK * 10.0),
        };
        await ApplyAsync(update);
    }

    private async void OnRetentionDebounceTick(object? sender, EventArgs e)
    {
        _retentionDebounce.Stop();
        // Send every retention day-count in one round-trip. The IPC
        // contract is partial-update, but lumping all five is cheaper
        // (one RPC, atomic at the handler) than dispatching one per
        // field and easier to reason about under back-to-back edits.
        var update = new SettingsUpdate
        {
            RetentionSamplesDays         = _vm.Samples.Days,
            RetentionConnectionsDays     = _vm.Connections.Days,
            RetentionHourlyDays          = _vm.HourlyRollups.Days,
            RetentionDailyDays           = _vm.DailyRollups.Days,
            RetentionAlertsDaysAfterAck  = _vm.AlertsAfterDismiss.Days,
        };
        await ApplyAsync(update);
    }

    // ── Reset history — explicit confirm ────────────────────────────────

    private async void OnResetHistoryClick(object sender, RoutedEventArgs e)
    {
        // Wpf.Ui's ContentDialog inherits its Background through the
        // visual tree from the hosting window's chrome. Our app overrides
        // ApplicationBackgroundBrush to Transparent for Mica showthrough
        // (App.xaml.cs ApplyDirectLevelOverrides), so without these
        // explicit values the dialog reads as an unstyled translucent
        // sheet floating over Mica. Pin the card surface + border + radius
        // recipe so it sits on top of the chrome as a proper card. Same
        // tokens our section cards use, so the visual language stays
        // consistent.
        var dialog = new ContentDialog
        {
            Title = "Reset history?",
            Content = "This permanently deletes every traffic, connection, " +
                      "session, and alert row. Your settings are preserved.\n\n" +
                      "ZenVizor will start collecting fresh data on the next " +
                      "capture tick. This cannot be undone.",
            PrimaryButtonText = "Reset history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            Background = (System.Windows.Media.Brush)FindResource("metal.card"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("border.card"),
            BorderThickness = new Thickness(1),
            Foreground = (System.Windows.Media.Brush)FindResource("text.primary"),
        };

        // Wpf.Ui's PrimaryButton (Appearance="Primary") binds its foreground
        // to TextOnAccentFillColorPrimaryBrush. The dialog.Foreground above
        // sets text.primary at the ContentControl scope which the button
        // template inherits via WPF's normal property inheritance, hiding
        // the framework's white-on-accent default and producing the
        // dark-text-on-violet contrast failure in light theme. Scope an
        // override into dialog.Resources so the brush flows down to the
        // button without affecting the dialog's body text. text.on-accent
        // is white in both themes — safe to use for either.
        dialog.Resources["TextOnAccentFillColorPrimaryBrush"] =
            (System.Windows.Media.Brush)FindResource("text.on-accent");

        // Host the dialog in MainWindow's content presenter — required by
        // Wpf.Ui's ContentDialog (it renders inside the DialogHost
        // ContentPresenter via z-order overlay).
        var host = Window.GetWindow(this) as MainWindow;
        if (host?.DialogHost is null)
        {
            // Fallback path — shouldn't happen in production but keeps the
            // page testable in a host without DialogHost wired.
            return;
        }
        dialog.DialogHost = host.DialogHost;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            var wipe = await Settings.WipeHistoryAsync();
            var total = wipe.TotalDeleted;
            _vm.ResetHistoryStatus = total == 0
                ? "Nothing to reset. History was already empty."
                : $"Reset complete. Removed {total:N0} rows.";
            ResetHistoryStatusText.Text = _vm.ResetHistoryStatus;
            ResetHistoryStatusText.Visibility = Visibility.Visible;

            // Fan out so every data page that's loaded refreshes its
            // cached result against the now-empty DB, and the nav-rail
            // badge counters reset. Pages that aren't currently loaded
            // pick up fresh data on their next OnPageLoaded refresh.
            host.RaiseHistoryWiped();
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            _vm.Content = SettingsViewModel.PageContent.Disconnected;
            ShowBanner(critical: false,
                "Service disconnected. Couldn't reset history.",
                glyph: SymbolRegular.PlugDisconnected20);
        }
        catch (Exception ex) when (SettingsClient.IsMethodNotFound(ex))
        {
            _vm.Content = SettingsViewModel.PageContent.Error;
            ShowBanner(critical: false,
                "Couldn't reset history. The ZenVizor service is older than this app; " +
                "restart the service to enable changes.");
        }
        catch (Exception ex)
        {
            _vm.Content = SettingsViewModel.PageContent.Error;
            ShowBanner(critical: false, $"Couldn't reset history ({ex.GetType().Name}).");
        }
    }

    // ── Test notification — bypass alert pipeline ──────────────────────

    private void OnTestNotificationClick(object sender, RoutedEventArgs e)
    {
        // Direct call into MainWindow.ShowTestToast — the same Tray
        // notification path real alerts use. Lets the user verify the OS
        // toast wiring without waiting for an alert to fire.
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.ShowTestToast();
        }
    }

    // ── About: shell-open the repo link ─────────────────────────────────

    private void OnRepoLinkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute = true hands off to the user's default
            // browser; ZenVizor itself emits no network traffic. This is
            // load-bearing for CLAUDE.md invariant 1.
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/mitchellvette/zenvizor",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; the link is a courtesy, not a feature.
        }
    }

    // ── Apply / banner / theme plumbing ─────────────────────────────────

    private async Task ApplyAsync(SettingsUpdate update)
    {
        // Defensive: never call UpdateSettingsAsync before the page has
        // successfully hydrated. WPF deferred events fired by the binding
        // system during Hydrate could otherwise reach here with
        // _suppressApply already reset to false, triggering a spurious
        // "Couldn't save change" banner on the very first page load.
        if (!_hasHydrated) return;

        try
        {
            await Settings.UpdateSettingsAsync(update);
            HideBanner();
            if (_vm.Content == SettingsViewModel.PageContent.Error)
            {
                _vm.Content = SettingsViewModel.PageContent.Populated;
            }
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            _vm.Content = SettingsViewModel.PageContent.Disconnected;
            ShowBanner(critical: false,
                "Service disconnected. Your change wasn't saved.",
                glyph: SymbolRegular.PlugDisconnected20);
        }
        catch (Exception ex) when (SettingsClient.IsMethodNotFound(ex))
        {
            // Same "service is older than UI" condition GetSettingsAsync
            // surfaces — but per-save. Theme changes still take effect
            // locally (ApplyThemeImmediate + ThemePreferenceStore.Save
            // ran before this call), so the banner reads as informational
            // not "your change was lost."
            _vm.Content = SettingsViewModel.PageContent.Error;
            ShowBanner(critical: false,
                "Changes can't be saved. The ZenVizor service is older than this app; " +
                "restart the service to enable changes.");
        }
        catch (Exception ex)
        {
            _vm.Content = SettingsViewModel.PageContent.Error;
            ShowBanner(critical: false, $"Couldn't save change ({ex.GetType().Name}).");
        }
    }

    private static void ApplyThemeImmediate(IpcAppTheme picked)
    {
        // System: re-enable the OS watcher path. Light/Dark: pin the theme.
        // updateAccent stays false so the brand violet ramp is preserved
        // (see App.xaml.cs comments).
        switch (picked)
        {
            case IpcAppTheme.System:
                ApplicationThemeManager.ApplySystemTheme(updateAccent: false);
                break;
            case IpcAppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, updateAccent: false);
                break;
            case IpcAppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, updateAccent: false);
                break;
        }
    }

    private void OnVmContentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.Content)) return;
        // Disconnected state disables every input. Done via IsEnabled on
        // the form's outer ScrollViewer since per-control bindings would
        // proliferate without buying anything.
        FormScroll.IsEnabled = _vm.Content != SettingsViewModel.PageContent.Disconnected;
    }

    /// <summary>
    /// Paints the inline status banner above the Settings form. The
    /// <paramref name="critical"/> flag is RESERVED for a future
    /// destructive-state surface (data-integrity error, config corruption);
    /// every Phase 6.5 caller passes <c>false</c>. Routine service-
    /// disconnect uses the caution paint with the dedicated
    /// <see cref="Wpf.Ui.Controls.SymbolRegular.PlugDisconnected20"/>
    /// glyph; any other transient error uses the default
    /// <see cref="Wpf.Ui.Controls.SymbolRegular.Warning20"/> glyph.
    /// </summary>
    private void ShowBanner(bool critical, string text, SymbolRegular glyph = SymbolRegular.Warning20)
    {
        StatusBanner.Background = (System.Windows.Media.Brush)FindResource(
            critical ? "status.critical.background" : "status.caution.background");
        var fg = critical ? "status.critical.text" : "status.caution.text";
        StatusBannerGlyph.Symbol = glyph;
        StatusBannerGlyph.Foreground = (System.Windows.Media.Brush)FindResource(fg);
        StatusBannerText.Foreground = (System.Windows.Media.Brush)FindResource(fg);
        StatusBannerText.Text = text;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void HideBanner()
    {
        StatusBanner.Visibility = Visibility.Collapsed;
    }

    // ── About card population ───────────────────────────────────────────

    private void PopulateAbout()
    {
        // User-facing surface uses clean SemVer (no commit hash). The full
        // diagnostic version (with +hash) is still available via
        // `zvctl --version` for support workflows.
        var assembly = typeof(SettingsPage).Assembly;
        var raw = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        AboutVersion.Text = VersionFormatter.Display(raw);
    }

    /// <summary>
    /// Page-internal row VM for the Retention ItemsControl. Carries label
    /// + description + unit cap + a reference to the persisted field.
    /// </summary>
    internal sealed class RetentionRowVm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Label { get; }
        public string Description { get; }
        public SettingsViewModel.RetentionField Field { get; }

        public RetentionRowVm(
            string label,
            string description,
            SettingsViewModel.RetentionField field)
        {
            Label = label;
            Description = description;
            Field = field;
            field.PropertyChanged += (_, args) =>
            {
                // Re-broadcast Field-level changes so XAML's binding on
                // Field.UnitScopedValue updates without needing a deep path.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxForUnit)));
            };
        }

        /// <summary>
        /// 0 = Days, 1 = Months, 2 = Years. Bound to the ComboBox
        /// SelectedIndex so a change on either side mirrors. Setter
        /// converts back to <see cref="SettingsViewModel.RetentionUnit"/>.
        /// </summary>
        public int UnitIndex
        {
            get => (int)Field.Unit;
            set
            {
                var unit = value switch
                {
                    1 => SettingsViewModel.RetentionUnit.Months,
                    2 => SettingsViewModel.RetentionUnit.Years,
                    _ => SettingsViewModel.RetentionUnit.Days,
                };
                if (Field.Unit == unit) return;
                Field.Unit = unit;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitIndex)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxForUnit)));
            }
        }

        /// <summary>
        /// Cap for the NumberBox per the active unit. Matches the IPC
        /// server-side validation: 3650 days = ~10 years; 120 months
        /// = 10 years; 10 years. The cap goes down as the unit grows
        /// to keep the persisted day count under 3650.
        /// </summary>
        public int MaxForUnit => Field.Unit switch
        {
            SettingsViewModel.RetentionUnit.Months => 120,
            SettingsViewModel.RetentionUnit.Years  => 10,
            _                                      => 3650,
        };

        public void RefreshAfterHydrate()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxForUnit)));
        }

        public void RefreshAfterUnitChange()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxForUnit)));
        }
    }
}
