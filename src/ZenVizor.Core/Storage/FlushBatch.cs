using ZenVizor.Core.Observations;

namespace ZenVizor.Core.Storage;

/// <summary>
/// The full payload of one flush tick. Built by the aggregator, consumed by
/// <see cref="IFlushSink"/> in a single transaction. Each PID-keyed item is
/// resolved to a real <c>session_id</c> by the sink:
/// new sessions go through INSERT; already-persisted PIDs come from
/// <see cref="KnownPidToSessionId"/>.
/// </summary>
public sealed record FlushBatch(
    IReadOnlyList<NewSessionEntry> NewSessions,
    IReadOnlyDictionary<int, int> KnownPidToSessionId,
    IReadOnlyList<PendingTrafficSample> Samples,
    IReadOnlyList<PendingConnection> Connections,
    IReadOnlyList<int> ClosedSessionIds,
    long FlushTimeUnixMs);

public sealed record NewSessionEntry(
    int Pid,
    AppIdentity App,
    long StartTimeUnixMs,
    string? HostedServices);

public sealed record PendingTrafficSample(
    int Pid,
    long BucketStartUnixMs,
    long BytesUp,
    long BytesDown,
    RemoteClass RemoteClass);

public sealed record PendingConnection(
    int Pid,
    Protocol Protocol,
    string RemoteAddress,
    int RemotePort,
    RemoteClass RemoteClass,
    long BytesUpDelta,
    long BytesDownDelta,
    long FirstSeenUnixMs,
    long LastSeenUnixMs,
    string? ResolvedHost = null);

/// <summary>
/// Returned by a successful <see cref="IFlushSink.Flush"/> call. Tells the
/// aggregator/tracker which PIDs are now persisted at which session ids so
/// future flushes can use <see cref="FlushBatch.KnownPidToSessionId"/> directly.
/// <para>
/// <see cref="NewSessionIdToAppId"/> exposes the session_id → app_id pairs
/// the sink resolved during this flush — used by the aggregator to maintain
/// a long-running pid → app_id mapping for the alert producer (which needs
/// app_id at WAN-connection-event time but is fed pid-keyed observations).
/// </para>
/// </summary>
public sealed record FlushBatchResult(
    IReadOnlyDictionary<int, int> NewPidToSessionId,
    IReadOnlyDictionary<int, int> NewSessionIdToAppId,
    int SampleRowsWritten,
    int ConnectionUpserts,
    int SessionsClosed);
