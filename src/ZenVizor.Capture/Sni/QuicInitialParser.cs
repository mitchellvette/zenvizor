// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;

namespace ZenVizor.Capture.Sni;

/// <summary>
/// Phase 8.6 — extract SNI from a QUIC v1 Initial packet (the UDP/443
/// equivalent of the TLS ClientHello). The ClientHello rides CRYPTO frames in
/// the Initial, protected with keys derivable by any observer from the DCID +
/// fixed salt (see <see cref="QuicCrypto"/>). Same robustness contract as the
/// TLS parser: false / empty on anything that isn't a parseable v1 Initial
/// carrying an SNI; never throws.
/// <para>
/// Scope: QUIC v1 (0x00000001) only. Other versions (v2, drafts) and
/// post-handshake encrypted packets are ignored. A ClientHello that spans
/// multiple Initial packets is only recovered if the SNI is in the first one
/// (the common case); cross-packet CRYPTO reassembly across datagrams is out of
/// scope.
/// </para>
/// <para>
/// AES-128-GCM is all-or-nothing over the full ciphertext + 16-byte tag, so the
/// caller MUST hand this the full Initial datagram — a truncated Initial fails
/// AEAD auth and yields nothing. The UDP substrate captures the full datagram
/// for exactly this reason (see Phase 8.5 findings §8.2).
/// </para>
/// </summary>
internal static class QuicInitialParser
{
    private const byte FramePadding = 0x00;
    private const byte FramePing = 0x01;
    private const byte FrameCrypto = 0x06;

    public static bool TryParse(ReadOnlySpan<byte> udp, out string sni)
    {
        sni = string.Empty;

        // Long header form (0x80) + fixed bit (0x40). Type bits 0x30 == 00 -> Initial.
        if (udp.Length < 7) return false;
        var first = udp[0];
        if ((first & 0x80) == 0) return false;           // not long header
        if ((first & 0x40) == 0) return false;           // fixed bit must be set
        if ((first & 0x30) != 0x00) return false;        // not an Initial

        var version = (uint)((udp[1] << 24) | (udp[2] << 16) | (udp[3] << 8) | udp[4]);
        if (version != QuicCrypto.VersionV1) return false;

        var o = 5;
        if (o >= udp.Length) return false;
        var dcidLen = udp[o++];
        if (dcidLen > 20 || o + dcidLen > udp.Length) return false;
        var dcid = udp.Slice(o, dcidLen);
        o += dcidLen;

        if (o >= udp.Length) return false;
        var scidLen = udp[o++];
        if (scidLen > 20 || o + scidLen > udp.Length) return false;
        o += scidLen; // skip SCID

        if (!QuicCrypto.TryReadVarInt(udp, ref o, out var tokenLen)) return false;
        if (o + (int)tokenLen > udp.Length) return false;
        o += (int)tokenLen; // skip token

        if (!QuicCrypto.TryReadVarInt(udp, ref o, out var remainingLen)) return false;
        var pnOffset = o;
        if (pnOffset + (int)remainingLen > udp.Length) return false;
        if (remainingLen < 20) return false; // need sample + tag room

        byte[] plaintext;
        try
        {
            plaintext = Decrypt(udp, dcid, pnOffset, (int)remainingLen);
        }
        catch (CryptographicException)
        {
            return false; // AEAD auth failed -> not a v1 Initial we can read
        }
        catch
        {
            return false;
        }

        return TryReadSniFromFrames(plaintext, out sni);
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> dcid, int pnOffset, int remainingLen)
    {
        var keys = QuicCrypto.DeriveClientInitialKeys(dcid);

        // Header protection: sample 16 bytes starting 4 bytes after pnOffset
        // (RFC 9001 §5.4.2 — assumes the largest 4-byte packet-number field).
        var sampleOffset = pnOffset + 4;
        if (sampleOffset + 16 > pnOffset + remainingLen) throw new CryptographicException("sample OOB");
        var sample = packet.Slice(sampleOffset, 16);
        var mask = QuicCrypto.HeaderProtectionMask(keys.Hp, sample);

        // Work on a mutable copy of the whole packet so AD reflects the
        // unprotected header bytes.
        var buf = packet.ToArray();
        buf[0] ^= (byte)(mask[0] & 0x0f);                 // long header: low 4 bits
        var pnLen = (buf[0] & 0x03) + 1;
        ulong packetNumber = 0;
        for (var i = 0; i < pnLen; i++)
        {
            buf[pnOffset + i] ^= mask[1 + i];
            packetNumber = (packetNumber << 8) | buf[pnOffset + i];
        }

        var headerLen = pnOffset + pnLen;
        var ad = buf.AsSpan(0, headerLen).ToArray();
        var cipherStart = headerLen;
        var cipherLen = (pnOffset + remainingLen) - headerLen - 16; // minus tag
        if (cipherLen <= 0) throw new CryptographicException("empty ciphertext");
        var ciphertext = buf.AsSpan(cipherStart, cipherLen).ToArray();
        var tag = buf.AsSpan(cipherStart + cipherLen, 16).ToArray();

        var nonce = QuicCrypto.BuildNonce(keys.Iv, packetNumber);
        var plaintext = new byte[cipherLen];
        using var gcm = new AesGcm(keys.Key, 16);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, ad);
        return plaintext;
    }

    /// <summary>
    /// Walk QUIC frames, reassemble CRYPTO data by offset, parse the resulting
    /// TLS handshake for SNI.
    /// </summary>
    private static bool TryReadSniFromFrames(ReadOnlySpan<byte> frames, out string sni)
    {
        sni = string.Empty;
        // CRYPTO offsets in an Initial start at 0; collect into a buffer.
        var crypto = new SortedDictionary<ulong, byte[]>();
        var o = 0;
        while (o < frames.Length)
        {
            var type = frames[o++];
            switch (type)
            {
                case FramePadding:
                case FramePing:
                    continue;
                case FrameCrypto:
                {
                    if (!QuicCrypto.TryReadVarInt(frames, ref o, out var off)) return false;
                    if (!QuicCrypto.TryReadVarInt(frames, ref o, out var len)) return false;
                    if (o + (int)len > frames.Length) return false;
                    crypto[off] = frames.Slice(o, (int)len).ToArray();
                    o += (int)len;
                    break;
                }
                default:
                    // ACK (0x02/0x03) and others carry varint fields we won't
                    // see before CRYPTO in a client Initial; bail out rather
                    // than mis-walk an unknown frame.
                    return TryAssemble(crypto, out sni);
            }
        }
        return TryAssemble(crypto, out sni);
    }

    private static bool TryAssemble(SortedDictionary<ulong, byte[]> crypto, out string sni)
    {
        sni = string.Empty;
        if (crypto.Count == 0) return false;
        using var ms = new MemoryStream();
        ulong expected = 0;
        foreach (var (off, data) in crypto)
        {
            if (off != expected) break; // gap — stop at contiguous prefix
            ms.Write(data, 0, data.Length);
            expected = off + (ulong)data.Length;
        }
        return TlsClientHelloParser.TryParseHandshake(ms.ToArray(), out sni);
    }
}
