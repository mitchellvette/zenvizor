using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Classification;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Aggregation;

/// <summary>
/// The in-memory rolling aggregate. <see cref="Observe"/> performs ZERO disk
/// I/O — it just updates PID-keyed dictionaries under a lock. <see cref="Flush"/>
/// drains the dictionaries, builds a <see cref="FlushBatch"/>, and hands it to
/// the <see cref="IFlushSink"/> which writes everything in one transaction.
/// </summary>
public sealed class TrafficAggregator
{
    private readonly object _gate = new();
    private readonly SessionTracker _sessions;
    private readonly PidCorrector _corrector;
    private readonly IPidTableSnapshotSource _snapshotSource;
    private readonly IFlushSink _sink;
    private readonly int _bucketSeconds;
    private readonly ILogger _logger;
    private readonly Func<long> _nowProvider;
    private readonly RollingActivityWindow _activityWindow = new();

    // Live accumulators keyed by PID. Swapped on flush.
    private Dictionary<SampleKey, SampleAcc> _samples = new();
    private Dictionary<ConnectionKey, ConnectionAcc> _connections = new();

    // When the current partial accumulator started filling. Used as the start
    // timestamp for the bucket sealed by the next Flush(); the snapshot rate
    // denominator is (now − this).
    private long _partialBucketStartUnixMs;

    // Observation counters live outside _gate so the untrackable-PID path
    // (an unattributed event) and the trackable path can both increment them
    // without contending on the accumulator lock.
    private long _observationsSeen;
    private long _observationsUnattributed;

