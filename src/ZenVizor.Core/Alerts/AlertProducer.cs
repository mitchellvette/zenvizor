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
    private readonly IFlushAlertRule[] _flushRules;
    private readonly IAlertSink _sink;
    private readonly Func<long> _now;
    private readonly Func<int, long>? _appFirstSeenLookup;
    private readonly ILogger _logger;

    // Per-active-alert state. Keyed by the storage-string forms of (type, entity_ref)
    // so the lookup matches the dedupe predicate the SQL gate uses.
    private readonly object _gate = new();
    private readonly Dictionary<(string Type, string EntityRef), ActiveState> _active = new();

    public AlertProducer(
        IEnumerable<IAlertRule> rules,
        IAlertSink sink,
        Func<long>? nowProvider = null,
        Func<int, long>? appFirstSeenLookup = null,
        IEnumerable<IFlushAlertRule>? flushRules = null,
        ILogger<AlertProducer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        // Phase 6.7 per-flush rules. Optional — null/empty in test paths
        // and in build phases before LargeDownload/OutboundHeavy/
        // UnusualDailyVolume land. Rules in this set are STATEFUL: they
        // hold per-connection or per-app rolling-window state across
        // flushes.
        _flushRules = flushRules?.ToArray() ?? Array.Empty<IFlushAlertRule>();
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _now = nowProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // Optional. Null in test paths and in production builds where no
        // rule reads it. Phase 6.7 FirstRunWanTalkerRule is the first
        // consumer; host service wires it to a cached SQLite query over
        // apps.first_seen. Zero on miss (race: app inserted after the
        // lookup snapshot) keeps the rule predicate clean — a zero
        // first-seen reads as "infinitely old" which is the correct
        // negative for FirstRunWanTalker.
        _appFirstSeenLookup = appFirstSeenLookup;
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
            // Enrich with the app's first-seen timestamp when the lookup is
            // wired (Phase 6.7+). FirstRunWanTalkerRule reads this; rules
            // that don't care simply leave it at the default zero. Lookup
            // failures (zero return) are treated as "no first-seen known"
            // — the rule's predicate correctly rejects.
            if (_appFirstSeenLookup is not null)
            {
                var firstSeen = _appFirstSeenLookup(ctx.AppId);
                if (firstSeen > 0)
                {
                    ctx = ctx with { AppFirstSeenUnixMs = firstSeen };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AlertProducer: malformed NewSessionEvent (app_id={AppId}); dropping.",
                evt.AppId);
            return;
        }

        // Per-WAN-connection entry-point log — kept at Debug because real
        // workloads fire this ~30+ times per 5s flush (Chrome alone tends
        // to dominate). Production validation 2026-06-17 confirmed the
        // producer IS being fed correctly; the three Information-level
        // lines below (raised / dedupe-hit / SQL-gate blocked) are the
        // ones worth surfacing in the day-to-day log. Re-enable this one
        // via an appsettings.json override when a future regression
        // makes the producer feed itself suspect again.
        _logger.LogDebug(
            "AlertProducer.OnSessionConnectedWan: app_id={AppId} session_id={SessionId} image={Image} sig={Sig} userPath={UserPath}",
            ctx.AppId, evt.SessionId, ctx.ImageName, ctx.SignatureStatus, ctx.IsUserWritablePath);

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
            var updated = _sink.UpdateDetail(typeStr, kindStr, refStr, newDetail);
            // Diagnostic: cache-hit branch never raises a fresh AlertRaised
            // event. If this fires post-Wipe, the producer thinks the alert
            // is still active in memory even though the row was deleted —
            // ForgetAll didn't run, or didn't run on this binary.
            _logger.LogInformation(
                "AlertProducer dedupe-hit (cache): type={Type} ref={Ref} newCount={Count} updateDetailRows={Updated}",
                typeStr, refStr, newCount, updated);
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
            // Diagnostic: SQL gate blocked. Post-Wipe this should NEVER
            // fire (alerts table is empty) — if it does, something is
            // leaving rows behind or Wipe didn't actually run.
            _logger.LogInformation(
                "AlertProducer TryInsert blocked by SQL gate (no row created): type={Type} ref={Ref}",
                typeStr, refStr);
            return;
        }

        // Diagnostic: success path — new alert row created, AlertRaised
        // about to fire. Post-Wipe + re-trigger this is the line that
        // should land in the log for a healthy re-fire.
        _logger.LogInformation(
            "AlertProducer raised: alert_id={AlertId} type={Type} ref={Ref}",
            alertId, typeStr, refStr);

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
    /// Phase 6.7 — per-flush rule evaluation hook. Called by the
    /// aggregator AFTER every <see cref="OnSessionConnectedWan"/> event
    /// for the same flush has fired. Iterates each registered
    /// <see cref="IFlushAlertRule"/>; each rule returns zero-or-more
    /// raise requests + pre-rendered detail strings. The producer
    /// applies its standard dedupe / cooldown / cache flow to each.
    /// </summary>
    public void OnFlushCompleted(FlushAlertEvent evt)
    {
        if (evt is null || _flushRules.Length == 0) return;

        foreach (var rule in _flushRules)
        {
            try
            {
                foreach (var (request, detail) in rule.Evaluate(evt))
                {
                    if (request is null || string.IsNullOrEmpty(detail))
                    {
                        continue;
                    }
                    RaiseFromFlush(rule, request, detail, evt.FlushTimeUnixMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AlertProducer: flush-rule {Rule} threw; producer continues with next rule.",
                    rule.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Per-flush sibling of <see cref="EvaluateOne"/>. Same dedupe + cooldown
    /// + cache shape, but the detail string arrives pre-rendered (per-flush
    /// rules need internal rolling-window state that
    /// <see cref="IAlertRule.RenderDetail"/> doesn't model).
    /// </summary>
    private void RaiseFromFlush(IFlushAlertRule rule, RaiseRequest req, string detail, long firstSeenUnixMs)
    {
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
            int newCount;
            lock (_gate)
            {
                state.Count++;
                newCount = state.Count;
            }
            // Detail already rendered by the rule against its own state —
            // the rule rolled in cumulative bytes, contributing PIDs, etc.
            // Producer just persists the new string.
            var updated = _sink.UpdateDetail(typeStr, kindStr, refStr, detail);
            _logger.LogInformation(
                "AlertProducer dedupe-hit (cache, flush): type={Type} ref={Ref} newCount={Count} updateDetailRows={Updated}",
                typeStr, refStr, newCount, updated);
            return;
        }

        var nowMs = _now();
        var alertId = _sink.TryInsert(
            type:          typeStr,
            severity:      req.Severity.ToString(),
            sourceMonitor: req.SourceMonitor.ToString(),
            entityKind:    kindStr,
            entityRef:     refStr,
            title:         req.Title,
            detail:        detail,
            nowUnixMs:     nowMs,
            cooldownMs:    rule.CooldownMs);

        if (alertId == 0)
        {
            _logger.LogInformation(
                "AlertProducer TryInsert blocked by SQL gate (flush): type={Type} ref={Ref}",
                typeStr, refStr);
            return;
        }

        _logger.LogInformation(
            "AlertProducer raised (flush): alert_id={AlertId} type={Type} ref={Ref}",
            alertId, typeStr, refStr);

        lock (_gate)
        {
            _active[key] = new ActiveState
            {
                Count = 1,
                FirstSeenUnixMs = firstSeenUnixMs,
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
            Detail:               detail,
            AcknowledgedAtUnixMs: null,
            AppId:                req.AppId);

        AlertRaised?.Invoke(dto);
    }

    /// <summary>
    /// Phase 6.7 QA hook for the <c>RunRollupRulesNowAsync</c> IPC.
    /// Resets the date-roll gate on every per-flush rule that
    /// implements <see cref="IResettableRollupRule"/> (currently just
    /// <see cref="UnusualDailyVolumeRule"/>), then synthesizes a flush
    /// event so the rules re-evaluate immediately. Used by the QA
    /// script that seeds 14 days of synthetic <c>traffic_daily</c>
    /// rows + a spike row for yesterday — without this hook, the
    /// rule would only fire on the next natural day-roll.
    /// </summary>
    public void EvaluateRollupRulesNow()
    {
        foreach (var rule in _flushRules)
        {
            if (rule is IResettableRollupRule resettable)
            {
                try
                {
                    resettable.ResetLastEvalDate();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "AlertProducer: ResetLastEvalDate threw for rule {Rule}; continuing.",
                        rule.GetType().Name);
                }
            }
        }

        // Empty connection list — rollup rules don't read the per-flush
        // connection slice. Wall-clock now is the flush-time anchor
        // they evaluate against.
        var syntheticEvent = new FlushAlertEvent(
            FlushTimeUnixMs: _now(),
            FlushIntervalMs: 0,
            Connections:     Array.Empty<FlushConnectionState>());

        OnFlushCompleted(syntheticEvent);
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

    /// <summary>
    /// Drops every entry from the producer's in-memory dedup cache.
    /// Called by the Settings Reset history flow on the service side so
    /// that after the alerts table has been wiped, the next qualifying
    /// observation lands as a fresh insert (raises a new push) instead of
    /// being silently absorbed as a dedupe-hit that calls UpdateDetail
    /// against a row that no longer exists. Without this, every alert
    /// type that was active at wipe time stays "tracked" in memory for
    /// the remainder of the process lifetime and Reset history quietly
    /// disables re-firing.
    /// </summary>
    public void ForgetAll()
    {
        lock (_gate)
        {
            _active.Clear();
        }

        // Phase 6.7 — propagate to stateful per-flush rules. Their
        // internal dedup HashSets ("apps/connections already alerted in
        // this process") would otherwise survive the wipe and silently
        // suppress all subsequent qualifying flushes for those keys.
        // Per-event IAlertRule implementations are stateless; only
        // IFlushAlertRule instances need this.
        foreach (var rule in _flushRules)
        {
            try
            {
                rule.ForgetAll();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AlertProducer: ForgetAll threw on rule {Rule}; continuing.",
                    rule.GetType().Name);
            }
        }
    }

    private sealed class ActiveState
    {
        public int Count;
        public long FirstSeenUnixMs;
    }
}
