// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Views;

/// <summary>
/// View-model for <see cref="SettingsPage"/>. Each persisted knob is a
/// distinct INPC property so the page can wire per-control bindings; the
/// page (not the view-model) owns the IPC client and the debounce-+-apply
/// orchestration so a future Settings-headless host can substitute its
/// own persistence without touching the visible state.
/// </summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public enum PageContent
    {
        /// <summary>Initial GetSettingsAsync in flight — show a centered ProgressRing.</summary>
        Loading,

        /// <summary>Settings loaded; form is interactive.</summary>
        Populated,

        /// <summary>Service pipe is down; form is read-only with a critical banner.</summary>
        Disconnected,

        /// <summary>An UpdateSettingsAsync round-trip faulted; form is interactive but a caution banner is showing.</summary>
        Error,
    }

    private PageContent _content = PageContent.Loading;
    public PageContent Content
    {
        get => _content;
        set => SetField(ref _content, value);
    }

    private string? _bannerText;
    public string? BannerText
    {
        get => _bannerText;
        set => SetField(ref _bannerText, value);
    }

    private string? _resetHistoryStatus;
    /// <summary>
    /// One-line confirmation after a successful Reset history call — e.g.
    /// "Cleared 1.2M rows". Cleared on next page load.
    /// </summary>
    public string? ResetHistoryStatus
    {
        get => _resetHistoryStatus;
        set => SetField(ref _resetHistoryStatus, value);
    }

    // ── Service ─────────────────────────────────────────────────────────

    private bool _autostartEnabled = true;
    /// <summary>
    /// UI binds a ToggleSwitch here: ON = Automatic, OFF = Manual. The
    /// page maps to <see cref="ServiceStartMode"/> on apply.
    /// </summary>
    public bool AutostartEnabled
    {
        get => _autostartEnabled;
        set => SetField(ref _autostartEnabled, value);
    }

    private bool _startMinimized;
    /// <summary>
    /// When true, the UI hides the window on launch so it goes straight
    /// to the tray. Cached locally via <see cref="StartMinimizedStore"/>
    /// so the boot-time launch reads it synchronously.
    /// </summary>
    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetField(ref _startMinimized, value);
    }

    // ── Alert thresholds (Phase 6.7) ────────────────────────────────────

    private int _alertLargeDownloadMb = 50;
    /// <summary>LargeDownload rule MB threshold. Range 1-1024.</summary>
    public int AlertLargeDownloadMb
    {
        get => _alertLargeDownloadMb;
        set => SetField(ref _alertLargeDownloadMb, value);
    }

    private int _alertOutboundHeavyFloorMb = 10;
    /// <summary>OutboundHeavy minimum outbound MB. Range 1-1024.</summary>
    public int AlertOutboundHeavyFloorMb
    {
        get => _alertOutboundHeavyFloorMb;
        set => SetField(ref _alertOutboundHeavyFloorMb, value);
    }

    private double _alertUnusualDailyVolumeK = 2.5;
    /// <summary>
    /// UnusualDailyVolume sensitivity multiplier. Wire format on the IPC
    /// is integer × 10 (so 2.5 ↔ 25); the VM exposes the decimal form to
    /// the UI for natural display + edit. Range 1.0-10.0.
    /// </summary>
    public double AlertUnusualDailyVolumeK
    {
        get => _alertUnusualDailyVolumeK;
        set => SetField(ref _alertUnusualDailyVolumeK, value);
    }

    // ── Capture (read-only diagnostic) ──────────────────────────────────

    private int _flushIntervalMs = 5000;
    public int FlushIntervalMs
    {
        get => _flushIntervalMs;
        set
        {
            if (SetField(ref _flushIntervalMs, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlushIntervalDescription)));
            }
        }
    }

    private int _flushBucketSeconds = 60;
    public int FlushBucketSeconds
    {
        get => _flushBucketSeconds;
        set
        {
            if (SetField(ref _flushBucketSeconds, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BucketSizeDescription)));
            }
        }
    }

    /// <summary>
    /// Human-readable sentence carrying both the meaning AND the current
    /// value of the flush cadence, surfaced inline in the Capture card so
    /// users don't have to read two surfaces to learn "what is it set to."
    /// </summary>
    public string FlushIntervalDescription =>
        $"Aggregated counters flush from memory to the database every {FormatSeconds(_flushIntervalMs / 1000.0)}.";

    /// <summary>
    /// Same shape as <see cref="FlushIntervalDescription"/> — value-aware
    /// description of the on-disk traffic bucket size.
    /// </summary>
    public string BucketSizeDescription =>
        $"Each on-disk traffic sample covers {FormatSeconds(_flushBucketSeconds)}. Smaller buckets mean more rows; larger buckets coarsen the timeline.";

    private static string FormatSeconds(double seconds)
    {
        if (seconds < 1) return $"{seconds * 1000:0} ms";
        if (seconds < 60) return seconds == 1 ? "1 second" : $"{seconds:0.#} seconds";
        var minutes = seconds / 60.0;
        return minutes == 1 ? "1 minute" : $"{minutes:0.#} minutes";
    }

    // ── Retention (NumberBox + Unit ComboBox per row) ───────────────────
    //
    // Persisted shape is always days (server contract). The VM holds the
    // canonical day count + a display unit per row. The page binds the
    // NumberBox to the unit-scoped value (e.g., "3" when unit=Years and
    // days=1095) via a small converter in the page code-behind — the VM
    // exposes both raw days AND unit so a future headless caller can
    // ignore the display unit entirely.

    public sealed class RetentionField : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int _days;
        private RetentionUnit _unit;

        public RetentionField(int initialDays, RetentionUnit initialUnit)
        {
            _days = initialDays;
            _unit = initialUnit;
        }

        /// <summary>The canonical value persisted to the settings table.</summary>
        public int Days
        {
            get => _days;
            set
            {
                if (_days == value) return;
                _days = value;
                Raise(nameof(Days));
                Raise(nameof(UnitScopedValue));
            }
        }

        /// <summary>Display unit selected by the user. Defaults set per tier on first load.</summary>
        public RetentionUnit Unit
        {
            get => _unit;
            set
            {
                if (_unit == value) return;
                _unit = value;
                Raise(nameof(Unit));
                Raise(nameof(UnitScopedValue));
            }
        }

        /// <summary>
        /// Days expressed in the active unit. Set this from the NumberBox;
        /// the setter rescales to canonical days. Months = 30 days,
        /// Years = 365 days — matches the IPC validation contract.
        /// </summary>
        public int UnitScopedValue
        {
            get => _unit switch
            {
                RetentionUnit.Months => Math.Max(1, _days / 30),
                RetentionUnit.Years  => Math.Max(1, _days / 365),
                _                    => _days,
            };
            set
            {
                var newDays = _unit switch
                {
                    RetentionUnit.Months => Math.Max(1, value) * 30,
                    RetentionUnit.Years  => Math.Max(1, value) * 365,
                    _                    => Math.Max(1, value),
                };
                Days = newDays;
            }
        }

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum RetentionUnit { Days, Months, Years }

    public RetentionField Samples            { get; } = new(30,  RetentionUnit.Days);
    public RetentionField Connections        { get; } = new(30,  RetentionUnit.Days);
    public RetentionField HourlyRollups      { get; } = new(90,  RetentionUnit.Days);
    public RetentionField DailyRollups       { get; } = new(365, RetentionUnit.Years);
    public RetentionField AlertsAfterDismiss { get; } = new(90,  RetentionUnit.Days);

    // ── Alerts ──────────────────────────────────────────────────────────

    private bool _toastOnAlert = true;
    public bool ToastOnAlert
    {
        get => _toastOnAlert;
        set => SetField(ref _toastOnAlert, value);
    }

    // ── Appearance ──────────────────────────────────────────────────────
    //
    // Bound through the page's ComboBox SelectedIndex via converters in
    // code-behind. 0 = System, 1 = Light, 2 = Dark — matches the AppTheme
    // enum order so the projection is a 1:1 cast.

    private AppTheme _theme = AppTheme.System;
    public AppTheme Theme
    {
        get => _theme;
        set => SetField(ref _theme, value);
    }

    private bool _smoothChartAnimations;
    /// <summary>
    /// Phase 9.a — Dashboard chart-animation toggle. Off by default
    /// (running the live-rates + sparkline pair with 2200 ms linear
    /// scroll animation adds ~8% idle CPU while the Dashboard is open).
    /// Effect applies on the next nav to Dashboard, not live to an
    /// already-open Dashboard.
    /// </summary>
    public bool SmoothChartAnimations
    {
        get => _smoothChartAnimations;
        set => SetField(ref _smoothChartAnimations, value);
    }

    /// <summary>
    /// Populate every field from a server snapshot. Called once per page
    /// load + on ServiceReconnected. Picks the friendliest default unit
    /// per tier — Days for short windows, Years for the long daily tier
    /// — but preserves what the user previously chose by inferring the
    /// unit from the day count's divisibility.
    /// </summary>
    public void Hydrate(SettingsSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);

        AutostartEnabled    = s.AutostartMode == ServiceStartMode.Automatic;
        StartMinimized      = s.StartMinimized;
        ToastOnAlert        = s.ToastOnAlert;
        Theme               = s.Theme;
        FlushIntervalMs     = s.FlushIntervalMs;
        FlushBucketSeconds  = s.FlushBucketSeconds;

        HydrateField(Samples,            s.RetentionSamplesDays,        RetentionUnit.Days);
        HydrateField(Connections,        s.RetentionConnectionsDays,    RetentionUnit.Days);
        HydrateField(HourlyRollups,      s.RetentionHourlyDays,         RetentionUnit.Days);
        HydrateField(DailyRollups,       s.RetentionDailyDays,          RetentionUnit.Years);
        HydrateField(AlertsAfterDismiss, s.RetentionAlertsDaysAfterAck, RetentionUnit.Days);

        AlertLargeDownloadMb       = s.AlertLargeDownloadMb;
        AlertOutboundHeavyFloorMb  = s.AlertOutboundHeavyFloorMb;
        AlertUnusualDailyVolumeK   = s.AlertUnusualDailyVolumeKTimesTen / 10.0;
        SmoothChartAnimations      = s.SmoothChartAnimations;
    }

    private static void HydrateField(RetentionField field, int days, RetentionUnit defaultUnit)
    {
        // Choose a unit that produces a tidy whole number. If days is an
        // exact multiple of 365, prefer Years; if of 30, Months; otherwise
        // fall back to the tier's natural default. Avoids surfacing "1095
        // Days" when the user previously picked "3 Years".
        var unit = defaultUnit;
        if (days >= 365 && days % 365 == 0)      unit = RetentionUnit.Years;
        else if (days >= 30 && days % 30 == 0)   unit = RetentionUnit.Months;
        else                                     unit = RetentionUnit.Days;

        field.Unit = unit;
        field.Days = days;
    }

    private bool SetField<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
