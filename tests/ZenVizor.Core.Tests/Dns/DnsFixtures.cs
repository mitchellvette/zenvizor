using System.Net;
using System.Text;

namespace ZenVizor.Core.Tests.Dns;

/// <summary>
/// Hand-rolled DNS-response packet builder for the decoder tests. Just enough
/// of RFC 1035 to construct A / AAAA / CNAME response fixtures with optional
/// name compression. Not a general-purpose DNS library — written so test
/// fixtures stay readable.
/// </summary>
internal static class DnsFixtures
{
    /// <summary>
    /// Build a DNS response packet: header (response, no error, RA), one
    /// question for <paramref name="qname"/>, and the supplied answers in
    /// order. Names are written uncompressed by default; pass a name like
    /// "@N" where N is a decimal offset to emit a compression pointer to
    /// that absolute byte offset instead (see <see cref="ResponseWithPointer"/>
    /// for the pointer-loop / pointer-decode tests).
    /// </summary>
    public static byte[] Response(string qname, params Answer[] answers)
    {
        var bytes = new List<byte>();
        WriteHeader(bytes, qdcount: 1, ancount: (ushort)answers.Length, qrTcRcode: (qr: 1, tc: 0, rcode: 0));
        WriteName(bytes, qname);
        WriteUInt16(bytes, 1);   // QTYPE = A (irrelevant for the decoder)
        WriteUInt16(bytes, 1);   // QCLASS = IN
        foreach (var answer in answers)
        {
            WriteAnswer(bytes, answer);
        }
        return bytes.ToArray();
    }

    /// <summary>Builds a query (QR=0) — used to assert the decoder ignores queries.</summary>
    public static byte[] Query(string qname)
    {
        var bytes = new List<byte>();
        WriteHeader(bytes, qdcount: 1, ancount: 0, qrTcRcode: (qr: 0, tc: 0, rcode: 0));
        WriteName(bytes, qname);
        WriteUInt16(bytes, 1);
        WriteUInt16(bytes, 1);
        return bytes.ToArray();
    }

    /// <summary>Builds a response with TC (truncated) set.</summary>
    public static byte[] TruncatedResponse(string qname)
    {
        var bytes = new List<byte>();
        WriteHeader(bytes, qdcount: 1, ancount: 0, qrTcRcode: (qr: 1, tc: 1, rcode: 0));
        WriteName(bytes, qname);
        WriteUInt16(bytes, 1);
        WriteUInt16(bytes, 1);
        return bytes.ToArray();
    }

    /// <summary>Builds a response with NXDOMAIN (RCODE=3).</summary>
    public static byte[] NxDomainResponse(string qname)
    {
        var bytes = new List<byte>();
        WriteHeader(bytes, qdcount: 1, ancount: 0, qrTcRcode: (qr: 1, tc: 0, rcode: 3));
        WriteName(bytes, qname);
        WriteUInt16(bytes, 1);
        WriteUInt16(bytes, 1);
        return bytes.ToArray();
    }

    /// <summary>
    /// Build a response where the answer's owner name is encoded as a
    /// compression pointer back to the question's QNAME at offset 12 (the
    /// byte just past the 12-byte header). Exercises the decoder's pointer
    /// follow path.
    /// </summary>
    public static byte[] ResponseWithPointerToQname(string qname, int ttl, string a4Ip)
    {
        var bytes = new List<byte>();
        WriteHeader(bytes, qdcount: 1, ancount: 1, qrTcRcode: (qr: 1, tc: 0, rcode: 0));
        WriteName(bytes, qname);          // QNAME at offset 12
        WriteUInt16(bytes, 1);            // QTYPE
        WriteUInt16(bytes, 1);            // QCLASS

        // Answer: name is a compression pointer to offset 12.
        bytes.Add(0xc0);
        bytes.Add(0x0c);                  // pointer to 12
        WriteUInt16(bytes, 1);            // TYPE = A
        WriteUInt16(bytes, 1);            // CLASS = IN
        WriteInt32 (bytes, ttl);
        var rdata = IPAddress.Parse(a4Ip).GetAddressBytes();
        WriteUInt16(bytes, (ushort)rdata.Length);
        bytes.AddRange(rdata);
        return bytes.ToArray();
    }

