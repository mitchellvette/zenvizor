// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Per-flush alert rule (Phase 6.7 — Rules 3, 4, 5). Unlike
/// <see cref="IAlertRule"/> which is pure and evaluated once per
/// qualifying WAN connection, per-flush rules are STATEFUL — they
/// maintain rolling-window or cumulative byte state that the predicate
/// reads on every flush. The producer holds the rule instance for its
/// lifetime; rule state survives across flushes.
/// <para>
/// Cooldown and dedupe still belong to the producer (one TryInsert/
/// UpdateDetail path for all rules); the rule's job is to return
/// zero-or-more <see cref="RaiseRequest"/>s per evaluation along with
/// the rendered detail string. The detail string is pre-rendered
/// because per-flush rules typically need internal state (cumulative
/// bytes, contributing PIDs, rolling-window totals) that the catalog's
/// <see cref="IAlertRule.RenderDetail"/> shape doesn't expose.
/// </para>
/// </summary>
public interface IFlushAlertRule
{
    /// <summary>Same semantic as <see cref="IAlertRule.CooldownMs"/>.</summary>
    long CooldownMs { get; }

    /// <summary>
    /// Walk the flush event, update internal state, return zero-or-more
    /// raise requests. Each tuple carries the request the producer
    /// passes to <see cref="IAlertSink.TryInsert"/> + the pre-rendered
    /// detail string the rule synthesized from its internal state.
    /// </summary>
    IEnumerable<(RaiseRequest Request, string Detail)> Evaluate(FlushAlertEvent evt);

    /// <summary>
    /// Called by <see cref="AlertProducer.ForgetAll"/> when the alerts
    /// table is wiped (Settings → Reset history). Stateful per-flush
    /// rules MUST drop their dedup HashSets (which track "already
    /// alerted for this app/connection in this process") so the next
    /// qualifying observation re-raises a fresh alert instead of
    /// silently no-op-ing. Rule cumulative state (rolling-window
    /// buckets, per-connection accumulators) should also clear: a
    /// "reset history" user expects a clean slate, not in-flight
    /// counters surviving from before the wipe.
    /// <para>
    /// Default implementation is a no-op for stateless rules (none
    /// today; every Phase 6.7 per-flush rule overrides).
    /// </para>
    /// </summary>
    void ForgetAll() { }
}
