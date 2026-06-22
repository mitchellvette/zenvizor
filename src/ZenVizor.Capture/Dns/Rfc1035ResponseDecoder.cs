// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;

namespace ZenVizor.Capture.Dns;

/// <summary>
/// Minimal RFC 1035 DNS response decoder. Extracts the (IP → hostname) tuples
/// the passive DNS observer needs and nothing else — TYPE A, TYPE AAAA, and
/// TYPE CNAME records in class IN. All other record types and any record in
/// the authority / additional sections are ignored.
/// <para>
/// Output convention: one <see cref="DnsAnswer"/> per A or AAAA record whose
/// owner name resolves back through the response's CNAME chain to the
/// question's QNAME. The hostname on each emitted answer is the QNAME — the
/// name the user (or the OS resolver) originally asked about — NOT the
/// terminal CNAME target. The TTL is the minimum along the chain so the
/// cache life respects the shortest-lived link, per standard DNS caching
/// semantics.
/// </para>
/// <para>
/// Returns an empty list (never throws) for any malformed, truncated,
/// error-RCODE, or non-response input. The decoder is purely a parser — it
/// does not validate that the response answers a query we actually sent
/// (we're observing arbitrary DNS traffic on the host).
/// </para>
/// </summary>
internal static class Rfc1035ResponseDecoder
{
    private const int HeaderLength = 12;
    private const ushort TypeA      = 1;
    private const ushort TypeCname  = 5;
    private const ushort TypeAaaa   = 28;
    private const ushort ClassIn    = 1;

    /// <summary>
    /// Cap on pointer hops while reading a single name. RFC 1035 says nothing
    /// about a maximum, but a hostile message could chain pointers in a loop
    /// or in a long zigzag. 16 is far more than any legitimate name needs
    /// (longest legal name has ~127 labels) and short enough that a malformed
    /// packet doesn't spin the CPU.
    /// </summary>
    private const int MaxNameHops = 16;

    public static IReadOnlyList<DnsAnswer> Decode(ReadOnlySpan<byte> response)
    {
        if (response.Length < HeaderLength)
        {
            return Array.Empty<DnsAnswer>();
        }

        var flags = ReadUInt16(response, 2);
        var qr    = (flags >> 15) & 0x1;
        var tc    = (flags >> 9)  & 0x1;
        var rcode =  flags        & 0xf;
        if (qr != 1 || tc == 1 || rcode != 0)
        {
            return Array.Empty<DnsAnswer>();
        }

        var qdcount = ReadUInt16(response, 4);
        var ancount = ReadUInt16(response, 6);
        if (qdcount == 0 || ancount == 0)
        {
            return Array.Empty<DnsAnswer>();
        }

        // Question section. Only the first question's QNAME matters — multi-
        // question responses are exceedingly rare and we don't need the type.
        var offset = HeaderLength;
        string? qname = null;
        for (var q = 0; q < qdcount; q++)
        {
            if (!TryReadName(response, ref offset, out var name))
            {
                return Array.Empty<DnsAnswer>();
            }
            if (q == 0)
            {
                qname = name;
            }
            // QTYPE (2) + QCLASS (2)
            if (offset + 4 > response.Length)
            {
                return Array.Empty<DnsAnswer>();
            }
            offset += 4;
        }
        if (string.IsNullOrEmpty(qname))
        {
            return Array.Empty<DnsAnswer>();
        }

        // Answer section: walk every record; collect CNAME aliases and the
        // IP-bearing records separately so the chain walk below can decide
        // which IP records sit on the QNAME's path.
        var cnameTarget    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cnameTtl       = new Dictionary<string, int>   (StringComparer.OrdinalIgnoreCase);
        var ipRecords      = new List<(string Name, IPAddress Ip, int Ttl)>();

        for (var a = 0; a < ancount; a++)
        {
            if (!TryReadName(response, ref offset, out var name))
            {
                break;
            }
            // RR fixed header: TYPE (2), CLASS (2), TTL (4), RDLENGTH (2) = 10
            if (offset + 10 > response.Length)
            {
                break;
            }
            var type     = ReadUInt16(response, offset);
            var rclass   = ReadUInt16(response, offset + 2);
            var ttl      = ReadInt32 (response, offset + 4);
            var rdlength = ReadUInt16(response, offset + 8);
            offset += 10;
            if (offset + rdlength > response.Length)
            {
                break;
            }
            if (ttl < 0) ttl = 0;

            if (rclass == ClassIn)
            {
                switch (type)
                {
                    case TypeA when rdlength == 4:
                    {
                        var bytes = new byte[4];
                        response.Slice(offset, 4).CopyTo(bytes);
                        ipRecords.Add((name, new IPAddress(bytes), ttl));
                        break;
                    }
                    case TypeAaaa when rdlength == 16:
                    {
                        var bytes = new byte[16];
                        response.Slice(offset, 16).CopyTo(bytes);
                        ipRecords.Add((name, new IPAddress(bytes), ttl));
                        break;
                    }
                    case TypeCname:
                    {
                        var nameOffset = offset;
                        if (TryReadName(response, ref nameOffset, out var target))
                        {
                            cnameTarget[name] = target;
                            cnameTtl[name]    = ttl;
                        }
                        break;
                    }
                }
            }

            offset += rdlength;
        }

        // Walk the CNAME chain from QNAME, collecting every name on the path
        // and tracking the minimum TTL of the chain's CNAME links.
        var chain  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { qname };
        var minTtl = int.MaxValue;
        var current = qname;
        for (var hop = 0; hop < MaxNameHops; hop++)
        {
            if (!cnameTarget.TryGetValue(current, out var next))
            {
                break;
            }
            if (!chain.Add(next))
            {
                // CNAME loop in the response — stop walking; what we have
                // already is fine, the loop itself is not our problem.
                break;
            }
            if (cnameTtl.TryGetValue(current, out var t))
            {
                minTtl = Math.Min(minTtl, t);
            }
            current = next;
        }

        var displayName = NormaliseName(qname);
        var result = new List<DnsAnswer>();
        foreach (var (recordName, ip, recordTtl) in ipRecords)
        {
            if (!chain.Contains(recordName))
            {
                continue;
            }
            var effectiveTtl = minTtl == int.MaxValue ? recordTtl : Math.Min(minTtl, recordTtl);
            result.Add(new DnsAnswer(ip, displayName, effectiveTtl));
        }

        return result;
    }

