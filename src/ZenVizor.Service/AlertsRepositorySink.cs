using System.Runtime.Versioning;
using ZenVizor.Core.Alerts;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Service;

/// <summary>
/// Service-side adapter that lets <see cref="AlertProducer"/> in
/// <c>ZenVizor.Core</c> write through <see cref="AlertsRepository"/> in
/// <c>ZenVizor.Storage</c> without giving Core a direct reference to the
/// Storage project (Core's headless-testable invariant). Two-method bridge
/// — the producer's contract is intentionally narrow so the adapter is
/// trivial and tests can substitute an in-memory <see cref="IAlertSink"/>
/// to assert dedupe behavior without touching SQLite.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AlertsRepositorySink : IAlertSink
{
    private readonly AlertsRepository _repo;

    public AlertsRepositorySink(AlertsRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public long TryInsert(
        string type, string severity, string sourceMonitor,
        string entityKind, string entityRef,
        string title, string detail,
        long nowUnixMs, long cooldownMs)
    {
        var newAlert = new NewAlert(
            Type:          type,
            Severity:      severity,
            SourceMonitor: sourceMonitor,
            EntityKind:    entityKind,
            EntityRef:     entityRef,
            Title:         title,
            Detail:        detail);
        return _repo.TryInsert(newAlert, nowUnixMs, cooldownMs);
    }

    public int UpdateDetail(string type, string entityKind, string entityRef, string detail)
        => _repo.UpdateDetail(type, entityKind, entityRef, detail);
}
