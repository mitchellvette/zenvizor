// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace ZenVizor.Capture.Dns;

/// <summary>
/// Maps a parsed <c>Microsoft-Windows-DNS-Client</c> event 3008 payload to
/// <see cref="DnsAnswer"/>s. Separated from <see cref="DnsCaptureSource"/>
/// so the mapping is unit-testable without an ETW session.
/// <para>
/// QueryResults wire format: semicolon-separated list containing a mix of
/// CNAME alias strings and resolved IPs. The Windows resolver has varied
/// the format slightly across builds (raw IP, <c>type:N value</c>,
/// or values prefixed with <c>?</c>/<c>*</c>), so the mapper strips the
/// most common framing variants before attempting an IP parse. Tokens
/// that don't parse as an IP are silently skipped — those are CNAME
/// aliases on the way to the resolved IP and we don't need to surface
/// them (QNAME is the displayed hostname per the Phase 8 spec).
/// </para>
/// <para>
/// The event payload does not include the response TTL, so the mapper
/// stamps a caller-supplied default on every emitted answer. See
/// <see cref="DnsCaptureSource.DefaultTtlSeconds"/> for the rationale.
/// </para>
/// </summary>
internal static class DnsClientEventMapper
{
    public static IReadOnlyList<DnsAnswer> Map(string queryName, string queryResults, int defaultTtlSeconds)
    {
        if (string.IsNullOrWhiteSpace(queryName) || string.IsNullOrWhiteSpace(queryResults))
        {
            return Array.Empty<DnsAnswer>();
        }

        var qname = queryName.EndsWith('.') ? queryName[..^1] : queryName;
        var result = new List<DnsAnswer>();
        foreach (var raw in queryResults.Split(';'))
        {
            var entry = StripPrefix(raw.Trim());
            if (entry.Length == 0)
            {
                continue;
            }
            if (IPAddress.TryParse(entry, out var ip))
            {
                result.Add(new DnsAnswer(ip, qname, defaultTtlSeconds));
            }
        }
        return result;
    }

    private static string StripPrefix(string entry)
    {
        if (entry.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
        {
            var sp = entry.IndexOf(' ', 5);
            return sp < 0 ? string.Empty : entry[(sp + 1)..];
        }
        if (entry.Length > 0 && (entry[0] == '?' || entry[0] == '*'))
        {
            return entry[1..];
        }
        return entry;
    }
}
