using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// What a rule wants the producer to raise. Intent only — the producer
/// applies the connection-count counter and renders the final detail string
/// via <see cref="IAlertRule.RenderDetail"/> before calling
/// <see cref="IAlertSink.TryInsert"/>. The split exists so the SAME rule
/// can drive both the initial raise (call site: <c>TryEvaluate</c>) and
/// subsequent count-update renders (call site: <c>UpdateDetail</c>) without
/// the rule learning about the producer's caching strategy.
/// </summary>
public sealed record RaiseRequest(
    AlertType Type,
    NotableSeverity Severity,
    SourceMonitor SourceMonitor,
    AlertEntityKind EntityKind,
    string EntityRef,
    int? AppId,
    string Title);
