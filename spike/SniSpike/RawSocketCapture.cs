using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ZenVizor.Core.Dns;

namespace SniSpike;

/// <summary>
/// Phase 8.5 spike — receive-only raw-socket capture via SIO_RCVALL. This is
/// the documented FALLBACK substrate (the de-risking path for the PktMon
/// control-surface unknown). It binds an IPv4 raw socket per active interface
/// and asks Windows to deliver a copy of all IPv4 packets on that interface.
/// <para>
/// INVARIANT #1: SIO_RCVALL is strictly receive-only. The socket is never
/// connected and never sends; it only mirrors packets the host was already
/// exchanging. Zero traffic originates here. (Self-monitoring lens confirms.)
/// </para>
/// <para>
/// We only care about the FIRST client-&gt;server bytes of each flow (the
/// ClientHello / HTTP request), so cost scales with new-flow rate: once a flow
/// is classified it is dropped without re-parse. Per-flow payload accumulates
/// up to a small cap to ride over TCP segmentation of a large ClientHello.
/// </para>
/// </summary>
internal static class RawSocketCapture
{
    private const int PerFlowCapBytes = 8 * 1024;
    private const int MaxTrackedFlows = 4096;

    public static int Run(int seconds)
    {
        var localIps = LocalIPv4Addresses();
        if (localIps.Count == 0)
        {
            Console.Error.WriteLine("No active IPv4 interface with a gateway found.");
            return 1;
        }

        Console.WriteLine($"raw-socket SIO_RCVALL capture for {seconds}s on:");
        foreach (var ip in localIps) Console.WriteLine($"  {ip}");
        Console.WriteLine("(IPv4 only — raw SIO_RCVALL is per-address-family; IPv6 would need a second socket.)");
        Console.WriteLine("Browse to e.g. https://example.com / https://en.wikipedia.org in Chrome (DoH on).\n");

        var store = new DnsResolutionStore();
        var flows = new ConcurrentDictionary<FlowKey, FlowState>();
        long packets = 0, candidatePackets = 0, hits = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var sockets = new List<Socket>();
        var threads = new List<Thread>();

        // Perf sampling (unknown #2). The raw-socket path has NO kernel-side
        // filter — it post-filters in user mode, so it sees EVERY packet. That
        // makes it a conservative upper bound for the PktMon design (which
        // filters + truncates in-kernel). If this stays in budget under a bulk
        // download, PktMon comfortably will too.
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long peakWs = 0;
        var perfThread = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                proc.Refresh();
                var ws = proc.WorkingSet64;
                if (ws > peakWs) peakWs = ws;
                Thread.Sleep(250);
            }
        }) { IsBackground = true, Name = "spike-perf" };
        perfThread.Start();

        foreach (var local in localIps)
        {
            Socket sock;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                sock.Bind(new IPEndPoint(local, 0));
                sock.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), BitConverter.GetBytes(1));
                sock.ReceiveBufferSize = 1 << 20;
                sock.ReceiveTimeout = 500;
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"  socket on {local} failed: {ex.SocketErrorCode} ({ex.Message}). Elevated?");
                return 1;
            }
            sockets.Add(sock);

            var t = new Thread(() =>
            {
                var buf = new byte[65535];
                while (!cts.IsCancellationRequested)
                {
                    int n;
                    try { n = sock.Receive(buf, SocketFlags.None); }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut) { continue; }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    if (n <= 0) continue;
                    Interlocked.Increment(ref packets);
                    HandlePacket(buf.AsSpan(0, n), local, store, flows,
                        ref candidatePackets, ref hits);
                }
            }) { IsBackground = true, Name = "spike-rawsock" };
            threads.Add(t);
            t.Start();
        }

        try { Task.Delay(TimeSpan.FromSeconds(seconds)).Wait(); } catch { }
        cts.Cancel();
        foreach (var s in sockets) { try { s.Dispose(); } catch { } }
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(2));
        sw.Stop();
        perfThread.Join(TimeSpan.FromSeconds(1));

        proc.Refresh();
        var cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        var cpuPct = cpuMs / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
        var pkts = Interlocked.Read(ref packets);

        Console.WriteLine($"\npackets={pkts} " +
            $"candidate(443/80 outbound)={Interlocked.Read(ref candidatePackets)} " +
            $"SNI/Host hits={Interlocked.Read(ref hits)} " +
            $"flows tracked={flows.Count} store entries={store.Count}");
        Console.WriteLine($"perf: wall={sw.Elapsed.TotalSeconds:F1}s cpu={cpuPct:F2}% " +
            $"(all-core) peakWS={peakWs / (1024 * 1024)}MB pktRate={pkts / Math.Max(1, sw.Elapsed.TotalSeconds):F0}/s");
        Console.WriteLine("NOTE: this is the UNFILTERED upper bound (sees all packets). PktMon's " +
            "in-kernel port filter + 320B truncation cuts both the packet count and per-packet copy.");
        return Interlocked.Read(ref hits) > 0 ? 0 : 3;
    }

    private static void HandlePacket(
        ReadOnlySpan<byte> pkt, IPAddress local, DnsResolutionStore store,
        ConcurrentDictionary<FlowKey, FlowState> flows,
        ref long candidatePackets, ref long hits)
    {
        // IPv4 header.
        if (pkt.Length < 20) return;
        var ihl = (pkt[0] & 0x0f) * 4;
        if ((pkt[0] >> 4) != 4 || ihl < 20 || pkt.Length < ihl) return;
        var proto = pkt[9];
        if (proto != 6 && proto != 17) return; // TCP / UDP only
        var srcIp = new IPAddress(pkt.Slice(12, 4).ToArray());
        var dstIp = new IPAddress(pkt.Slice(16, 4).ToArray());

        // Outbound only: source is one of our interface addresses. The remote
        // endpoint (store key) is the destination.
        if (!srcIp.Equals(local)) return;

        var l4 = pkt[ihl..];
        if (proto == 6)
        {
            if (l4.Length < 20) return;
            var dstPort = (l4[2] << 8) | l4[3];
            if (dstPort != 443 && dstPort != 80) return;
            var dataOff = (l4[12] >> 4) * 4;
            if (dataOff < 20 || l4.Length < dataOff) return;
            var payload = l4[dataOff..];
            if (payload.Length == 0) return;
            Interlocked.Increment(ref candidatePackets);

            var key = new FlowKey(dstIp, (ushort)dstPort, 6);
            if (!TryAccumulate(flows, key, payload, out var assembled)) return;

            var found = dstPort == 443
                ? TlsClientHelloParser.TryParse(assembled, out var host)
                : HttpHostParser.TryParse(assembled, out host);
            if (found)
            {
                store.Record(dstIp, host, 300, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                flows[key] = FlowState.Classified;
                Interlocked.Increment(ref hits);
                Console.WriteLine($"  [{(dstPort == 443 ? "TLS" : "HTTP")}] {dstIp} -> {host}");
            }
        }
        else // UDP — QUIC Initial on 443
        {
            if (l4.Length < 8) return;
            var dstPort = (l4[2] << 8) | l4[3];
            if (dstPort != 443) return;
            var payload = l4[8..]; // UDP header is 8 bytes
            if (payload.Length < 64) return; // Initials are padded to 1200; ignore tiny
            Interlocked.Increment(ref candidatePackets);

            var key = new FlowKey(dstIp, (ushort)dstPort, 17);
            var state = flows.GetOrAdd(key, _ => new FlowState());
            if (state.IsClassified) return;

            // QUIC Initials are self-contained datagrams; parse per-packet
            // rather than accumulating a stream.
            if (QuicInitialParser.TryParse(payload, out var host))
            {
                store.Record(dstIp, host, 300, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                flows[key] = FlowState.Classified;
                Interlocked.Increment(ref hits);
                Console.WriteLine($"  [QUIC] {dstIp} -> {host}");
            }
        }
    }

    private static bool TryAccumulate(
        ConcurrentDictionary<FlowKey, FlowState> flows, FlowKey key,
        ReadOnlySpan<byte> payload, out byte[] assembled)
    {
        assembled = Array.Empty<byte>();
        var state = flows.GetOrAdd(key, _ => new FlowState());
        if (state.IsClassified) return false;
        if (flows.Count > MaxTrackedFlows) return false;

        lock (state.Gate)
        {
            if (state.IsClassified) return false;
            state.Buffer.AddRange(payload);
            if (state.Buffer.Count > PerFlowCapBytes)
            {
                state.IsClassified = true; // give up on this flow
                return false;
            }
            assembled = state.Buffer.ToArray();
            return true;
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
                    result.Add(ua.Address);
            }
        }
        return result;
    }

    private readonly record struct FlowKey(IPAddress RemoteIp, ushort RemotePort, byte Proto);

    private sealed class FlowState
    {
        public static readonly FlowState Classified = new() { IsClassified = true };
        public readonly object Gate = new();
        public readonly List<byte> Buffer = new();
        public volatile bool IsClassified;
    }
}