    public TrafficAggregator(
        SessionTracker sessions,
        PidCorrector corrector,
        IPidTableSnapshotSource snapshotSource,
        IFlushSink sink,
        int bucketSeconds = BucketAligner.DefaultBucketSeconds,
        ILogger<TrafficAggregator>? logger = null,
        Func<long>? nowProvider = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _corrector = corrector ?? throw new ArgumentNullException(nameof(corrector));
        _snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

        if (bucketSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSeconds), "Bucket width must be positive.");
        }
        _bucketSeconds = bucketSeconds;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _partialBucketStartUnixMs = _nowProvider();
    }

    /// <summary>Total observations seen since process start.</summary>
    public long ObservationsSeen => Interlocked.Read(ref _observationsSeen);

    /// <summary>Observations dropped because no PID could be attributed.</summary>
    public long ObservationsUnattributed => Interlocked.Read(ref _observationsUnattributed);

    /// <summary>
    /// Snapshot of both counters. Each is read atomically; the pair is NOT
    /// atomic (Unattributed can advance between the two reads), which is
    /// fine for an observability surface.
    /// </summary>
    public (long Seen, long Unattributed) SnapshotObservationCounters() =>
        (Interlocked.Read(ref _observationsSeen),
         Interlocked.Read(ref _observationsUnattributed));

    public void Observe(NetworkObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        Interlocked.Increment(ref _observationsSeen);

        var snapshot = _snapshotSource.CurrentSnapshot;
        var pid = _corrector.Correct(observation, snapshot);

        // SessionTracker.TryTrack handles its own locking. Session-open
        // enrichment (WinVerifyTrust + FileInfo + SCM enumeration) runs
        // inside TryTrack but OUTSIDE this aggregator's _gate, so per-event
        // accumulator writes never block on signature verification or file
        // I/O.
        bool tracked = pid is int p &&
                       _sessions.TryTrack(p, observation.TimestampUnixMs);

        if (!tracked)
        {
            Interlocked.Increment(ref _observationsUnattributed);
            return;
        }

        var correctedPid = (int)pid!;

        // Pure computations stay outside _gate: classification and bucket
        // alignment touch no shared mutable state.
        var remoteClass = RemoteAddressClassifier.Classify(observation.RemoteEndpoint.Address);
        var bucketStart = BucketAligner.AlignToBucket(observation.TimestampUnixMs, _bucketSeconds);

        lock (_gate)
        {
            var sampleKey = new SampleKey(correctedPid, bucketStart, remoteClass);
            if (!_samples.TryGetValue(sampleKey, out var sampleAcc))
            {
                sampleAcc = new SampleAcc();
                _samples[sampleKey] = sampleAcc;
            }
            sampleAcc.Add(observation.Direction, observation.Bytes);

            var connectionKey = new ConnectionKey(
                correctedPid, observation.Protocol,
                observation.RemoteEndpoint.Address,
                observation.RemoteEndpoint.Port);
            if (!_connections.TryGetValue(connectionKey, out var connAcc))
            {
                connAcc = new ConnectionAcc(remoteClass, observation.TimestampUnixMs);
                _connections[connectionKey] = connAcc;
            }
            connAcc.Add(observation.Direction, observation.Bytes, observation.TimestampUnixMs);
        }
    }

    /// <summary>
    /// Atomically swap the live accumulators with fresh ones, then hand a
    /// single <see cref="FlushBatch"/> to the sink. On success, commit the
    /// SessionTracker and advance the activity window; on sink failure, the
    /// tracker stays unchanged AND the swapped-out byte accumulators get
    /// merged back into the live dictionaries so the next tick retries with
    /// the data intact (instead of silently dropping it on the floor).
    /// </summary>
    public FlushSummary Flush(long nowUnixMs)
    {
        Dictionary<SampleKey, SampleAcc> samplesSnapshot;
        Dictionary<ConnectionKey, ConnectionAcc> connectionsSnapshot;
        IReadOnlyList<NewSessionEntry> newSessions;
        IReadOnlyDictionary<int, int> knownPidToSessionId;
        IReadOnlyList<int> closedSessionIds;
        IReadOnlyDictionary<int, SessionTracker.PidAppInfo> pidToAppSnapshot;
        long bucketStartUnixMs;

        lock (_gate)
        {
            samplesSnapshot = _samples;
            connectionsSnapshot = _connections;
            _samples = new Dictionary<SampleKey, SampleAcc>();
            _connections = new Dictionary<ConnectionKey, ConnectionAcc>();

            newSessions = _sessions.CollectPendingOpens();
            knownPidToSessionId = _sessions.SnapshotPersistedSessions();
            closedSessionIds = _sessions.CollectStaleSessionIds(nowUnixMs);
            pidToAppSnapshot = _sessions.SnapshotPidToApp();

            bucketStartUnixMs = _partialBucketStartUnixMs;
            _partialBucketStartUnixMs = nowUnixMs;
        }

        var sampleRows = new List<PendingTrafficSample>(samplesSnapshot.Count);
        foreach (var (key, acc) in samplesSnapshot)
        {
            sampleRows.Add(new PendingTrafficSample(
                Pid: key.Pid,
                BucketStartUnixMs: key.BucketStartUnixMs,
                BytesUp: acc.BytesUp,
                BytesDown: acc.BytesDown,
                RemoteClass: key.RemoteClass));
        }

        var connectionRows = new List<PendingConnection>(connectionsSnapshot.Count);
        foreach (var (key, acc) in connectionsSnapshot)
        {
            // ToString runs once per unique connection here at flush time, not
            // once per event in the hot path.
            connectionRows.Add(new PendingConnection(
                Pid: key.Pid,
                Protocol: key.Protocol,
                RemoteAddress: key.RemoteAddress.ToString(),
                RemotePort: key.RemotePort,
                RemoteClass: acc.RemoteClass,
                BytesUpDelta: acc.BytesUp,
                BytesDownDelta: acc.BytesDown,
                FirstSeenUnixMs: acc.FirstSeenUnixMs,
                LastSeenUnixMs: acc.LastSeenUnixMs));
        }

        var batch = new FlushBatch(
            NewSessions: newSessions,
            KnownPidToSessionId: knownPidToSessionId,
            Samples: sampleRows,
            Connections: connectionRows,
            ClosedSessionIds: closedSessionIds,
            FlushTimeUnixMs: nowUnixMs);

        FlushBatchResult result;
        try
        {
            result = _sink.Flush(batch);
        }
        catch
        {
            // Sink failed: merge the swapped-out accumulators back into the
            // live ones so the next Flush() retries them. SessionTracker
            // stays untouched because we never called OnFlushCommitted, and
            // the activity-window advance happens AFTER the sink call below
            // for the same retry-safety reason.
            lock (_gate)
            {
                MergeSnapshotBack(samplesSnapshot, connectionsSnapshot, bucketStartUnixMs);
            }
            throw;
        }

        lock (_gate)
        {
            _sessions.OnFlushCommitted(result.NewPidToSessionId, closedSessionIds);
            var rollup = BuildPerAppRollup(samplesSnapshot, pidToAppSnapshot);
            _activityWindow.OnFlush(rollup.Apps, rollup.Breakdown, bucketStartUnixMs, nowUnixMs);
        }

        _logger.LogDebug(
            "Flush committed: samples={S} connections={C} newSessions={N} closed={X}.",
            result.SampleRowsWritten, result.ConnectionUpserts,
            result.NewPidToSessionId.Count, result.SessionsClosed);

        return new FlushSummary(
            SampleRowsWritten: result.SampleRowsWritten,
            ConnectionUpserts: result.ConnectionUpserts,
            SessionsClosed: result.SessionsClosed);
    }

    /// <summary>
    /// Caller MUST hold <see cref="_gate"/>. Folds the swapped-out
    /// accumulators back into the live dictionaries — keys that re-appeared
    /// during the failed flush window get their bytes summed; keys that
    /// didn't get reinserted as-is. Also restores the partial-bucket start
    /// timestamp so the next snapshot's window span is consistent.
    /// </summary>
    private void MergeSnapshotBack(
        Dictionary<SampleKey, SampleAcc> samplesSnapshot,
        Dictionary<ConnectionKey, ConnectionAcc> connectionsSnapshot,
        long bucketStartUnixMs)
    {
        foreach (var (key, acc) in samplesSnapshot)
        {
            if (_samples.TryGetValue(key, out var live))
            {
                live.Merge(acc);
            }
            else
            {
                _samples[key] = acc;
            }
        }

        foreach (var (key, acc) in connectionsSnapshot)
        {
            if (_connections.TryGetValue(key, out var live))
            {
                live.Merge(acc);
            }
            else
            {
                _connections[key] = acc;
            }
        }

        _partialBucketStartUnixMs = bucketStartUnixMs;
    }

    /// <summary>
    /// Returns the current rolling-window activity snapshot, served entirely
    /// from in-memory state. MUST NOT perform any SQLite I/O — that invariant
    /// is enforced by the Phase-3 integration guard (mirrors the "Observe must
    /// not write to disk" check from Phase 1).
    /// </summary>
    public ActivitySnapshot TakeActivitySnapshot()
    {
        var nowUnixMs = _nowProvider();
        lock (_gate)
        {
            var pidToApp = _sessions.SnapshotPidToApp();
            var partial = BuildPerAppRollup(_samples, pidToApp);
            return _activityWindow.TakeSnapshot(partial.Apps, partial.Breakdown, nowUnixMs);
        }
    }

    /// <summary>
    /// Per-app byte rollup keyed by (AppIdentity, HostedServices) PLUS the
    /// aggregate WAN/Local byte breakdown over the same input samples.
    /// Both are computed in one pass because the source dictionary keys
    /// (<see cref="SampleKey"/>) already carry <see cref="RemoteClass"/>.
    /// Multiple PIDs of the same app collapse; distinct svchost PIDs
    /// hosting different service sets stay separate (CLAUDE.md invariant
    /// #5). Skips samples whose PID is no longer in the tracker (rare;
    /// happens if a session was reaped between the Observe that recorded
    /// the sample and this rollup).
    /// </summary>
    private static PerAppRollup BuildPerAppRollup(
        IReadOnlyDictionary<SampleKey, SampleAcc> samples,
        IReadOnlyDictionary<int, SessionTracker.PidAppInfo> pidToApp)
    {
        var apps = new Dictionary<ActivityKey, ActivityBytes>(pidToApp.Count);
        long wanUp = 0, wanDown = 0, localUp = 0, localDown = 0;

        foreach (var (sampleKey, acc) in samples)
        {
            if (!pidToApp.TryGetValue(sampleKey.Pid, out var info))
            {
                continue;
            }

            var key = new ActivityKey(info.AppIdentity, info.HostedServices);
            if (apps.TryGetValue(key, out var existing))
            {
                apps[key] = new ActivityBytes(
                    existing.BytesUp + acc.BytesUp,
                    existing.BytesDown + acc.BytesDown);
            }
            else
            {
                apps[key] = new ActivityBytes(acc.BytesUp, acc.BytesDown);
            }

            if (sampleKey.RemoteClass == RemoteClass.Wan)
            {
                wanUp += acc.BytesUp;
                wanDown += acc.BytesDown;
            }
            else
            {
                localUp += acc.BytesUp;
                localDown += acc.BytesDown;
            }
        }

        return new PerAppRollup(
            Apps: apps,
            Breakdown: new ClassBreakdown(wanUp, wanDown, localUp, localDown));
    }

    private readonly record struct PerAppRollup(
        Dictionary<ActivityKey, ActivityBytes> Apps,
        ClassBreakdown Breakdown);

    public sealed record FlushSummary(int SampleRowsWritten, int ConnectionUpserts, int SessionsClosed);

    private readonly record struct SampleKey(int Pid, long BucketStartUnixMs, RemoteClass RemoteClass);

    // RemoteAddress is held as IPAddress (not string) so the per-event hot path
    // does NOT call IPAddress.ToString() to build the dictionary key. ToString
    // happens exactly once per unique connection at flush time, where
    // PendingConnection expects a string for the SQLite text column. IPAddress
    // overrides Equals/GetHashCode so record-struct field equality is correct.
    private readonly record struct ConnectionKey(int Pid, Protocol Protocol, IPAddress RemoteAddress, int RemotePort);

    private sealed class SampleAcc
    {
        public long BytesUp;
        public long BytesDown;

        public void Add(Direction direction, long bytes)
        {
            if (direction == Direction.Up) BytesUp += bytes;
            else BytesDown += bytes;
        }

        public void Merge(SampleAcc other)
        {
            BytesUp += other.BytesUp;
            BytesDown += other.BytesDown;
        }
    }

    private sealed class ConnectionAcc
    {
        public ConnectionAcc(RemoteClass remoteClass, long firstSeenUnixMs)
        {
            RemoteClass = remoteClass;
            FirstSeenUnixMs = firstSeenUnixMs;
            LastSeenUnixMs = firstSeenUnixMs;
        }

        public RemoteClass RemoteClass { get; }
        public long BytesUp;
        public long BytesDown;
        public long FirstSeenUnixMs;
        public long LastSeenUnixMs;

        public void Add(Direction direction, long bytes, long timestampUnixMs)
        {
            if (direction == Direction.Up) BytesUp += bytes;
            else BytesDown += bytes;
            if (timestampUnixMs > LastSeenUnixMs) LastSeenUnixMs = timestampUnixMs;
        }

        public void Merge(ConnectionAcc other)
        {
            BytesUp += other.BytesUp;
            BytesDown += other.BytesDown;
            if (other.FirstSeenUnixMs < FirstSeenUnixMs) FirstSeenUnixMs = other.FirstSeenUnixMs;
            if (other.LastSeenUnixMs > LastSeenUnixMs) LastSeenUnixMs = other.LastSeenUnixMs;
        }
    }
}
