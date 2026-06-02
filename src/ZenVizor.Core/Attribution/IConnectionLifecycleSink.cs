using System.Net;
using ZenVizor.Core.Observations;

namespace ZenVizor.Core.Attribution;

/// <summary>
/// Consumer of kernel-emitted TCP connection lifecycle events. Used so the
/// capture layer can build an authoritative (local-endpoint → PID) map from
/// events that fire while the owning process is alive — fixing the race
/// where polled <c>GetExtendedTcpTable</c> snapshots miss sub-second
/// connections (a fast curl downloads 50 MB and exits before any poll
/// captures its connection, then receive-path attribution drops to 0).
/// </summary>
public interface IConnectionLifecycleSink
{
    /// <summary>
    /// A connection has been created (active connect from the owning process,
    /// or an accepted inbound). <paramref name="pid"/> is authoritative: ETW
    /// fires this event synchronously with the syscall, so the process is
    /// always alive at this moment.
    /// </summary>
    void OnConnect(
        Protocol protocol,
        IPEndPoint localEndpoint,
        IPEndPoint remoteEndpoint,
        int pid,
        long timestampUnixMs);

    /// <summary>
    /// A connection has closed. The mapping should be retained for a grace
    /// window so trailing receive-side ETW events (delivered after the
    /// disconnect due to buffer flush latency) still resolve.
    /// </summary>
    void OnDisconnect(
        Protocol protocol,
        IPEndPoint localEndpoint,
        long timestampUnixMs);
}
