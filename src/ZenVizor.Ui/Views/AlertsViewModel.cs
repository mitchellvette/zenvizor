using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Ui.Views;

/// <summary>
/// Page-level view-model for <see cref="AlertsPage"/>. Owns the filtered
/// alert collection, the in-memory filter pipeline (Severity + Type
/// applied client-side per brief §14; State server-side via
/// <c>GetAlertsAsync</c>), the KPI count derivation, and the page-state
/// machine that drives the visibility shells (loading / disconnected /
/// error / empty / filtered-empty / populated).
/// <para>
/// Phase 4a (this commit) seeds the collection with synthetic mock data
/// drawn from the brief §3 sample instances so the layout is visually
/// verifiable without the IPC round-trip. Phase 4b replaces the seed with
/// a real <c>AlertsClient.GetAlertsAsync</c> call on page load and wires
/// the <c>AlertRaised</c> push subscription to inject new rows.
/// </para>
/// </summary>
internal sealed class AlertsViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// What the page is currently showing. Drives which content shell
    /// (feed / loading ring / empty medallion / filtered-empty CTA) is
    /// visible. Distinct from the connection-level state (a populated
    /// feed can coexist with a stale disconnected banner — see the
    /// mockup's disconnected state on pg 6).
    /// </summary>
    public enum PageContent
    {
        /// <summary>Initial query in flight; show the centered ProgressRing.</summary>
        Loading,
        /// <summary>Zero alerts in the system at all.</summary>
        NoAlerts,
        /// <summary>Alerts exist but the active filter hides every one.</summary>
        FilteredEmpty,
        /// <summary>Default — feed has matched items to render.</summary>
        Populated,
    }

    /// <summary>
    /// Service-connection state, controls the inline banner above the
    /// feed (status.critical.background for disconnected; status.caution
    /// for query failure; collapsed when steady).
    /// </summary>
    public enum BannerState
    {
        None,
        Disconnected,
        Error,
    }

    // ---- The full in-memory set + the filtered view -----------------------

    private readonly List<AlertVm> _allAlerts = new();

    /// <summary>
    /// The filtered ordered collection bound to the feed ListView. Updated
    /// in place by <see cref="ApplyFilter"/> so virtualization survives
    /// filter changes (rebuilding a fresh collection forces a reset, which
    /// blows away the recycled item containers).
    /// </summary>
    public ObservableCollection<AlertVm> Alerts { get; } = new();

    // ---- Filter state (bound from filter bar) ------------------------------

    private AlertState _selectedState = AlertState.Active;
    public AlertState SelectedState
    {
        get => _selectedState;
        set
        {
            if (_selectedState == value) return;
            _selectedState = value;
            OnPropertyChanged();
            // State is the server-applied axis (brief §14). The page
            // subscribes to this PropertyChanged and issues a fresh
            // GetAlertsAsync with the new filter; the result lands via
            // LoadAlerts which calls ApplyFilter. We deliberately do NOT
            // ApplyFilter locally here — the existing _allAlerts only
            // holds rows matching the PREVIOUS State filter, so filtering
            // them with the new State predicate would briefly render
            // FilteredEmpty (or wrong contents) before the round-trip
            // completes. Severity and Type axes remain client-side and
            // call ApplyFilter from their setters as before.
        }
    }

    private bool _severityCriticalEnabled = true;
    public bool SeverityCriticalEnabled
    {
        get => _severityCriticalEnabled;
        set { if (_severityCriticalEnabled == value) return; _severityCriticalEnabled = value; OnPropertyChanged(); ApplyFilter(); }
    }

    private bool _severityWarningEnabled = true;
    public bool SeverityWarningEnabled
    {
        get => _severityWarningEnabled;
        set { if (_severityWarningEnabled == value) return; _severityWarningEnabled = value; OnPropertyChanged(); ApplyFilter(); }
    }

    private bool _severityInfoEnabled = true;
    public bool SeverityInfoEnabled
    {
        get => _severityInfoEnabled;
        set { if (_severityInfoEnabled == value) return; _severityInfoEnabled = value; OnPropertyChanged(); ApplyFilter(); }
    }

    /// <summary>
    /// Multi-select Type filter. All six catalog types enabled by default;
    /// the filter-by-type control lists every type even when only
    /// <c>UnsignedFromUserPath</c> has a producer in Phase 6 (brief §3.7
    /// lock — no Phase-6-only special-case design).
    /// </summary>
    public HashSet<AlertType> EnabledTypes { get; } =
        new(Enum.GetValues<AlertType>());

    public void SetTypeEnabled(AlertType type, bool enabled)
    {
        bool changed = enabled ? EnabledTypes.Add(type) : EnabledTypes.Remove(type);
        if (changed)
        {
            OnPropertyChanged(nameof(EnabledTypes));
            OnPropertyChanged(nameof(IsFilterAtDefault));
            OnPropertyChanged(nameof(IsFilterNotAtDefault));
            OnPropertyChanged(nameof(TypeFilterLabel));
            OnPropertyChanged(nameof(TypeFilterTooltip));
            ApplyFilter();
        }
    }

    /// <summary>
    /// True iff every filter axis is at its default (Active state, all
    /// severities on, all types on). Useful for diagnostics; the Reset
    /// link binds to <see cref="IsFilterNotAtDefault"/> instead so it
    /// can use the stock BooleanToVisibilityConverter without an
    /// invert parameter.
    /// </summary>
    public bool IsFilterAtDefault =>
        _selectedState == AlertState.Active
        && _severityCriticalEnabled && _severityWarningEnabled && _severityInfoEnabled
        && EnabledTypes.Count == Enum.GetValues<AlertType>().Length;

    /// <summary>Inverse of <see cref="IsFilterAtDefault"/>. Reset link visibility binds here.</summary>
    public bool IsFilterNotAtDefault => !IsFilterAtDefault;

    /// <summary>
    /// Dynamic label for the Type filter ContextMenu button. Always uses a
    /// count format so the label fits in any reasonable button width — even
    /// at narrow window widths where the parent column squeezes the button.
    /// The single-type display name (which can be up to ~30 chars: "Higher-
    /// than-usual data use") goes to <see cref="TypeFilterTooltip"/> instead;
    /// the user opens the menu (one click) to see exactly which type is
    /// selected when only one is.
    /// <para>
    /// Format: "All types" (default) / "{N} of 6 types" (2-5 enabled) /
    /// "1 type" (exactly one) / "No types" (none enabled). Longest possible
    /// label is "5 of 6 types" (~84px) — safely fits MinWidth=140.
    /// </para>
    /// </summary>
    public string TypeFilterLabel
    {
        get
        {
            var allCount = Enum.GetValues<AlertType>().Length;
            var enabled = EnabledTypes.Count;
            if (enabled == allCount) return "All types";
            if (enabled == 0)        return "No types";
            if (enabled == 1)        return "1 type";
            return $"{enabled} of {allCount} types";
        }
    }

    /// <summary>
    /// Hover tooltip for the Type filter button. Carries the comma-joined
    /// list of enabled type display names so the user can read the full
    /// filter without opening the dropdown. Returns null in the all-enabled
    /// (default) and none-enabled (cleared) states so the ToolTip
    /// suppresses — the label already communicates those clearly.
    /// </summary>
    public string? TypeFilterTooltip
    {
        get
        {
            var allCount = Enum.GetValues<AlertType>().Length;
            var enabled = EnabledTypes.Count;
            if (enabled == allCount || enabled == 0) return null;
            // Preserve canonical AlertType ordering (declaration order) so
            // the tooltip reads consistently across runs regardless of
            // HashSet enumeration order.
            var names = Enum.GetValues<AlertType>()
                .Where(t => EnabledTypes.Contains(t))
                .Select(t => AlertCatalogLookups.DisplayName(t));
            return string.Join(", ", names);
        }
    }

    public void ResetFilter()
    {
        SelectedState = AlertState.Active;
        SeverityCriticalEnabled = SeverityWarningEnabled = SeverityInfoEnabled = true;
        EnabledTypes.UnionWith(Enum.GetValues<AlertType>());
        OnPropertyChanged(nameof(EnabledTypes));
        OnPropertyChanged(nameof(IsFilterAtDefault));
        OnPropertyChanged(nameof(IsFilterNotAtDefault));
        OnPropertyChanged(nameof(TypeFilterLabel));
        OnPropertyChanged(nameof(TypeFilterTooltip));
        ApplyFilter();
    }

    // ---- KPI count surface --------------------------------------------------

    private int _activeCount;
    public int ActiveCount { get => _activeCount; private set { _activeCount = value; OnPropertyChanged(); } }

    private int _criticalCount;
    public int CriticalCount { get => _criticalCount; private set { _criticalCount = value; OnPropertyChanged(); } }

    private int _warningCount;
    public int WarningCount { get => _warningCount; private set { _warningCount = value; OnPropertyChanged(); } }

    private int _infoCount;
    public int InfoCount { get => _infoCount; private set { _infoCount = value; OnPropertyChanged(); } }

    /// <summary>
    /// Highest active severity, or null when there are no active alerts.
    /// Drives the nav-rail badge tint.
    /// </summary>
    public NotableSeverity? HighestActiveSeverity
    {
        get
        {
            if (_criticalCount > 0) return NotableSeverity.Critical;
            if (_warningCount  > 0) return NotableSeverity.Warning;
            if (_infoCount     > 0) return NotableSeverity.Info;
            return null;
        }
    }

    // ---- Page state surface -------------------------------------------------

    private PageContent _content = PageContent.Loading;
    public PageContent Content
    {
        get => _content;
        private set { if (_content == value) return; _content = value; OnPropertyChanged(); }
    }

    private BannerState _banner = BannerState.None;
    public BannerState Banner
    {
        get => _banner;
        private set { if (_banner == value) return; _banner = value; OnPropertyChanged(); }
    }

    private string _bannerMessage = string.Empty;
    public string BannerMessage
    {
        get => _bannerMessage;
        private set { if (_bannerMessage == value) return; _bannerMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Count of alerts hidden by the active filter combination. Used by
    /// the filtered-empty CTA copy ("Reset filter to see N hidden alerts").
    /// </summary>
    public int HiddenByFilterCount
        => _allAlerts.Count(a => MatchesStateAxis(a) && !MatchesUiAxes(a));

    /// <summary>
    /// Push-subscription hook the page wires to its
    /// <see cref="AlertsClient.AlertRaised"/> event. Marshals to the
    /// dispatcher at the page level; this method assumes it's already
    /// on the UI thread.
    /// </summary>
    public void OnAlertRaised(AlertDto alert)
    {
        // Phase 4b will inject the new row into the right spot
        // (reverse-chronological, top-insert). Phase 4a leaves this
        // unused — the synthetic seed populates the feed directly.
        var vm = new AlertVm(alert);
        _allAlerts.Insert(0, vm);
        RebuildKpiCounts();
        ApplyFilter();
    }

    // ---- Filter pipeline ----------------------------------------------------

    private bool MatchesStateAxis(AlertVm alert) => _selectedState switch
    {
        AlertState.Active    => !alert.IsDismissed,
        AlertState.Dismissed => alert.IsDismissed,
        AlertState.All       => true,
        _                    => true,
    };

    private bool MatchesUiAxes(AlertVm alert)
    {
        bool severityOk = alert.Severity switch
        {
            NotableSeverity.Critical => _severityCriticalEnabled,
            NotableSeverity.Warning  => _severityWarningEnabled,
            NotableSeverity.Info     => _severityInfoEnabled,
            _                        => true,
        };
        return severityOk && EnabledTypes.Contains(alert.Type);
    }

    private void ApplyFilter()
    {
        Alerts.Clear();
        foreach (var alert in _allAlerts)
        {
            if (MatchesStateAxis(alert) && MatchesUiAxes(alert))
            {
                Alerts.Add(alert);
            }
        }
        RecomputePageContent();
        OnPropertyChanged(nameof(IsFilterAtDefault));
        OnPropertyChanged(nameof(IsFilterNotAtDefault));
        OnPropertyChanged(nameof(HiddenByFilterCount));
    }

    private void RebuildKpiCounts()
    {
        int active = 0, crit = 0, warn = 0, info = 0;
        foreach (var alert in _allAlerts)
        {
            if (alert.IsDismissed) continue;
            active++;
            switch (alert.Severity)
            {
                case NotableSeverity.Critical: crit++; break;
                case NotableSeverity.Warning:  warn++; break;
                case NotableSeverity.Info:     info++; break;
            }
        }
        ActiveCount   = active;
        CriticalCount = crit;
        WarningCount  = warn;
        InfoCount     = info;
        OnPropertyChanged(nameof(HighestActiveSeverity));
    }

    private void RecomputePageContent()
    {
        // Banner state is computed independently in SetConnectionState —
        // it can coexist with any Content state (e.g. disconnected banner
        // over a populated feed).
        if (_allAlerts.Count == 0)
        {
            Content = PageContent.NoAlerts;
        }
        else if (Alerts.Count == 0)
        {
            Content = PageContent.FilteredEmpty;
        }
        else
        {
            Content = PageContent.Populated;
        }
    }

    // ---- Data injection points ---------------------------------------------

    /// <summary>
    /// Replaces the in-memory set with a freshly-loaded batch (from
    /// <c>GetAlertsAsync</c>). Recomputes KPI counts and reapplies the
    /// active filter. Phase 4b wires this from <see cref="AlertsPage"/>.
    /// </summary>
    public void LoadAlerts(IEnumerable<AlertDto> alerts)
    {
        _allAlerts.Clear();
        foreach (var dto in alerts)
        {
            _allAlerts.Add(new AlertVm(dto));
        }
        RebuildKpiCounts();
        ApplyFilter();
    }

    public void SetLoading()
    {
        Content = PageContent.Loading;
    }

    /// <summary>
    /// True when the in-memory set has at least one row. Used by the page's
    /// dev-only seed-bypass branch in <c>RefreshAsync</c>: once the seed has
    /// populated <c>_allAlerts</c>, the page skips the server round-trip on
    /// subsequent State chip flips so that in-place user mutations
    /// (dismissals) survive. Production code paths (seed flag off) don't
    /// touch this property.
    /// </summary>
    public bool HasAnyAlerts => _allAlerts.Count > 0;

    /// <summary>
    /// Re-runs the filter pipeline against the current in-memory rows
    /// without replacing them. Thin public surface over the private
    /// <see cref="ApplyFilter"/> for the dev-seed bypass; not part of the
    /// production refresh path.
    /// </summary>
    public void RefilterOnly() => ApplyFilter();

    /// <summary>
    /// Locally marks an alert as dismissed and re-runs the KPI + filter
    /// pipeline so the nav badge, KPI strip, and visible feed all
    /// reflect the optimistic state change. Page calls this BEFORE
    /// awaiting <c>DismissAlertAsync</c>; on failure it pairs with
    /// <see cref="RollbackDismiss"/> to revert.
    /// </summary>
    public void MarkAlertDismissed(long alertId, long whenUnixMs)
    {
        var vm = _allAlerts.FirstOrDefault(a => a.AlertId == alertId);
        if (vm is null || vm.IsDismissed) return;
        vm.MarkDismissed(whenUnixMs);
        RebuildKpiCounts();
        ApplyFilter();
    }

    /// <summary>
    /// Reverts the optimistic update made by <see cref="MarkAlertDismissed"/>
    /// when the server-side dismiss call fails. Re-runs KPI + filter so
    /// the card reappears (if filtered out) and the badge re-increments.
    /// </summary>
    public void RollbackDismiss(long alertId)
    {
        var vm = _allAlerts.FirstOrDefault(a => a.AlertId == alertId);
        if (vm is null || !vm.IsDismissed) return;
        vm.RollbackDismissed();
        RebuildKpiCounts();
        ApplyFilter();
    }

    public void SetBanner(BannerState state, string message)
    {
        // BannerMessage assigned BEFORE Banner so that the page's
        // PropertyChanged handler — which reacts to Banner by calling
        // ApplyBannerToUi (which reads VM.BannerMessage) — sees the
        // current message text, not the prior call's stale value.
        // Reverse order produces an off-by-one banner: every SetBanner
        // call paints the banner with the PREVIOUS message; first call
        // shows empty text. ClearBanner doesn't have this concern
        // because ApplyBannerToUi's None case just collapses the
        // banner without reading BannerMessage.
        BannerMessage = message;
        Banner = state;
    }

    public void ClearBanner()
    {
        Banner = BannerState.None;
        BannerMessage = string.Empty;
    }

    // ---- INotifyPropertyChanged ---------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // ---- Phase 4a synthetic seed --------------------------------------------

    /// <summary>
    /// Seeds the in-memory set with the six brief §3 sample instances so the
    /// Phase 4a layout is visually verifiable without a Phase 6 producer.
    /// Phase 4b removes this and replaces it with a real GetAlertsAsync call.
    /// </summary>
    public void SeedSyntheticForPhase4aPreview()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long MinuteMs = 60_000L;

        var samples = new[]
        {
            new AlertDto(
                AlertId: 1,
                Type: AlertType.UnsignedFromUserPath,
                Severity: NotableSeverity.Critical,
                CreatedAtUnixMs: nowMs - 15 * MinuteMs,
                Source: SourceMonitor.Capture,
                EntityKind: AlertEntityKind.App,
                EntityRef: "7",
                Title: "Unsigned program talking to the network: 7zG.exe",
                Detail: "7zG.exe is running from a user-writable folder and started making network connections. " +
                        "Image path: C:\\Users\\Mitch\\AppData\\Local\\Temp\\7zS9F2A3.tmp\\7zG.exe. " +
                        "Signer: none (unsigned). First connection: 2026-06-11 14:32. Connections so far: 3.",
                AcknowledgedAtUnixMs: null),
            new AlertDto(
                AlertId: 2,
                Type: AlertType.InvalidSignature,
                Severity: NotableSeverity.Critical,
                CreatedAtUnixMs: nowMs - 45 * MinuteMs,
                Source: SourceMonitor.Capture,
                EntityKind: AlertEntityKind.App,
                EntityRef: "12",
                Title: "Program signature does not verify: legacy-installer.exe",
                Detail: "legacy-installer.exe started a network connection while its signature did not verify. " +
                        "Image path: C:\\Program Files (x86)\\OldVendor\\legacy-installer.exe. " +
                        "Signer: OldVendor LLC (signature invalid). First connection: 2026-06-11 09:15. Connections so far: 1.",
                AcknowledgedAtUnixMs: null),
            new AlertDto(
                AlertId: 3,
                Type: AlertType.FirstRunWanTalker,
                Severity: NotableSeverity.Info,
                CreatedAtUnixMs: nowMs - 2 * 60 * MinuteMs,
                Source: SourceMonitor.Capture,
                EntityKind: AlertEntityKind.App,
                EntityRef: "21",
                Title: "First-time program reached the network: Notion.exe",
                Detail: "Notion.exe was seen for the first time and connected to a remote endpoint within seconds. " +
                        "Image path: C:\\Users\\Mitch\\AppData\\Local\\Programs\\Notion\\Notion.exe. " +
                        "Signer: Notion Labs, Inc. First seen: 2026-06-11 10:48. First connection: 2026-06-11 10:48.",
                AcknowledgedAtUnixMs: null),
            new AlertDto(
                AlertId: 4,
                Type: AlertType.UnusualDailyVolume,
                Severity: NotableSeverity.Warning,
                CreatedAtUnixMs: nowMs - 3 * 60 * MinuteMs,
                Source: SourceMonitor.Rollup,
                EntityKind: AlertEntityKind.App,
                EntityRef: "33",
                Title: "Higher-than-usual data use today: chrome.exe",
                Detail: "chrome.exe has moved 8.4 GB today, against a typical 1.9 GB over the past 14 days. " +
                        "Today's volume is about 4.4 times the recent median. " +
                        "Open the program's history to see when the activity spiked.",
                AcknowledgedAtUnixMs: null),
            new AlertDto(
                AlertId: 5,
                Type: AlertType.LargeDownload,
                Severity: NotableSeverity.Info,
                CreatedAtUnixMs: nowMs - 8 * 60 * MinuteMs,
                Source: SourceMonitor.Capture,
                EntityKind: AlertEntityKind.Session,
                EntityRef: "447",
                Title: "Large download in progress: MicrosoftEdgeUpdate.exe",
                Detail: "MicrosoftEdgeUpdate.exe pulled down 187 MB from an endpoint it had not used today. " +
                        "Image path: C:\\Program Files (x86)\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe. " +
                        "Signer: Microsoft Corporation. Started: 2026-06-11 13:21.",
                AcknowledgedAtUnixMs: nowMs - 5 * 60 * MinuteMs),
            new AlertDto(
                AlertId: 6,
                Type: AlertType.OutboundHeavy,
                Severity: NotableSeverity.Warning,
                CreatedAtUnixMs: nowMs - 24 * 60 * MinuteMs,
                Source: SourceMonitor.Rollup,
                EntityKind: AlertEntityKind.App,
                EntityRef: "58",
                Title: "Uploads dominated downloads today: Backblaze.exe",
                Detail: "Backblaze.exe sent 4.1 GB and received 78 MB today. " +
                        "The outbound-to-inbound ratio is unusual; backup clients legitimately look like this. " +
                        "Signer: Backblaze, Inc.",
                AcknowledgedAtUnixMs: nowMs - 10 * 60 * MinuteMs),
        };

        LoadAlerts(samples);
    }
}
