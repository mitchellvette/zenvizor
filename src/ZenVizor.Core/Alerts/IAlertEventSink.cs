namespace ZenVizor.Core.Alerts;

/// <summary>
/// Aggregator-side hook the producer implements. Called by
/// <c>TrafficAggregator.Flush</c> after the sink commits, once per qualifying
/// WAN connection in the flushed batch. The aggregator owns event creation —
/// it knows pid → app_id and pid → AppIdentity from its long-running cache
/// (rebuilt each flush from <c>FlushBatchResult.SessionIdToAppId</c>).
/// <para>
/// Implementations MUST be thread-safe: the aggregator's Flush runs on the
/// flush timer thread, and the producer's <see cref="AlertProducer.AlertRaised"/>
/// fan-out runs synchronously on the same thread before <c>Flush</c> returns.
/// </para>
/// </summary>
public interface IAlertEventSink
{
    /// <summary>
    /// Called once per qualifying WAN connection observed in the just-committed
    /// flush. Implementation runs every rule against the event's context;
    /// per-rule TryInsert / UpdateDetail are the producer's responsibility.
    /// Must not throw; implementation logs and swallows internal failures so
    /// a single rule fault can't fail the entire flush commit path.
    /// </summary>
    void OnSessionConnectedWan(NewSessionEvent evt);
}
