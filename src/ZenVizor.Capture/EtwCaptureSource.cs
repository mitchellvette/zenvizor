using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    private readonly string _sessionName;
    private readonly ILogger _logger;
    private readonly Channel<NetworkObservation> _channel =
        Channel.CreateUnbounded<NetworkObservation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private TraceEventSession? _session;
    private Thread? _processThread;
    private CancellationTokenSource? _internalCts;

    public EtwCaptureSource(
        string? sessionName = null,
        ILogger<EtwCaptureSource>? logger = null)
    {
        _sessionName = sessionName ?? DefaultSessionName;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
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

        _internalCts = new CancellationTokenSource();
        _session = new TraceEventSession(_sessionName)
        {
            StopOnDispose = true,
        };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var kernel = _session.Source.Kernel;
        kernel.TcpIpRecv     += OnTcpRecv;
        kernel.TcpIpSend     += OnTcpSend;
        kernel.TcpIpRecvIPV6 += OnTcp6Recv;
        kernel.TcpIpSendIPV6 += OnTcp6Send;
        kernel.UdpIpRecv     += OnUdpRecv;
        kernel.UdpIpSend     += OnUdpSend;
        kernel.UdpIpRecvIPV6 += OnUdp6Recv;
        kernel.UdpIpSendIPV6 += OnUdp6Send;

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW Process loop terminated unexpectedly.");
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

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
        _channel.Writer.TryWrite(new NetworkObservation(ts, pid, protocol, local, remote, direction, bytes));
    }

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

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
        {
            return;
        }

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
        _internalCts?.Dispose();
        _session = null;
        _processThread = null;
        _internalCts = null;
    }
}
