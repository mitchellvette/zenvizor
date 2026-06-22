// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace ZenVizor.Core.Observations;

/// <summary>
/// A single capture-level observation: "at time T, PID P sent/received N bytes
/// over protocol X with local endpoint L and remote endpoint R."
/// </summary>
/// <param name="TimestampUnixMs">When the underlying ETW (or synthetic) event occurred.</param>
/// <param name="Pid">
/// PID as reported by the capture source. May be wrong or null on the receive
/// path (ETW kernel-network can fire in DPC context); the attribution layer
/// corrects it from the IP Helper table.
/// </param>
/// <param name="Protocol">TCP or UDP.</param>
/// <param name="LocalEndpoint">Local IP + port for this flow.</param>
/// <param name="RemoteEndpoint">Remote IP + port for this flow.</param>
/// <param name="Direction">Up (sent) or Down (received).</param>
/// <param name="Bytes">Byte count for this single event.</param>
public sealed record NetworkObservation(
    long TimestampUnixMs,
    int? Pid,
    Protocol Protocol,
    IPEndPoint LocalEndpoint,
    IPEndPoint RemoteEndpoint,
    Direction Direction,
    long Bytes);
