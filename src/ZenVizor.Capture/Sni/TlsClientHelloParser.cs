// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Capture.Sni;

/// <summary>
/// Phase 8.6 — extracts the plaintext SNI host_name from a TLS ClientHello.
/// Mirrors the robustness contract of <c>Rfc1035ResponseDecoder</c>: returns
/// false / empty on any malformed, truncated, or non-ClientHello input and
/// NEVER throws. Bounds-checked on every read.
/// <para>
/// Input is the start of a TCP stream payload (the first bytes the client
/// sent after the handshake completes at the TCP layer). The SNI is plaintext
/// in TLS 1.2 and in TLS 1.3 *without* Encrypted ClientHello (ECH). ECH moves
/// the real SNI into an encrypted extension — out of reach by design, the
/// documented residual gap (see Phase 8 verification doc "Known limitations").
/// </para>
/// <para>
/// Handles a ClientHello that spans more than one TLS record fragment only to
/// the extent the bytes are already in the buffer; the caller is responsible
/// for accumulating enough of the flow (<see cref="SniFlowTracker"/> accumulates
/// up to a per-flow cap). If the SNI extension isn't fully present yet, returns
/// false so the caller keeps accumulating.
/// </para>
/// </summary>
internal static class TlsClientHelloParser
{
    private const byte ContentTypeHandshake = 0x16;
    private const byte HandshakeTypeClientHello = 0x01;
    private const ushort ExtensionServerName = 0x0000;
    private const byte SniNameTypeHostName = 0x00;

    /// <summary>
    /// Try to pull the SNI host_name out of <paramref name="tcpPayload"/>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> tcpPayload, out string sni)
    {
        sni = string.Empty;

        // TLS record header: type(1) version(2) length(2)
        if (tcpPayload.Length < 5) return false;
        if (tcpPayload[0] != ContentTypeHandshake) return false;
        // tcpPayload[1] is the legacy record version major (0x03); don't
        // hard-require the minor — TLS 1.3 ClientHellos still carry 0x0301.
        if (tcpPayload[1] != 0x03) return false;

        var recordLen = ReadUInt16(tcpPayload, 3);
        if (recordLen == 0) return false;

        // The ClientHello handshake body lives inside the record fragment, but
        // a large ClientHello can be split across multiple records. We walk the
        // handshake structure over the contiguous post-header bytes we have
        // rather than trusting a single record length, so a multi-record split
        // that the caller has already accumulated still parses.
        var hs = tcpPayload[5..];
        return TryParseHandshake(hs, out sni);
    }

    /// <summary>
    /// Parse from the handshake layer directly (no TLS record header). QUIC
    /// carries the ClientHello in CRYPTO frames without the record wrapper, so
    /// the QUIC path calls this after reassembling the CRYPTO bytes.
    /// </summary>
    public static bool TryParseHandshake(ReadOnlySpan<byte> hs, out string sni)
    {
        sni = string.Empty;

        // Handshake header: msg_type(1) length(3)
        if (hs.Length < 4) return false;
        if (hs[0] != HandshakeTypeClientHello) return false;
        // We don't require the full body to be present (segmentation), but we
        // do need the body region to start where we expect.
        var body = hs[4..];

        var o = 0;

        // client_version(2) + random(32)
        if (!Advance(body, ref o, 2 + 32)) return false;

        // session_id: len(1) + bytes
        if (o >= body.Length) return false;
        var sidLen = body[o];
        o += 1;
        if (!Advance(body, ref o, sidLen)) return false;

        // cipher_suites: len(2) + bytes
        if (o + 2 > body.Length) return false;
        var csLen = ReadUInt16(body, o);
        o += 2;
        if (!Advance(body, ref o, csLen)) return false;

        // compression_methods: len(1) + bytes
        if (o >= body.Length) return false;
        var compLen = body[o];
        o += 1;
        if (!Advance(body, ref o, compLen)) return false;

        // extensions: len(2) + extension list
        if (o + 2 > body.Length) return false;
        var extTotal = ReadUInt16(body, o);
        o += 2;
        // Clamp the extension region to what's actually present so a truncated
        // tail still lets us scan the extensions we DO have.
        var extEnd = Math.Min(o + extTotal, body.Length);

        while (o + 4 <= extEnd)
        {
            var extType = ReadUInt16(body, o);
            var extLen = ReadUInt16(body, o + 2);
            o += 4;
            if (o + extLen > body.Length) return false; // SNI not fully arrived
            if (extType == ExtensionServerName)
            {
                if (TryReadSni(body.Slice(o, extLen), out sni)) return true;
                return false;
            }
            o += extLen;
        }

        return false;
    }

    /// <summary>
    /// server_name extension body: server_name_list len(2), then entries of
    /// name_type(1) + name len(2) + name. We take the first host_name entry.
    /// </summary>
    private static bool TryReadSni(ReadOnlySpan<byte> ext, out string sni)
    {
        sni = string.Empty;
        if (ext.Length < 2) return false;
        var listLen = ReadUInt16(ext, 0);
        var o = 2;
        var end = Math.Min(2 + listLen, ext.Length);

        while (o + 3 <= end)
        {
            var nameType = ext[o];
            var nameLen = ReadUInt16(ext, o + 1);
            o += 3;
            if (o + nameLen > ext.Length) return false;
            if (nameType == SniNameTypeHostName && nameLen > 0)
            {
                // host_name is ASCII (IDNA-encoded for non-ASCII). Render as
                // latin1/ASCII — punycode stays as-is, which is fine for a
                // store key.
                var name = new char[nameLen];
                for (var i = 0; i < nameLen; i++) name[i] = (char)ext[o + i];
                var s = new string(name);
                if (IsPlausibleHostname(s))
                {
                    sni = s;
                    return true;
                }
                return false;
            }
            o += nameLen;
        }
        return false;
    }

    private static bool IsPlausibleHostname(string s)
    {
        if (s.Length is 0 or > 253) return false;
        foreach (var c in s)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9') or '.' or '-' or '_';
            if (!ok) return false;
        }
        return s.Contains('.');
    }

    private static bool Advance(ReadOnlySpan<byte> b, ref int o, int n)
    {
        if (n < 0) return false;
        if (o + n > b.Length) return false;
        o += n;
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> b, int o) =>
        (ushort)((b[o] << 8) | b[o + 1]);
}
