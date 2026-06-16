using ZenVizor.Core.Storage;

namespace ZenVizor.Core.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IFlushSink"/>. Records every batch and assigns
/// monotonically increasing session ids on Flush.
/// </summary>
internal sealed class FakeFlushSink : IFlushSink
{
    private int _nextSessionId;

    public List<FlushBatch> Batches { get; } = new();
    public List<FlushBatchResult> Results { get; } = new();

    public IReadOnlyCollection<PendingTrafficSample> AllSamples =>
        Batches.SelectMany(b => b.Samples).ToList();

    public IReadOnlyCollection<PendingConnection> AllConnections =>
        Batches.SelectMany(b => b.Connections).ToList();

    public IReadOnlyCollection<NewSessionEntry> AllNewSessions =>
        Batches.SelectMany(b => b.NewSessions).ToList();

    public IReadOnlyCollection<int> AllClosedSessionIds =>
        Batches.SelectMany(b => b.ClosedSessionIds).ToList();

    public FlushBatchResult Flush(FlushBatch batch)
    {
        Batches.Add(batch);

        var newPidToSessionId = new Dictionary<int, int>();
        var newSessionIdToAppId = new Dictionary<int, int>();
        foreach (var entry in batch.NewSessions)
        {
            var sessionId = ++_nextSessionId;
            newPidToSessionId[entry.Pid] = sessionId;
            // Synthesize a 1:1 session→app mapping so the alert producer
            // hook on TrafficAggregator (Phase 6.1) can resolve app ids in
            // tests that drive Flush via this fake. Production wiring
            // ultimately gets app_id from SqliteFlushSink's apps-table
            // upsert; this fake just hands back a stable derived value.
            newSessionIdToAppId[sessionId] = sessionId;
        }

        var result = new FlushBatchResult(
            NewPidToSessionId:   newPidToSessionId,
            NewSessionIdToAppId: newSessionIdToAppId,
            SampleRowsWritten: batch.Samples.Count,
            ConnectionUpserts: batch.Connections.Count,
            SessionsClosed: batch.ClosedSessionIds.Count);
        Results.Add(result);
        return result;
    }
}
