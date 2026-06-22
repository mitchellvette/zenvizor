// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Security.Cryptography;
using ZenVizor.Capture.Sni;

namespace ZenVizor.Core.Tests.Sni;

/// <summary>
/// Synthetic packet builders for the Phase 8.6 SNI tests. These are the inverse
/// of the production parsers — they construct a valid ClientHello / protected
/// QUIC Initial / IP-TCP-UDP frame so the parsers can be exercised
/// deterministically with no live capture (CLAUDE.md: headless-first, exact
/// rows). The QUIC builder in particular is the closed-loop anchor: it encrypts
/// with the same RFC 9001 §A.1 key schedule the parser derives, so a decrypt bug
/// can't pass.
/// </summary>
internal static class SniTestFixtures
{
    private const byte ProtoTcp = 6;
    private const byte ProtoUdp = 17;

    // --- TLS ClientHello --------------------------------------------------

    /// <summary>Handshake message only (0x01 + len + body) — what QUIC CRYPTO carries.</summary>
    public static byte[] BuildHandshake(string serverName, int precedingExtensions = 0)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(serverName);

        var entry = new List<byte> { 0x00 }; // name_type host_name
        entry.AddRange(U16(nameBytes.Length));
        entry.AddRange(nameBytes);
        var sniBody = new List<byte>();
        sniBody.AddRange(U16(entry.Count)); // server_name_list length
        sniBody.AddRange(entry);

