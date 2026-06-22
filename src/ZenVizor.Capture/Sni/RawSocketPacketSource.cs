// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZenVizor.Capture.Sni;

/// <summary>
/// Phase 8.6 — receive-only raw-socket packet source via <c>SIO_RCVALL</c>. The
/// documented FALLBACK substrate: it needs no PktMon control surface, so it is
/// the de-risking path for the Phase 8.5 §7 unknown. Binds one IPv4 raw socket
/// per active interface and asks Windows to mirror a copy of every IPv4 packet
/// on that interface; the <see cref="SniPacketProcessor"/>'s destination-port
/// filter selects the client→server packets that carry the ClientHello.
/// <para>
/// INVARIANT #1: <c>SIO_RCVALL</c> is strictly receive. The socket is never
/// connected and never sends — it only mirrors packets the host was already
/// exchanging. Zero traffic originates here. The self-monitoring lens confirms
/// (Phase 8.5 §5).
/// </para>
/// <para>
/// Limitation: raw <c>SIO_RCVALL</c> is per-address-family. This covers IPv4
/// only; IPv6 flows are not seen on this substrate. PktMon (primary) carries
/// IPv6. This is acceptable for a fallback whose job is to keep the feature
/// working when the PktMon control surface is unavailable.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RawSocketPacketSource : IRawPacketSource
{
    private const int ReceiveBufferBytes = 1 << 20;
    private const int ReceiveTimeoutMs = 500;

    private readonly ILogger _logger;
    private readonly List<Socket> _sockets = new();
    private readonly List<Thread> _threads = new();
    private CancellationTokenSource? _cts;
    private volatile bool _faulted;

    public RawSocketPacketSource(ILogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    public bool IsFaulted => _faulted;

    public void Start(Action<ReadOnlyMemory<byte>> onIpPacket)
    {
        ArgumentNullException.ThrowIfNull(onIpPacket);

        var locals = LocalIPv4Addresses();
        if (locals.Count == 0)
        {
            throw new InvalidOperationException(
                "No active IPv4 interface with a gateway found for SIO_RCVALL capture.");
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        foreach (var local in locals)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            sock.Bind(new IPEndPoint(local, 0));
            sock.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), BitConverter.GetBytes(1));
            sock.ReceiveBufferSize = ReceiveBufferBytes;
            sock.ReceiveTimeout = ReceiveTimeoutMs;
            _sockets.Add(sock);

            var thread = new Thread(() => ReceiveLoop(sock, onIpPacket, token))
            {
                IsBackground = true,
                Name = "ZenVizor.SniCapture.RawSock",
            };
            _threads.Add(thread);
            thread.Start();
        }

        _logger.LogInformation(
            "SNI raw-socket capture started on {Count} IPv4 interface(s).", locals.Count);
    }

    private void ReceiveLoop(Socket sock, Action<ReadOnlyMemory<byte>> onIpPacket, CancellationToken token)
    {
        var buffer = new byte[ushort.MaxValue];
        while (!token.IsCancellationRequested)
        {
            int n;
            try
            {
                n = sock.Receive(buffer, SocketFlags.None);
            }
            catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (SocketException) when (!token.IsCancellationRequested)
            {
                _faulted = true;
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (n <= 0) continue;
            try
            {
                onIpPacket(buffer.AsMemory(0, n));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SNI raw-socket packet handler threw (ignored).");
            }
        }
    }

    private static List<IPAddress> LocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any));
            if (!hasGateway) continue;

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    result.Add(ua.Address);
                }
            }
        }
        return result;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* best-effort */ }
        foreach (var s in _sockets) { try { s.Dispose(); } catch { /* best-effort */ } }
        foreach (var t in _threads) { try { t.Join(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ } }
        _sockets.Clear();
        _threads.Clear();
        try { _cts?.Dispose(); } catch { /* best-effort */ }
        _cts = null;
    }
}
