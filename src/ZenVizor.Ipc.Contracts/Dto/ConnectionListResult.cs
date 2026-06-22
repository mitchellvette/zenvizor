// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Endpoints an app talked to during the window. Per Phase 4 Q8 decision:
/// rows are aggregated by <c>(protocol, remote_addr, remote_port)</c> across
/// all of the app's sessions in window — one row per endpoint regardless of
/// session count. Temporal/per-session detail is not surfaced here; spike
/// shape lives in <c>traffic_samples</c> (by app) and is the alert pipeline's
/// concern.
/// </summary>
public sealed record ConnectionListResult(
    QueryWindow Window,
    IReadOnlyList<ConnectionRow> Connections);

/// <summary>
/// One endpoint row. <see cref="ResolvedHost"/> is the passively-observed
/// hostname (Phase 8) — null when the DNS observer hasn't seen a matching
/// response, when the connection's remote was a literal IP the user typed
/// directly, or when the entry has aged out of the resolver's cache. The UI
/// renders the hostname as primary text with the raw address as a subscript
/// fall-back; the raw address remains authoritative for sort + copy actions.
/// </summary>
public sealed record ConnectionRow(
    string Protocol,
    string RemoteAddress,
    int RemotePort,
    string RemoteClass,
    long BytesUp,
    long BytesDown,
    long FirstSeenUnixMs,
    long LastSeenUnixMs,
    string? ResolvedHost = null);