    private static string NormaliseName(string name)
    {
        // QNAMEs come off the wire as DNS labels concatenated with '.';
        // strip a trailing '.' so UI rendering matches the user's mental
        // model ("outlook.office.com" not "outlook.office.com.").
        return name.EndsWith('.') ? name[..^1] : name;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> b, int o) =>
        (ushort)((b[o] << 8) | b[o + 1]);

    private static int ReadInt32(ReadOnlySpan<byte> b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    /// <summary>
    /// Decode one DNS-encoded name starting at <paramref name="offset"/>.
    /// Advances <paramref name="offset"/> past the *first* occurrence of the
    /// name in the message (not past any pointer target). Returns false on
    /// malformed input — caller treats that as "abort this response."
    /// </summary>
    private static bool TryReadName(ReadOnlySpan<byte> b, ref int offset, out string name)
    {
        var sb = new StringBuilder();
        var current = offset;
        var hops = 0;
        var advanceTo = -1;
        var jumped = false;

        while (true)
        {
            if (current >= b.Length) { name = string.Empty; return false; }
            var len = b[current];
            if (len == 0)
            {
                current++;
                if (!jumped) advanceTo = current;
                break;
            }
            if ((len & 0xc0) == 0xc0)
            {
                if (current + 1 >= b.Length) { name = string.Empty; return false; }
                var ptr = ((len & 0x3f) << 8) | b[current + 1];
                if (!jumped) advanceTo = current + 2;
                jumped = true;
                if (++hops > MaxNameHops) { name = string.Empty; return false; }
                if (ptr >= current) { name = string.Empty; return false; } // RFC: pointer MUST be to prior data
                current = ptr;
                continue;
            }
            if ((len & 0xc0) != 0) { name = string.Empty; return false; } // 01/10 reserved
            current++;
            if (current + len > b.Length) { name = string.Empty; return false; }
            if (sb.Length > 0) sb.Append('.');
            for (var i = 0; i < len; i++) sb.Append((char)b[current + i]);
            current += len;
        }

        offset = advanceTo;
        name = sb.ToString();
        return true;
    }
}
