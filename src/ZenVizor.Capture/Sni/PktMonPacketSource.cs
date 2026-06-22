using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZenVizor.Capture.Sni;

/// <summary>
/// Phase 8.6 — PRIMARY packet substrate. Drives an in-kernel
/// <c>Microsoft-Windows-PktMon</c> capture with a server-side port filter (TCP
/// 443/80, UDP 443) so Windows mirrors only the client→server packets that can
/// carry a ClientHello / Host header / QUIC Initial, then surfaces each packet's
/// payload through our own sibling <see cref="TraceEventSession"/>.
/// <para>
/// INVARIANT #1: strictly observational. PktMon mirrors packets the host was
/// already exchanging; we never connect or send. The self-monitoring lens
/// confirms (Phase 8.5 §5). The child <c>pktmon.exe</c> we spawn only toggles
/// the capture component — it is not a network egress.
/// </para>
/// <para>
/// Phase 8.5 §7 (the de-risked unknown): enabling the provider in a
/// <see cref="TraceEventSession"/> is not on its own sufficient — PktMon only
/// emits packet payloads once its capture component is running, so we start it
/// in <c>-m real-time</c> mode (no .etl file, nothing hits disk). The child's
/// stdout is drained on a background reader thread (NEVER a blocking
/// <c>ReadToEnd()</c> on the still-streaming process — that was the spike hang).
/// </para>
/// <para>
/// Phase 8.5 §8.1: PktMon delivers full L2 Ethernet frames. We strip the L2
/// header here (and only here) via <see cref="EthernetFrame"/>, handing the
/// substrate-agnostic <see cref="SniPacketProcessor"/> an IP-layer packet. Snap
/// length is 1600 bytes — NOT the spike's 320 — because a QUIC Initial AEAD
/// covers the full datagram and truncation breaks the all-or-nothing decrypt.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PktMonPacketSource : IRawPacketSource
{
    private const string ProviderName = "Microsoft-Windows-PktMon";
    private const string DefaultSessionName = "ZenVizor.Capture.Sni.PktMon";

    // Full QUIC Initials are ~1200 bytes; 1600 covers them plus L2/IP/UDP
    // overhead. Truncating (spike used 320) silently breaks AES-128-GCM auth
    // over the full ciphertext+tag — see Phase 8.5 §8.2.
    private const int CaptureSnapLengthBytes = 1600;

    private static readonly (string Name, string Args)[] CaptureFilters =
    {
        ("ZenVizorSniTcp443", "-t TCP -p 443"),
        ("ZenVizorSniUdp443", "-t UDP -p 443"),
        ("ZenVizorSniTcp80",  "-t TCP -p 80"),
    };

    private readonly string _sessionName;
    private readonly ILogger _logger;

    private TraceEventSession? _session;
    private Thread? _processThread;
    private Process? _captureProcess;
    private Action<ReadOnlyMemory<byte>>? _onIpPacket;
    private volatile bool _shutdownRequested;
    private volatile bool _faulted;
    private bool _filtersConfigured;

    public PktMonPacketSource(string? sessionName = null, ILogger? logger = null)
    {
        _sessionName = sessionName ?? DefaultSessionName;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsFaulted => _faulted;

    public void Start(Action<ReadOnlyMemory<byte>> onIpPacket)
    {
        ArgumentNullException.ThrowIfNull(onIpPacket);
        if (_session is not null) return;

        _onIpPacket = onIpPacket;
        _shutdownRequested = false;
        _faulted = false;

        TryStopLeakedSession(_sessionName, _logger);
        ConfigureFilters();
        StartCaptureComponent();

        _session = new TraceEventSession(_sessionName) { StopOnDispose = true };
        _session.EnableProvider(ProviderName, TraceEventLevel.Verbose);
        _session.Source.Dynamic.All += OnEvent;

        _processThread = new Thread(ProcessLoop)
        {
            IsBackground = true,
            Name = "ZenVizor.SniCapture.PktMon",
        };
        _processThread.Start();

        _logger.LogInformation(
            "SNI PktMon capture started (session '{Session}', snap {Snap}B).",
            _sessionName, CaptureSnapLengthBytes);
    }

    private void ProcessLoop()
    {
        try
        {
            _session?.Source.Process();
            if (!_shutdownRequested)
            {
                _faulted = true;
                _logger.LogError(
                    "SNI PktMon Process loop exited without a shutdown request — SNI capture is now dead.");
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            _logger.LogError(ex, "SNI PktMon Process loop terminated unexpectedly.");
        }
    }

    private void OnEvent(TraceEvent data)
    {
        var handler = _onIpPacket;
        if (handler is null) return;

        // PktMon packet events carry the (truncated) frame as a byte[] payload
        // field. Take the largest such field, strip L2, deliver the IP packet.
        foreach (var name in data.PayloadNames)
        {
            object? value;
            try { value = data.PayloadByName(name); }
            catch { continue; }

            if (value is not byte[] frame || frame.Length == 0) continue;

            if (!EthernetFrame.TryGetIpOffset(frame, out var ipOffset)) return;
            try
            {
                handler(frame.AsMemory(ipOffset));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SNI PktMon packet handler threw (ignored).");
            }
            return;
        }
    }

    private void ConfigureFilters()
    {
        // Clear any filters a prior crashed run left behind, then install ours.
        Pktmon("filter remove");
        foreach (var (name, args) in CaptureFilters)
        {
            Pktmon($"filter add {name} {args}");
        }
        _filtersConfigured = true;
    }

    private void StartCaptureComponent()
    {
        var psi = new ProcessStartInfo("pktmon.exe",
            $"start --capture --pkt-size {CaptureSnapLengthBytes} -m real-time")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            _captureProcess = Process.Start(psi);
            if (_captureProcess is null)
            {
                throw new InvalidOperationException("pktmon.exe did not start.");
            }

            // Real-time mode streams continuously. Drain both pipes on the
            // async reader threads the framework owns — a blocking ReadToEnd()
            // here would never return (Phase 8.5 §7 hang). We discard the
            // decoded text; the actual bytes come via our provider session.
            _captureProcess.OutputDataReceived += static (_, _) => { };
            _captureProcess.ErrorDataReceived += static (_, _) => { };
            _captureProcess.BeginOutputReadLine();
            _captureProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start the PktMon capture component (pktmon.exe start).", ex);
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

    private void Pktmon(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("pktmon.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            // One-shot subcommands return promptly; safe to read to completion.
            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "pktmon {Args} failed (non-fatal).", args);
        }
    }

    public void Dispose()
    {
        _shutdownRequested = true;

        if (_captureProcess is not null)
        {
            try { if (!_captureProcess.HasExited) _captureProcess.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
            try { _captureProcess.Dispose(); } catch { /* best-effort */ }
            _captureProcess = null;
        }

        // Stop the capture component and remove our filters so we leave no
        // global PktMon state behind for the next run / other tools.
        Pktmon("stop");
        if (_filtersConfigured)
        {
            Pktmon("filter remove");
            _filtersConfigured = false;
        }

        try { _session?.Dispose(); } catch { /* best-effort */ }

        if (_processThread is not null)
        {
            try { _processThread.Join(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        }

        _session = null;
        _processThread = null;
        _onIpPacket = null;
    }
}
