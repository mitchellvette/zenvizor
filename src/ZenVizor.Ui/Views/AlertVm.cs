using System.ComponentModel;
using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Views;

/// <summary>
/// Per-alert view-model wrapping an <see cref="AlertDto"/> with display-ready
/// computed properties. Owned by <see cref="AlertsViewModel"/>; consumed by
/// the per-item <c>DataTemplate</c> in <c>AlertsPage.xaml</c>.
/// <para>
/// <see cref="IsDismissed"/> is the only mutable property — it flips on
/// successful <c>DismissAlertAsync</c> via <see cref="MarkDismissed"/>.
/// Every other field is captured at construction from the immutable DTO,
/// so the type doesn't need full INotifyPropertyChanged on every field.
/// </para>
/// </summary>
internal sealed class AlertVm : INotifyPropertyChanged
{
    private long? _acknowledgedAtUnixMs;
    private bool _isExpanded;

    public AlertVm(AlertDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        AlertId = dto.AlertId;
        Type = dto.Type;
        Severity = dto.Severity;
        CreatedAtUnixMs = dto.CreatedAtUnixMs;
        Source = dto.Source;
        EntityKind = dto.EntityKind;
        EntityRef = dto.EntityRef;
        Title = dto.Title;
        Detail = dto.Detail;
        _acknowledgedAtUnixMs = dto.AcknowledgedAtUnixMs;
    }

    public long AlertId { get; }
    public AlertType Type { get; }
    public NotableSeverity Severity { get; }
    public long CreatedAtUnixMs { get; }
    public SourceMonitor Source { get; }
    public AlertEntityKind EntityKind { get; }
    public string EntityRef { get; }
    public string Title { get; }
    public string Detail { get; }

    // ---- Display-ready computed properties (read by DataTemplate bindings) ----

    public DateTime CreatedAtLocal =>
        DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUnixMs).LocalDateTime;

    /// <summary>
    /// "2026-06-11  14:32" — ISO date + two-space gap + HH:mm. Two-space
    /// gap reads cleaner than a single hyphen between two numeric runs
    /// (matches the mockup pg 2 metadata row).
    /// </summary>
    public string CreatedAtDisplay =>
        CreatedAtLocal.ToString("yyyy-MM-dd  HH:mm");

    public bool IsDismissed => _acknowledgedAtUnixMs.HasValue;

    /// <summary>
    /// Inverse of <see cref="IsDismissed"/>. Bound by per-item bindings
    /// that need "visible when active" (the Dismiss button slot) without
    /// the boilerplate of a custom invertible converter.
    /// </summary>
    public bool IsActive => !_acknowledgedAtUnixMs.HasValue;

    /// <summary>
    /// When this alert was dismissed (server-authoritative on a load; set
    /// optimistically by <see cref="MarkDismissed"/> on a UI dismiss click).
    /// Null when active.
    /// </summary>
    public long? DismissedAtUnixMs => _acknowledgedAtUnixMs;

    /// <summary>
    /// "dismissed 2026-06-14  20:09" — format matches CreatedAtDisplay so the
    /// meta row reads consistently when both timestamps appear. Empty string
    /// when not dismissed (the consuming TextBlock is collapsed via the
    /// IsDismissed BoolToVisibility binding so the empty value never paints).
    /// </summary>
    public string DismissedAtDisplay
    {
        get
        {
            if (!_acknowledgedAtUnixMs.HasValue) return string.Empty;
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(_acknowledgedAtUnixMs.Value).LocalDateTime;
            return dt.ToString("yyyy-MM-dd  HH:mm");
        }
    }

    /// <summary>
    /// Whether the per-item "Why this matters" disclosure is open. Bound by
    /// the DataTemplate to drive (a) the body Border's Visibility and (b)
    /// the chevron's RotateTransform.Angle storyboard. Toggled by
    /// <see cref="ToggleExpanded"/> from the link's click handler.
    /// Defaults false; each item enters the feed collapsed.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    public string TypeDisplayName       => AlertCatalogLookups.DisplayName(Type);
    public string SourceLabel           => AlertCatalogLookups.SourceLabel(Source);
    public string WhyMatters            => AlertCatalogLookups.WhyMatters(Type);
    public string SeverityDisplayName   => AlertCatalogLookups.SeverityDisplayName(Severity);
    public SymbolRegular Icon           => AlertCatalogLookups.Icon(Type);

    /// <summary>
    /// Marks this alert as dismissed. Idempotent — repeated calls are
    /// silent no-ops, mirroring the IPC contract's idempotent
    /// <c>DismissAlertAsync</c>.
    /// </summary>
    public void MarkDismissed(long whenUnixMs)
    {
        if (_acknowledgedAtUnixMs.HasValue) return;
        _acknowledgedAtUnixMs = whenUnixMs;
        OnPropertyChanged(nameof(IsDismissed));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(DismissedAtUnixMs));
        OnPropertyChanged(nameof(DismissedAtDisplay));
    }

    /// <summary>
    /// Reverts a previous <see cref="MarkDismissed"/>. Reserved for the
    /// optimistic-update rollback path in <c>AlertsPage.OnDismissAlertClick</c>
    /// — when the server-side <c>DismissAlertAsync</c> throws, the page rolls
    /// back the optimistic flip so the card returns to its active state.
    /// No-op if already active. NOT a general "un-dismiss" surface; the
    /// server is the authority on durable state and this method exists
    /// purely to undo a not-yet-confirmed local mutation.
    /// </summary>
    public void RollbackDismissed()
    {
        if (!_acknowledgedAtUnixMs.HasValue) return;
        _acknowledgedAtUnixMs = null;
        OnPropertyChanged(nameof(IsDismissed));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(DismissedAtUnixMs));
        OnPropertyChanged(nameof(DismissedAtDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
