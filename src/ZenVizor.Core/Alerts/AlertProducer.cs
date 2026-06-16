using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Drives alert raises from aggregator flush events. Owns the per-rule
/// dedupe + connection-count state machine; writes through
/// <see cref="IAlertSink"/>; broadcasts new alerts via the
/// <see cref="AlertRaised"/> event the host service forwards to the
/// <c>AlertBroadcaster</c>.
/// <para>
/// Lifecycle: one producer per service process, registered as the
/// aggregator's <see cref="IAlertEventSink"/>. Stateful — owns an in-memory
/// per-active-alert state machine keyed by <c>(type, entity_ref)</c>:
/// </para>
/// <list type="bullet">
///   <item><description>On the first observation for a new alert key:
///   <see cref="IAlertSink.TryInsert"/> creates the row, the producer caches
///   <c>(count=1, first_seen)</c>, and raises <see cref="AlertRaised"/>.</description></item>
///   <item><description>On subsequent observations of a key the producer is
///   already tracking: increment count, render detail with cached first_seen
///   + new count, call <see cref="IAlertSink.UpdateDetail"/>. No
///   <see cref="AlertRaised"/> event for updates — the UI re-fetches on
///   next refresh.</description></item>
///   <item><description>On observations where <see cref="IAlertSink.TryInsert"/>
///   returns 0 (an active row exists in the DB but the producer has no cache
///   entry — typically a service restart): the producer does NOT call
///   <see cref="IAlertSink.UpdateDetail"/>. Restart-drift safety: rather
///   than overwriting a correct DB detail with a regressed first_seen
///   timestamp + reset count, leave the existing detail untouched. New
///   alerts raised AFTER restart track normally; old alerts freeze their
///   detail until dismissed.</description></item>
/// </list>
/// <para>
/// Restart drift consequence: a previously-active alert's "Connections so
/// far: N" phrase is frozen at whatever value was rendered just before the
/// service stopped. Acceptable for Phase 6.1 — the alert is still
/// recognizable and dismissible. A future migration adding a persisted
/// counter column eliminates the freeze; punted until real-world usage
/// shows it matters.
/// </para>
/// </summary>
public sealed class AlertProducer : IAlertEventSink
{
    private readonly IAlertRule[] _rules;
    private readonly IAlertSink _sink;
    private readonly Func<long> _now;
    private readonly ILogger _logger;

    // Per-active-alert state. Keyed by the storage-string forms of (type, entity_ref)
    // so the lookup matches the dedupe predicate the SQL gate uses.
    private readonly object _gate = new();
    private readonly Dictionary<(string Type, string EntityRef), ActiveState> _active = new();

    public AlertProducer(
        IEnumerable<IAlertRule> rules,
        IAlertSink sink,
        Func<long>? nowProvider = null,
        ILogger<AlertProducer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _now = nowProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Fired once per newly-inserted alert row, AFTER the sink commits the
    /// insert. Host service subscribes and forwards to <c>AlertBroadcaster</c>
    /// for fan-out to connected IPC clients. NOT raised for
    /// <see cref="IAlertSink.UpdateDetail"/> calls — the UI re-fetches the
    /// detail on next refresh / next AlertRaised push for some other alert.
    /// </summary>
    public event Action<AlertDto>? AlertRaised;

    public void OnSessionConnectedWan(NewSessionEvent evt)
    {
        if (evt is null) return;

        NewSessionContext ctx;
        try
        {
            ctx = NewSessionContext.From(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AlertProducer: malformed NewSessionEvent (app_id={AppId}); dropping.",
                evt.AppId);
            return;
        }

        foreach (var rule in _rules)
        {
            try
            {
                EvaluateOne(rule, ctx);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AlertProducer: rule {Rule} threw while evaluating " +
                    "session for app_id={AppId}; producer continues with next rule.",
                    rule.GetType().Name, ctx.AppId);
            }
        }
    }

    private void EvaluateOne(IAlertRule rule, NewSessionContext ctx)
    {
        var req = rule.TryEvaluate(ctx);
        if (req is null) return;

        var typeStr = req.Type.ToString();
        var kindStr = req.EntityKind.ToString();
        var refStr  = req.EntityRef;
        var key     = (typeStr, refStr);

        ActiveState? state;
        lock (_gate)
        {
            _active.TryGetValue(key, out state);
        }

        if (state is not null)
        {
            // We're already tracking this alert in this process. Bump count
            // and update detail with the SAME first_seen we cached on the
            // initial raise so the rendered string stays stable.
            int newCount;
            lock (_gate)
            {
                state.Count++;
                newCount = state.Count;
            }

            var renderedCtx = ctx with
            {
                WanConnection = ctx.WanConnection with
                {
                    FirstSeenUnixMs = state.FirstSeenUnixMs,
                },
            };
            var newDetail = rule.RenderDetail(renderedCtx, newCount);
            _sink.UpdateDetail(typeStr, kindStr, refStr, newDetail);
            return;
        }

        // First observation for this key in this process. Try to insert.
        // The SQL gate is dedupe authority: if the row was created in a
        // prior process and is still active (or in cooldown), TryInsert
        // returns 0 and we deliberately do NOT cache or update — preserves
        // whatever detail the prior process rendered.
        var initialDetail = rule.RenderDetail(ctx, connectionCount: 1);
        var firstSeen = ctx.WanConnection.FirstSeenUnixMs;
        var nowMs = _now();
        var alertId = _sink.TryInsert(
            type:          typeStr,
            severity:      req.Severity.ToString(),
            sourceMonitor: req.SourceMonitor.ToString(),
            entityKind:    kindStr,
            entityRef:     refStr,
            title:         req.Title,
            detail:        initialDetail,
            nowUnixMs:     nowMs,
            cooldownMs:    rule.CooldownMs);

        if (alertId == 0)
        {
            // Pre-existing active or cooling-down row. Stay silent — no
            // cache entry, no UpdateDetail, no AlertRaised event. The row
            // already in the DB is authoritative for the active surface.
            return;
        }

        // We created the row. Cache the state for future increment-and-update
        // observations, raise the AlertDto for broadcast.
        lock (_gate)
        {
            _active[key] = new ActiveState
            {
                Count = 1,
                FirstSeenUnixMs = firstSeen,
            };
        }

        var dto = new AlertDto(
            AlertId:              alertId,
            Type:                 req.Type,
            Severity:             req.Severity,
            CreatedAtUnixMs:      nowMs,
            Source:               req.SourceMonitor,
            EntityKind:           req.EntityKind,
            EntityRef:            req.EntityRef,
            Title:                req.Title,
            Detail:               initialDetail,
            AcknowledgedAtUnixMs: null,
            AppId:                req.AppId);

        AlertRaised?.Invoke(dto);
    }

    /// <summary>
    /// Test / diagnostic hook for the optimistic-dismiss flow on the UI
    /// side. When a user dismisses an alert via the IPC handler, the
    /// host service can call this to evict the producer's in-memory state
    /// for the dismissed key so a subsequent qualifying observation post-
    /// cooldown starts cleanly. Production caller wiring is optional — the
    /// SQL gate makes correct decisions either way; this just keeps the
    /// in-memory map from accumulating stale entries.
    /// </summary>
    public void ForgetActive(string type, string entityRef)
    {
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(entityRef)) return;
        lock (_gate)
        {
            _active.Remove((type, entityRef));
        }
    }

    private sealed class ActiveState
    {
        public int Count;
        public long FirstSeenUnixMs;
    }
}
