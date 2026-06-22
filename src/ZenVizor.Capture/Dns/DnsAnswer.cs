using System.Net;

namespace ZenVizor.Capture.Dns;

/// <summary>
/// One (IP, hostname, TTL) tuple extracted from a single DNS response.
/// Produced by <see cref="Rfc1035ResponseDecoder.Decode"/> and consumed by
/// the DNS source which writes it into the store. Hostname is the QNAME of
/// the response (the most-specific user-facing name per the Phase 8 spec),
/// trailing dot stripped; TTL is the minimum across the CNAME chain that
/// links QNAME to this IP.
/// </summary>
public readonly record struct DnsAnswer(IPAddress Ip, string Hostname, int TtlSeconds);
