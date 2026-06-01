using TitaniRun.Core.Storage;

namespace TitaniRun.Core.Tests.Fakes;

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
        foreach (var entry in batch.NewSessions)
        {
            newPidToSessionId[entry.Pid] = ++_nextSessionId;
        }

        var result = new FlushBatchResult(
            NewPidToSessionId: newPidToSessionId,
            SampleRowsWritten: batch.Samples.Count,
            ConnectionUpserts: batch.Connections.Count,
            SessionsClosed: batch.ClosedSessionIds.Count);
        Results.Add(result);
        return result;
    }
}
