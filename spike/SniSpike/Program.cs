using System.Net;
using SniSpike;
using ZenVizor.Core.Dns;

// THROWAWAY Phase 8.5 spike harness. Modes:
//   tls-selftest            offline ClientHello parser check (no elevation)
//   rawsock [seconds]       raw-socket SIO_RCVALL capture -> TLS SNI (ELEVATED)
//   pktmon-probe [seconds]  enable Microsoft-Windows-PktMon, dump events (ELEVATED)
// The capture modes feed the REAL DnsResolutionStore to prove the back half is
// source-agnostic.

var mode = args.Length > 0 ? args[0] : "tls-selftest";

if (mode is "rawsock" or "pktmon-probe")
{
    Console.WriteLine($"SniSpike PID={Environment.ProcessId} (image SniSpike.exe) — " +
        "cross-check this PID in ZenVizor: it must show ZERO outbound (invariant #1).");
}

switch (mode)
{
    case "tls-selftest":
        return TlsSelfTest.Run();
    case "quic-selftest":
        return QuicSelfTest.Run();
    case "selftest":
    {
        var a = TlsSelfTest.Run();
        Console.WriteLine();
        var b = QuicSelfTest.Run();
        return a == 0 && b == 0 ? 0 : 1;
    }
    case "rawsock":
        return RawSocketCapture.Run(args.Length > 1 && int.TryParse(args[1], out var rs) ? rs : 60);
    case "pktmon-probe":
        return PktMonProbe.Run(args.Length > 1 && int.TryParse(args[1], out var pp) ? pp : 12);
    default:
        Console.Error.WriteLine($"Unknown mode '{mode}'. Modes: tls-selftest, rawsock, pktmon-probe.");
        return 2;
}

internal static class TlsSelfTest
{
    public static int Run()
    {
        var pass = 0;
        var fail = 0;

        void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("TLS ClientHello SNI parser — offline self-test");

        // Positive: TLS 1.2-style ClientHello carrying SNI.
        var hello = ClientHelloFactory.BuildRecord("outlook.office.com");
        var got = TlsClientHelloParser.TryParse(hello, out var sni);
        Check($"extract 'outlook.office.com' (got '{sni}')", got && sni == "outlook.office.com");

        // Positive: SNI not the first extension (preceded by a dummy ext).
        var hello2 = ClientHelloFactory.BuildRecord("www.google.com", precedingExtensions: 2);
        var got2 = TlsClientHelloParser.TryParse(hello2, out var sni2);
        Check($"extract past leading extensions (got '{sni2}')", got2 && sni2 == "www.google.com");

        // Negative: non-handshake record.
        var notHs = new byte[] { 0x17, 0x03, 0x03, 0x00, 0x10, 1, 2, 3, 4 };
        Check("reject application_data record", !TlsClientHelloParser.TryParse(notHs, out _));

        // Negative: truncated mid-SNI -> false, no throw.
        var truncated = hello[..(hello.Length - 6)];
        Check("reject truncated SNI (no throw)", !TlsClientHelloParser.TryParse(truncated, out _));

        // Negative: empty / tiny.
        Check("reject empty input", !TlsClientHelloParser.TryParse(ReadOnlySpan<byte>.Empty, out _));

        // Prove the feed: extracted SNI lands in the REAL store.
        var store = new DnsResolutionStore();
        var ip = IPAddress.Parse("52.96.0.1");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (TlsClientHelloParser.TryParse(hello, out var feed))
        {
            store.Record(ip, feed, 300, now);
        }
        var found = store.TryGetHostname(ip, now + 1000, out var stored);
        Check($"DnsResolutionStore round-trip (got '{stored}')", found && stored == "outlook.office.com");

        // HTTP/1.1 Host header.
        var req = System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: neverssl.com\r\nUser-Agent: x\r\n\r\n");
        var gotHttp = HttpHostParser.TryParse(req, out var httpHost);
        Check($"HTTP Host -> 'neverssl.com' (got '{httpHost}')", gotHttp && httpHost == "neverssl.com");
        Check("reject non-HTTP bytes as Host", !HttpHostParser.TryParse(new byte[] { 0x16, 0x03, 0x01, 0x02, 0x00 }, out _));

        Console.WriteLine($"\n{pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }
}
