using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace SniSpike;

/// <summary>
/// Phase 8.5 spike — settles open unknown #1: does enabling the
/// <c>Microsoft-Windows-PktMon</c> provider inside a <see cref="TraceEventSession"/>
/// yield truncated packet PAYLOADS directly, or must PktMon's capture component
/// be started alongside (via <c>pktmon.exe</c>)?
/// <para>
/// Two phases, same live session:
///   Phase A — provider enabled, NO <c>pktmon start</c>. Count events + payloads.
///   Phase B — <c>pktmon start --capture --pkt-size 320 -m real-time</c> running.
/// Comparing the two tells us whether the capture component is required.
/// </para>
/// <para>INVARIANT #1: purely observational. pktmon capture mirrors existing
/// packets; our session only reads. Nothing is sent.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PktMonProbe
{
    private const string Provider = "Microsoft-Windows-PktMon";
    private const string SessionName = "ZenVizor.Spike.PktMon";

    public static int Run(int secondsPerPhase)
    {
        var stats = new Stats();

        ConfigureFilters();

        TraceEventSession.GetActiveSession(SessionName)?.Dispose();
        using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        try
        {
            session.EnableProvider(Provider, TraceEventLevel.Verbose);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EnableProvider('{Provider}') failed: {ex.Message}");
            return 1;
        }

        session.Source.Dynamic.All += e => stats.Observe(e);
        var reader = new Thread(() => { try { session.Source.Process(); } catch { } })
        { IsBackground = true, Name = "spike-pktmon-reader" };
        reader.Start();

        Console.WriteLine($"Phase A ({secondsPerPhase}s): provider enabled, NO pktmon capture. Generate HTTPS traffic now...");
        var before = stats.Snapshot();
        Thread.Sleep(TimeSpan.FromSeconds(secondsPerPhase));
        var afterA = stats.Snapshot();
        PrintDelta("Phase A (provider only)", before, afterA, stats);

        Console.WriteLine($"\nStarting pktmon capture component (--pkt-size 320 -m real-time)...");
        var started = Pktmon("start --capture --pkt-size 320 -m real-time");
        Console.WriteLine($"Phase B ({secondsPerPhase}s): pktmon capture running. Generate HTTPS traffic now...");
        Thread.Sleep(TimeSpan.FromSeconds(secondsPerPhase));
        var afterB = stats.Snapshot();
        PrintDelta("Phase B (capture component on)", afterA, afterB, stats);

        if (started) Pktmon("stop");
        session.Dispose();

        Console.WriteLine("\n=== VERDICT ===");
        var aPayloads = afterA.PayloadEvents - before.PayloadEvents;
        var bPayloads = afterB.PayloadEvents - afterA.PayloadEvents;
        if (aPayloads > 0)
            Console.WriteLine("Provider ALONE delivers payloads — no capture component needed.");
        else if (bPayloads > 0)
            Console.WriteLine("Capture component REQUIRED — payloads only arrived after `pktmon start`.");
        else
            Console.WriteLine("NO payloads in either phase via this provider/session shape. See event-name dump above; the real-time framing may differ (try etl2txt path).");
        if (stats.SampleHex is not null)
            Console.WriteLine($"Sample payload field '{stats.SampleField}' ({stats.SampleLen}B) first bytes: {stats.SampleHex}");
        return 0;
    }

    private static void PrintDelta(string label, Snap a, Snap b, Stats s)
    {
        Console.WriteLine($"  {label}: events={b.Events - a.Events} payloadEvents={b.PayloadEvents - a.PayloadEvents} maxPayloadLen={s.MaxPayloadLen}");
        Console.WriteLine($"    event names seen: {string.Join(", ", s.EventNames.Take(12))}");
    }

    private static void ConfigureFilters()
    {
        Pktmon("filter remove");
        Pktmon("filter add ZenSpikeTcp443 -t TCP -p 443");
        Pktmon("filter add ZenSpikeUdp443 -t UDP -p 443");
        Pktmon("filter add ZenSpikeTcp80 -t TCP -p 80");
    }

    private static bool Pktmon(string args)
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
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            var trimmed = (so + se).Trim();
            if (trimmed.Length > 0) Console.WriteLine($"    pktmon {args}: {Truncate(trimmed, 200)}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    pktmon {args} failed: {ex.Message}");
            return false;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private sealed class Stats
    {
        private long _events;
        private long _payloadEvents;
        public int MaxPayloadLen;
        public readonly HashSet<string> EventNames = new(StringComparer.Ordinal);
        public string? SampleHex;
        public string? SampleField;
        public int SampleLen;
        private readonly object _gate = new();

        public void Observe(TraceEvent e)
        {
            Interlocked.Increment(ref _events);
            lock (_gate) { if (EventNames.Count < 64) EventNames.Add(e.EventName); }

            // Find the largest byte[] payload field.
            foreach (var name in e.PayloadNames)
            {
                object? val;
                try { val = e.PayloadByName(name); } catch { continue; }
                if (val is byte[] bytes && bytes.Length > 0)
                {
                    Interlocked.Increment(ref _payloadEvents);
                    lock (_gate)
                    {
                        if (bytes.Length > MaxPayloadLen) MaxPayloadLen = bytes.Length;
                        if (SampleHex is null && bytes.Length >= 32)
                        {
                            SampleField = name;
                            SampleLen = bytes.Length;
                            SampleHex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length)));
                        }
                    }
                    break;
                }
            }
        }

        public Snap Snapshot() => new(Interlocked.Read(ref _events), Interlocked.Read(ref _payloadEvents));
    }

    private readonly record struct Snap(long Events, long PayloadEvents);
}