    /// <summary>
    /// Response where the answer's name field is a pointer that points to
    /// itself — exercises the decoder's pointer-loop defence.
    /// </summary>
    public static byte[] ResponseWithSelfReferentialPointer(string qname)
    {
        var bytes = new List<byte>();
        WriteHeader(bytes, qdcount: 1, ancount: 1, qrTcRcode: (qr: 1, tc: 0, rcode: 0));
        WriteName(bytes, qname);
        WriteUInt16(bytes, 1);
        WriteUInt16(bytes, 1);

        // Self-referencing pointer: pointer at offset X points to X. The
        // decoder's "ptr must be < current" guard should reject this.
        var selfPos = bytes.Count;
        bytes.Add(0xc0);
        bytes.Add((byte)selfPos);
        WriteUInt16(bytes, 1);
        WriteUInt16(bytes, 1);
        WriteInt32 (bytes, 60);
        WriteUInt16(bytes, 4);
        bytes.AddRange(new byte[] { 1, 2, 3, 4 });
        return bytes.ToArray();
    }

    public static Answer A(string name, int ttl, string ipv4) =>
        new(name, Type: 1, ttl, IPAddress.Parse(ipv4).GetAddressBytes());

    public static Answer Aaaa(string name, int ttl, string ipv6) =>
        new(name, Type: 28, ttl, IPAddress.Parse(ipv6).GetAddressBytes());

    public static Answer Cname(string name, int ttl, string target)
    {
        var rdata = new List<byte>();
        WriteName(rdata, target);
        return new Answer(name, Type: 5, ttl, rdata.ToArray());
    }

    public readonly record struct Answer(string Name, ushort Type, int Ttl, byte[] Rdata);

    private static void WriteHeader(List<byte> bytes, ushort qdcount, ushort ancount, (int qr, int tc, int rcode) qrTcRcode)
    {
        bytes.AddRange(new byte[] { 0x00, 0x01 });   // ID
        var flagsHi = (byte)((qrTcRcode.qr << 7) | (1 << 0));   // QR + RD
        var flagsLo = (byte)((1 << 7) | (qrTcRcode.tc << 1) | (qrTcRcode.rcode & 0xf));
        // The flag layout above is approximate. For our decoder only QR, TC,
        // and RCODE matter — set them explicitly.
        var flags = (ushort)(((qrTcRcode.qr & 0x1) << 15)
                           | ((qrTcRcode.tc & 0x1) << 9)
                           | (qrTcRcode.rcode & 0xf));
        WriteUInt16(bytes, flags);
        WriteUInt16(bytes, qdcount);
        WriteUInt16(bytes, ancount);
        WriteUInt16(bytes, 0);   // NSCOUNT
        WriteUInt16(bytes, 0);   // ARCOUNT
    }

    private static void WriteAnswer(List<byte> bytes, Answer answer)
    {
        WriteName(bytes, answer.Name);
        WriteUInt16(bytes, answer.Type);
        WriteUInt16(bytes, 1);              // CLASS = IN
        WriteInt32 (bytes, answer.Ttl);
        WriteUInt16(bytes, (ushort)answer.Rdata.Length);
        bytes.AddRange(answer.Rdata);
    }

    private static void WriteName(List<byte> bytes, string name)
    {
        if (string.IsNullOrEmpty(name) || name == ".")
        {
            bytes.Add(0);
            return;
        }
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            if (label.Length > 63)
            {
                throw new ArgumentException($"Label '{label}' exceeds 63 bytes.", nameof(name));
            }
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
    }

    private static void WriteUInt16(List<byte> bytes, ushort v)
    {
        bytes.Add((byte)(v >> 8));
        bytes.Add((byte)(v & 0xff));
    }

    private static void WriteInt32(List<byte> bytes, int v)
    {
        bytes.Add((byte)(v >> 24));
        bytes.Add((byte)(v >> 16));
        bytes.Add((byte)(v >> 8));
        bytes.Add((byte)(v & 0xff));
    }
}