        var extensions = new List<byte>();
        for (var i = 0; i < precedingExtensions; i++)
        {
            extensions.AddRange(U16(0x002b)); // supported_versions (filler)
            extensions.AddRange(U16(2));
            extensions.AddRange(new byte[] { 0x03, 0x04 });
        }
        extensions.AddRange(U16(0x0000)); // server_name type
        extensions.AddRange(U16(sniBody.Count));
        extensions.AddRange(sniBody);

        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 }); // client_version
        body.AddRange(new byte[32]);              // random
        body.Add(0x00);                           // session_id length 0
        body.AddRange(U16(2));                     // cipher_suites length
        body.AddRange(new byte[] { 0x13, 0x01 });
        body.Add(0x01);                           // compression methods length
        body.Add(0x00);
        body.AddRange(U16(extensions.Count));
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01 }; // ClientHello
        handshake.AddRange(U24(body.Count));
        handshake.AddRange(body);
        return handshake.ToArray();
    }

    /// <summary>Full TLS record wrapping the handshake — what TCP/443 carries.</summary>
    public static byte[] BuildClientHelloRecord(string serverName, int precedingExtensions = 0)
    {
        var hs = BuildHandshake(serverName, precedingExtensions);
        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.AddRange(U16(hs.Length));
        record.AddRange(hs);
        return record.ToArray();
    }

    // --- QUIC v1 Initial (protected) --------------------------------------

    /// <summary>
    /// Build a header-protected, AEAD-encrypted QUIC v1 client Initial carrying a
    /// ClientHello with <paramref name="serverName"/>. The exact inverse of
    /// <c>QuicInitialParser</c>, used only to exercise it end-to-end.
    /// </summary>
    public static byte[] BuildProtectedQuicInitial(byte[] dcid, string serverName)
    {
        var keys = QuicCrypto.DeriveClientInitialKeys(dcid);
        var handshake = BuildHandshake(serverName);

        var frames = new List<byte> { 0x06 };       // CRYPTO
        frames.AddRange(VarInt(0));                  // offset
        frames.AddRange(VarInt(handshake.Length));   // length
        frames.AddRange(handshake);
        frames.AddRange(new byte[16]);               // PADDING
        var plaintext = frames.ToArray();

        const int pnLength = 1;
        var length = pnLength + plaintext.Length + 16; // pn + ciphertext + tag

        var header = new List<byte> { 0xC0 };       // long, fixed, Initial, pnLen=1
        header.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 }); // version v1
        header.Add((byte)dcid.Length);
        header.AddRange(dcid);
        header.Add(0x00);                            // SCID length 0
        header.Add(0x00);                            // token length varint 0
        header.AddRange(VarInt(length));
        var pnOffset = header.Count;
        header.Add(0x00);                            // packet number = 0 (1 byte)

        var ad = header.ToArray();                   // AD = unprotected header incl pn
        var nonce = QuicCrypto.BuildNonce(keys.Iv, 0);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(keys.Key, 16))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, ad);
        }

        var packet = new List<byte>();
        packet.AddRange(ad);
        packet.AddRange(ciphertext);
        packet.AddRange(tag);
        var arr = packet.ToArray();

        var sample = arr.AsSpan(pnOffset + 4, 16);
        var mask = QuicCrypto.HeaderProtectionMask(keys.Hp, sample);
        arr[0] ^= (byte)(mask[0] & 0x0f);
        for (var i = 0; i < pnLength; i++) arr[pnOffset + i] ^= mask[1 + i];
        return arr;
    }

    // --- IP / TCP / UDP framing -------------------------------------------

    /// <summary>Wrap an L4 payload in an IPv4 + TCP header (server-side port = dstPort).</summary>
    public static byte[] BuildIpv4Tcp(string dstIp, ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
        => Ipv4(ProtoTcp, dstIp, TcpSegment(srcPort, dstPort, payload));

    /// <summary>Wrap an L4 payload in an IPv4 + UDP header.</summary>
    public static byte[] BuildIpv4Udp(string dstIp, ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
        => Ipv4(ProtoUdp, dstIp, UdpDatagram(srcPort, dstPort, payload));

    /// <summary>Wrap an L4 payload in an IPv6 + TCP header.</summary>
    public static byte[] BuildIpv6Tcp(string dstIp, ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
        => Ipv6(ProtoTcp, dstIp, TcpSegment(srcPort, dstPort, payload));

    /// <summary>Prepend a 14-byte Ethernet II header (for the L2-strip tests).</summary>
    public static byte[] BuildEthernet(ushort etherType, ReadOnlySpan<byte> ip)
    {
        var frame = new byte[14 + ip.Length];
        // dst MAC (6) + src MAC (6) left zero.
        frame[12] = (byte)(etherType >> 8);
        frame[13] = (byte)etherType;
        ip.CopyTo(frame.AsSpan(14));
        return frame;
    }

    private static byte[] TcpSegment(ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        var tcp = new byte[20 + payload.Length];
        tcp[0] = (byte)(srcPort >> 8); tcp[1] = (byte)srcPort;
        tcp[2] = (byte)(dstPort >> 8); tcp[3] = (byte)dstPort;
        // seq/ack left zero
        tcp[12] = 0x50; // data offset = 5 words (20 bytes), no options
        tcp[13] = 0x18; // PSH+ACK (cosmetic)
        payload.CopyTo(tcp.AsSpan(20));
        return tcp;
    }

    private static byte[] UdpDatagram(ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        var udp = new byte[8 + payload.Length];
        udp[0] = (byte)(srcPort >> 8); udp[1] = (byte)srcPort;
        udp[2] = (byte)(dstPort >> 8); udp[3] = (byte)dstPort;
        var len = udp.Length;
        udp[4] = (byte)(len >> 8); udp[5] = (byte)len;
        // checksum left zero (optional over IPv4)
        payload.CopyTo(udp.AsSpan(8));
        return udp;
    }

    private static byte[] Ipv4(byte proto, string dstIp, ReadOnlySpan<byte> l4)
    {
        var ip = new byte[20 + l4.Length];
        ip[0] = 0x45;                       // version 4, IHL 5
        var total = ip.Length;
        ip[2] = (byte)(total >> 8); ip[3] = (byte)total;
        ip[8] = 64;                         // TTL
        ip[9] = proto;
        // src 0.0.0.0; dst at offset 16
        IPAddress.Parse(dstIp).GetAddressBytes().CopyTo(ip.AsSpan(16, 4));
        l4.CopyTo(ip.AsSpan(20));
        return ip;
    }

    private static byte[] Ipv6(byte nextHeader, string dstIp, ReadOnlySpan<byte> l4)
    {
        var ip = new byte[40 + l4.Length];
        ip[0] = 0x60;                       // version 6
        var payloadLen = l4.Length;
        ip[4] = (byte)(payloadLen >> 8); ip[5] = (byte)payloadLen;
        ip[6] = nextHeader;
        ip[7] = 64;                         // hop limit
        // src ::; dst at offset 24 (16 bytes)
        IPAddress.Parse(dstIp).GetAddressBytes().CopyTo(ip.AsSpan(24, 16));
        l4.CopyTo(ip.AsSpan(40));
        return ip;
    }

    private static byte[] VarInt(int v)
    {
        if (v < 64) return new[] { (byte)v };
        if (v < 16384) return new[] { (byte)(0x40 | (v >> 8)), (byte)v };
        return new[] { (byte)(0x80 | (v >> 24)), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U24(int v) => new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}
