using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;

namespace ZenVizor.Capture;

/// <summary>
/// Real-time ETW capture source over the kernel network provider. Subscribes
/// to TcpIp/UdpIp Send/Recv events (v4 and v6) and maps them to
/// <see cref="NetworkObservation"/>s. Requires LocalSystem/Admin.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EtwCaptureSource : ICaptureSource, IAsyncDisposable
{
    /// <summary>Default session name used by ZenVizor. Stable across restarts.</summary>
    public const string DefaultSessionName = "ZenVizor.Capture";

    /// <summary>
    /// Bounded channel capacity. At a sustained 10 k events/sec — well past
    /// typical desktop load — 65 536 slots buffer ~6 s of backlog, which is
    /// enough to ride out a flush hiccup without the channel itself becoming
    /// a memory pressure source. Sustained overrun drops oldest events; the
    /// drop count is exposed via <see cref="DroppedObservations"/>.
    /// </summary>
    public const int ChannelCapacity = 65_536;

    private readonly string _sessionName;
    private readonly ILogger _logger;
    private readonly IProcessLifecycleSink? _processSink;
    private readonly IConnectionLifecycleSink? _connectionSink;
    private readonly Channel<NetworkObservation> _channel =
        Channel.CreateBounded<NetworkObservation>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private TraceEventSession? _session;
    private Thread? _processThread;
    private volatile bool _shutdownRequested;
    private volatile bool _isFaulted;
    private long _droppedObservations;

    public EtwCaptureSource(
        string? sessionName = null,
        ILogger<EtwCaptureSource>? logger = null,
        IProcessLifecycleSink? processSink = null,
        IConnectionLifecycleSink? connectionSink = null)
    {
        _sessionName = sessionName ?? DefaultSessionName;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _processSink = processSink;
        _connectionSink = connectionSink;
    }

    /// <summary>
    /// Start the ETW real-time session and begin emitting observations.
    /// Idempotent — calling twice is a no-op. Defensively stops any leaked
    /// pre-existing session of the same name.
    /// </summary>
    public void Start()
    {
        if (_session is not null)
        {
            return;
        }

        // Defensive cleanup — a prior unclean shutdown can leave a session running.
        TryStopLeakedSession(_sessionName, _logger);

        _shutdownRequested = false;
        _isFaulted = false;
        _session = new TraceEventSession(_sessionName)
        {
            StopOnDispose = true,
        };

        // Subscribe to Process keywords ONLY when a sink is wired. The Process
        // keyword gives us ProcessStart/Stop events used by the lifecycle
        // resolver to populate its image cache BEFORE the first network event
        // for that PID arrives — without this, short-lived processes (sub-1 s
        // curl, single-shot CLI tools) silently lose all attribution because
        // their image can't be resolved post-exit.
        var keywords = KernelTraceEventParser.Keywords.NetworkTCPIP;
        if (_processSink is not null)
        {
            keywords |= KernelTraceEventParser.Keywords.Process;
        }
        _session.EnableKernelProvider(keywords);

        var kernel = _session.Source.Kernel;
        kernel.TcpIpRecv     += OnTcpRecv;
        kernel.TcpIpSend     += OnTcpSend;
        kernel.TcpIpRecvIPV6 += OnTcp6Recv;
        kernel.TcpIpSendIPV6 += OnTcp6Send;
        kernel.UdpIpRecv     += OnUdpRecv;
        kernel.UdpIpSend     += OnUdpSend;
        kernel.UdpIpRecvIPV6 += OnUdp6Recv;
        kernel.UdpIpSendIPV6 += OnUdp6Send;

        if (_processSink is not null)
        {
            kernel.ProcessStart += OnProcessStart;
            kernel.ProcessStop  += OnProcessStop;
        }

        // TCP connection lifecycle events. These are part of NetworkTCPIP, so
        // no additional kernel keyword is needed. Connect/Accept give us
        // (local, remote, PID) at the moment of connection creation, BEFORE
        // any send/recv arrives — which is exactly what the polled
        // GetExtendedTcpTable misses for short-lived connections.
        if (_connectionSink is not null)
        {
            kernel.TcpIpConnect       += OnTcpConnect;
            kernel.TcpIpConnectIPV6   += OnTcp6Connect;
            kernel.TcpIpAccept        += OnTcpAccept;
            kernel.TcpIpAcceptIPV6    += OnTcp6Accept;
            kernel.TcpIpDisconnect    += OnTcpDisconnect;
            kernel.TcpIpDisconnectIPV6 += OnTcp6Disconnect;
        }

        _processThread = new Thread(ProcessLoop)
        {
            IsBackground = true,
            Name = "ZenVizor.EtwCapture",
        };
        _processThread.Start();

        _logger.LogInformation("ETW capture session '{Session}' started.", _sessionName);
    }

    public async IAsyncEnumerable<NetworkObservation> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var observation))
            {
                yield return observation;
            }
        }
    }

    private void ProcessLoop()
    {
        try
        {
            // Blocks until session is disposed.
            _session?.Source.Process();

            // Process() returning without us asking for shutdown means the
            // session died on its own (kernel logger evicted, ETW backpressure,
            // etc.). Surface as faulted so CaptureMonitor stops reporting
            // CaptureActive=true.
            if (!_shutdownRequested)
            {
                _isFaulted = true;
                _logger.LogError(
                    "ETW Process loop exited unexpectedly without a shutdown request — " +
                    "capture is now dead.");
            }
        }
        catch (Exception ex)
        {
            _isFaulted = true;
            _logger.LogError(ex, "ETW Process loop terminated unexpectedly.");
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public bool IsFaulted => _isFaulted;

    private static void TryStopLeakedSession(string sessionName, ILogger logger)
    {
        try
        {
            using var leak = TraceEventSession.GetActiveSession(sessionName);
            if (leak is not null)
            {
                logger.LogWarning(
                    "Found pre-existing ETW session '{Session}' from a prior run; stopping it.",
                    sessionName);
                leak.Stop();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Defensive ETW session cleanup failed (non-fatal).");
        }
    }

    // ---- event handlers ----

    private void OnTcpRecv(TcpIpTraceData d) => Emit(d, Protocol.Tcp, Direction.Down);
    private void OnTcpSend(TcpIpSendTraceData d) => Emit(d, Protocol.Tcp, Direction.Up);
    private void OnTcp6Recv(TcpIpV6TraceData d) => Emit(d, Protocol.Tcp, Direction.Down);
    private void OnTcp6Send(TcpIpV6SendTraceData d) => Emit(d, Protocol.Tcp, Direction.Up);
    private void OnUdpRecv(UdpIpTraceData d) => Emit(d, Protocol.Udp, Direction.Down);
    private void OnUdpSend(UdpIpTraceData d) => Emit(d, Protocol.Udp, Direction.Up);
    private void OnUdp6Recv(UpdIpV6TraceData d) => Emit(d, Protocol.Udp, Direction.Down);
    private void OnUdp6Send(UpdIpV6TraceData d) => Emit(d, Protocol.Udp, Direction.Up);

    private void Emit(TcpIpTraceData d, Protocol p, Direction dir) =>
        WriteObservation(d.TimeStamp, NullablePid(d.ProcessID), p,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            dir, d.size);

    private void Emit(TcpIpSendTraceData d, Protocol p, Direction dir) =>
        WriteObservation(d.TimeStamp, NullablePid(d.ProcessID), p,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            dir, d.size);

    private void Emit(TcpIpV6TraceData d, Protocol p, Direction dir) =>
        WriteObservation(d.TimeStamp, NullablePid(d.ProcessID), p,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            dir, d.size);

    private void Emit(TcpIpV6SendTraceData d, Protocol p, Direction dir) =>
        WriteObservation(d.TimeStamp, NullablePid(d.ProcessID), p,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            dir, d.size);

    private void Emit(UdpIpTraceData d, Protocol p, Direction dir) =>
        WriteObservation(d.TimeStamp, NullablePid(d.ProcessID), p,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            dir, d.size);

    private void Emit(UpdIpV6TraceData d, Protocol p, Direction dir) =>
        WriteObservation(d.TimeStamp, NullablePid(d.ProcessID), p,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            dir, d.size);

    private void WriteObservation(
        DateTime timestamp, int? pid, Protocol protocol,
        IPEndPoint local, IPEndPoint remote, Direction direction, long bytes)
    {
        var ts = ToUnixTimeMs(timestamp);
        var observation = new NetworkObservation(ts, pid, protocol, local, remote, direction, bytes);

        // DropOldest mode: when the bounded channel is full, TryWrite still
        // returns true after silently discarding the oldest entry. To surface
        // that load signal, check Reader.Count first — if it's at capacity,
        // this write will displace one. Best-effort under concurrency (the
        // reader runs on another thread, so the count can fall between check
        // and write), but ETW Process loop is single-threaded, so we never
        // double-count a drop from the writer side.
        if (_channel.Reader.Count >= ChannelCapacity)
        {
            Interlocked.Increment(ref _droppedObservations);
        }

        _channel.Writer.TryWrite(observation);
    }

    /// <summary>
    /// Approximate count of observations the bounded channel discarded
    /// (oldest-first) since startup. Surfaces sustained back-pressure for
    /// QA. Bounded mode is a relief valve, not the steady state: any non-zero
    /// here means the reader / aggregator is not keeping up.
    /// </summary>
    public long DroppedObservations => Interlocked.Read(ref _droppedObservations);

    /// <summary>
    /// Coerce a TraceEvent <see cref="DateTime"/> (which arrives with
    /// <see cref="DateTimeKind.Local"/>) to a Unix-ms timestamp. Public for tests.
    /// </summary>
    internal static long ToUnixTimeMs(DateTime timestamp)
    {
        // DateTimeOffset(dt, TimeSpan.Zero) requires dt.Kind == Utc; pass through.
        var utc = timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.ToUniversalTime();
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    // ETW reports ProcessID = -1 when unknown; map that to null.
    private static int? NullablePid(int etwPid) =>
        etwPid < 0 ? null : etwPid;

    // ---- TCP connection lifecycle handlers ----

    // ---- TCP connect / accept handlers -------------------------------------
    //
    // Phase 6.1a: every connect/accept event ALSO synthesizes a zero-byte
    // NetworkObservation and writes it to the channel. Without this, a
    // process that opens a TCP socket and then idles (the classic C2 beacon
    // shape — establish, wait for command, exfil tiny payload, close) is
    // invisible to the aggregator. The Microsoft-Windows-Kernel-Network
    // ETW provider only fires TcpIpSend/Recv for packets carrying
    // application bytes; SYN/SYN-ACK/ACK/FIN traffic flows through these
    // connect/disconnect events. So a connection that never carries data
    // generates ZERO observations in the data-event path. The
    // _connectionSink call (left intact below) populates the
    // ConnectionLifecycleResolver cache for PID correction; the new
    // zero-byte observation is what makes the connection visible to
    // aggregator → flush → producer.
    //
    // Zero-byte semantics: the aggregator's accumulator math handles
    // bytes=0 cleanly (the sample accumulator adds 0; the connection
    // accumulator updates FirstSeen/LastSeen and stores 0 bytes). The
    // connection row at flush time still gets RemoteClass=Wan from the
    // remote-address classifier, which is the only field
    // AlertProducer.OnSessionConnectedWan keys on for rule evaluation.
    //
    // Direction lock: connects originate locally → Direction.Up. Accepts
    // are incoming → Direction.Down. UDP has no connect/disconnect events
    // (every UDP send IS a data event, so it's already covered).

    private void OnTcpConnect(TcpIpConnectTraceData d)
    {
        EmitConnectObservation(d.TimeStamp, d.ProcessID, Protocol.Tcp,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            Direction.Up);
        if (_connectionSink is null) return;
        var pid = d.ProcessID;
        if (pid <= 0) return;
        try
        {
            _connectionSink.OnConnect(
                Protocol.Tcp,
                new IPEndPoint(d.saddr, d.sport),
                new IPEndPoint(d.daddr, d.dport),
                pid,
                ToUnixTimeMs(d.TimeStamp));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConnectionLifecycleSink.OnConnect failed.");
        }
    }

    private void OnTcp6Connect(TcpIpV6ConnectTraceData d)
    {
        EmitConnectObservation(d.TimeStamp, d.ProcessID, Protocol.Tcp,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            Direction.Up);
        if (_connectionSink is null) return;
        var pid = d.ProcessID;
        if (pid <= 0) return;
        try
        {
            _connectionSink.OnConnect(
                Protocol.Tcp,
                new IPEndPoint(d.saddr, d.sport),
                new IPEndPoint(d.daddr, d.dport),
                pid,
                ToUnixTimeMs(d.TimeStamp));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConnectionLifecycleSink.OnConnect (v6) failed.");
        }
    }

    private void OnTcpAccept(TcpIpConnectTraceData d)
    {
        EmitConnectObservation(d.TimeStamp, d.ProcessID, Protocol.Tcp,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            Direction.Down);
        if (_connectionSink is null) return;
        var pid = d.ProcessID;
        if (pid <= 0) return;
        try
        {
            _connectionSink.OnConnect(
                Protocol.Tcp,
                new IPEndPoint(d.saddr, d.sport),
                new IPEndPoint(d.daddr, d.dport),
                pid,
                ToUnixTimeMs(d.TimeStamp));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConnectionLifecycleSink.OnConnect (accept) failed.");
        }
    }

    private void OnTcp6Accept(TcpIpV6ConnectTraceData d)
    {
        EmitConnectObservation(d.TimeStamp, d.ProcessID, Protocol.Tcp,
            new IPEndPoint(d.saddr, d.sport),
            new IPEndPoint(d.daddr, d.dport),
            Direction.Down);
        if (_connectionSink is null) return;
        var pid = d.ProcessID;
        if (pid <= 0) return;
        try
        {
            _connectionSink.OnConnect(
                Protocol.Tcp,
                new IPEndPoint(d.saddr, d.sport),
                new IPEndPoint(d.daddr, d.dport),
                pid,
                ToUnixTimeMs(d.TimeStamp));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConnectionLifecycleSink.OnConnect (accept v6) failed.");
        }
    }

    /// <summary>
    /// Zero-byte observation synthesized from a TcpIpConnect/Accept ETW
    /// event so that connect-only TCP sessions (no data exchanged) still
    /// flow through the aggregator. Mirrors the WriteObservation path the
    /// data-event handlers use, including the negative-PID guard.
    /// </summary>
    private void EmitConnectObservation(
        DateTime timestamp, int etwPid, Protocol protocol,
        IPEndPoint local, IPEndPoint remote, Direction direction)
    {
        // PID guard matches the original Connect handlers: ETW occasionally
        // reports PID=-1 for events it can't attribute; skip those rather
        // than emit a null-PID observation that the aggregator would just
        // drop as unattributed.
        if (etwPid <= 0) return;
        WriteObservation(timestamp, etwPid, protocol, local, remote, direction, 0);
    }

    private void OnTcpDisconnect(TcpIpTraceData d)
    {
        if (_connectionSink is null) return;
        try
        {
            _connectionSink.OnDisconnect(
                Protocol.Tcp,
                new IPEndPoint(d.saddr, d.sport),
                ToUnixTimeMs(d.TimeStamp));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConnectionLifecycleSink.OnDisconnect failed.");
        }
    }

    private void OnTcp6Disconnect(TcpIpV6TraceData d)
    {
        if (_connectionSink is null) return;
        try
        {
            _connectionSink.OnDisconnect(
                Protocol.Tcp,
                new IPEndPoint(d.saddr, d.sport),
                ToUnixTimeMs(d.TimeStamp));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConnectionLifecycleSink.OnDisconnect (v6) failed.");
        }
    }

    // ---- Process lifecycle handlers ----

    private void OnProcessStart(ProcessTraceData data)
    {
        if (_processSink is null) return;
        var pid = data.ProcessID;
        if (pid <= 0) return;

        var imagePath = ResolveProcessImagePath(data);
        if (string.IsNullOrEmpty(imagePath))
        {
            return;
        }

        var startMs = ToUnixTimeMs(data.TimeStamp);
        try
        {
            _processSink.OnProcessStart(pid, imagePath, startMs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ProcessLifecycleSink.OnProcessStart({Pid}) failed.", pid);
        }
    }

    private void OnProcessStop(ProcessTraceData data)
    {
        if (_processSink is null) return;
        var pid = data.ProcessID;
        if (pid <= 0) return;

        var stopMs = ToUnixTimeMs(data.TimeStamp);
        try
        {
            _processSink.OnProcessStop(pid, stopMs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ProcessLifecycleSink.OnProcessStop({Pid}) failed.", pid);
        }
    }

    /// <summary>
    /// Extract the best-available image path from a kernel process event.
    /// Prefers <c>ImageFileName</c> when it looks like a full DOS path; falls
    /// back to parsing the executable out of <c>CommandLine</c>; finally falls
    /// back to <c>ImageFileName</c> as a basename. NEVER calls Win32 from the
    /// ETW callback thread — that would race process exit and re-introduce
    /// the bug we just fixed.
    /// </summary>
    private static string ResolveProcessImagePath(ProcessTraceData data)
    {
        var image = data.ImageFileName;
        if (!string.IsNullOrEmpty(image) && LooksLikeFullPath(image))
        {
            return image;
        }

        var cmd = data.CommandLine;
        if (!string.IsNullOrEmpty(cmd))
        {
            var fromCmd = ExtractExeFromCommandLine(cmd);
            if (!string.IsNullOrEmpty(fromCmd) && LooksLikeFullPath(fromCmd))
            {
                return fromCmd;
            }
        }

        // Basename only — better than nothing.
        return image ?? string.Empty;
    }

    private static bool LooksLikeFullPath(string s)
    {
        // Rooted DOS path: "X:\..." or UNC "\\..."
        if (s.Length >= 3 && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) return true;
        if (s.Length >= 2 && s[0] == '\\' && s[1] == '\\') return true;
        return false;
    }

    private static string ExtractExeFromCommandLine(string cmd)
    {
        // CommandLine forms:
        //   "C:\path\img.exe" arg1 arg2
        //   C:\path\img.exe arg1
        //   img.exe arg1
        var trimmed = cmd.TrimStart();
        if (trimmed.Length == 0) return string.Empty;

        if (trimmed[0] == '"')
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1) return trimmed.Substring(1, end - 1);
            return string.Empty;
        }

        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed.Substring(0, space);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
        {
            return;
        }

        // Tell ProcessLoop "this exit is intentional" before we tear the
        // session down. Without this, a clean Dispose looks identical to
        // an unexpected exit and would flip IsFaulted.
        _shutdownRequested = true;

        try
        {
            _session.Dispose();
        }
        catch
        {
            // best-effort
        }

        if (_processThread is not null)
        {
            try
            {
                await Task.Run(() => _processThread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }

        _channel.Writer.TryComplete();
        _session = null;
        _processThread = null;
    }
}
