using ZenVizor.Core.Attribution;
using ZenVizor.Core.Classification;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Aggregator → producer payload for one flush tick's worth of per-flush
/// alert evaluation (Phase 6.7 P4 — Rules 3 & 4 + Rule 5 date-roll gate).
/// Fires AFTER per-WAN-connection events on the same flush; the
/// producer iterates registered <see cref="IFlushAlertRule"/>s against
/// it. Cheap to build — the aggregator already has the per-connection
/// deltas and the pid → app_id snapshot.
/// </summary>
/// <param name="FlushTimeUnixMs">Wall-clock time the flush completed.</param>
/// <param name="FlushIntervalMs">Distance to the previous flush (rate denominator).</param>
/// <param name="Connections">
/// One entry per (pid, protocol, remote) row sealed in this flush.
/// Byte values are the PER-FLUSH deltas (not lifetime) — per-flush rules
/// keep their own cumulative state and add deltas in <c>Evaluate</c>.
/// </param>
public sealed record FlushAlertEvent(
    long FlushTimeUnixMs,
    long FlushIntervalMs,
    IReadOnlyList<FlushConnectionState> Connections);

/// <summary>
/// One connection's per-flush slice for use by per-flush alert rules.
/// Mirrors <see cref="PendingConnection"/> + resolves pid → app_id /
/// AppIdentity / session_id so a rule doesn't have to walk auxiliary
/// maps. <c>BytesUpDelta</c>/<c>BytesDownDelta</c> are the bytes
/// observed in THIS flush only.
/// </summary>
public sealed record FlushConnectionState(
    int Pid,
    int AppId,
    int SessionId,
    AppIdentity App,
    Protocol Protocol,
    string RemoteAddress,
    int RemotePort,
    RemoteClass RemoteClass,
    long BytesUpDelta,
    long BytesDownDelta,
    long FirstSeenUnixMs,
    long LastSeenUnixMs);
