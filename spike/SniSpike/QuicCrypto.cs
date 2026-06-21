using System.Security.Cryptography;
using System.Text;

namespace SniSpike;

/// <summary>
/// Phase 8.5 spike — QUIC v1 Initial-packet crypto primitives, BCL only
/// (<see cref="HKDF"/> + <see cref="Aes"/> + <see cref="AesGcm"/>). The QUIC
/// Initial is "encrypted" with keys derived deterministically from the client's
/// Destination Connection ID and the fixed RFC 9001 v1 salt — i.e. readable by
/// any passive observer. No secrets, no network, pure computation.
/// </summary>
internal static class QuicCrypto
{
    /// <summary>RFC 9001 §5.2 — QUIC v1 initial salt.</summary>
    public static readonly byte[] InitialSaltV1 =
        Convert.FromHexString("38762cf7f55934b34d179ae6a4c80cadccbb7f0a");

    public const uint VersionV1 = 0x00000001;

    public readonly record struct InitialKeys(byte[] Key, byte[] Iv, byte[] Hp);

    /// <summary>
    /// Derive the CLIENT Initial AEAD key/iv and header-protection key from the
    /// Destination Connection ID, per RFC 9001 §5.2.
    /// </summary>
    public static InitialKeys DeriveClientInitialKeys(ReadOnlySpan<byte> dcid)
    {
        // initial_secret = HKDF-Extract(initial_salt, client_dst_connection_id)
        var initialSecret = HKDF.Extract(HashAlgorithmName.SHA256, dcid.ToArray(), InitialSaltV1);
        var clientSecret = ExpandLabel(initialSecret, "client in", 32);
        var key = ExpandLabel(clientSecret, "quic key", 16);
        var iv = ExpandLabel(clientSecret, "quic iv", 12);
        var hp = ExpandLabel(clientSecret, "quic hp", 16);
        return new InitialKeys(key, iv, hp);
    }

    /// <summary>
    /// HKDF-Expand-Label (RFC 8446 §7.1) as QUIC uses it: the label is prefixed
    /// with "tls13 " and the context is empty.
    /// </summary>
    public static byte[] ExpandLabel(byte[] secret, string label, int length)
    {
        var fullLabel = "tls13 " + label;
        var labelBytes = Encoding.ASCII.GetBytes(fullLabel);

        // struct { uint16 length; opaque label<7..255>; opaque context<0..255>; }
        var info = new byte[2 + 1 + labelBytes.Length + 1];
        info[0] = (byte)(length >> 8);
        info[1] = (byte)length;
        info[2] = (byte)labelBytes.Length;
        labelBytes.CopyTo(info, 3);
        info[3 + labelBytes.Length] = 0x00; // empty context
        return HKDF.Expand(HashAlgorithmName.SHA256, secret, length, info);
    }

    /// <summary>
    /// Header-protection mask = AES-128-ECB(hp_key, sample). RFC 9001 §5.4.3.
    /// </summary>
    public static byte[] HeaderProtectionMask(byte[] hpKey, ReadOnlySpan<byte> sample16)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = hpKey;
        return aes.EncryptEcb(sample16, PaddingMode.None);
    }

    /// <summary>
    /// AEAD nonce = left-padded packet number XOR iv. RFC 9001 §5.3.
    /// </summary>
    public static byte[] BuildNonce(byte[] iv, ulong packetNumber)
    {
        var nonce = (byte[])iv.Clone();
        for (var i = 0; i < 8; i++)
        {
            nonce[nonce.Length - 1 - i] ^= (byte)(packetNumber >> (8 * i));
        }
        return nonce;
    }

    /// <summary>
    /// Read a QUIC variable-length integer (RFC 9000 §16). Returns false on
    /// truncation; advances <paramref name="offset"/> past the integer.
    /// </summary>
    public static bool TryReadVarInt(ReadOnlySpan<byte> b, ref int offset, out ulong value)
    {
        value = 0;
        if (offset >= b.Length) return false;
        var first = b[offset];
        var prefix = first >> 6;
        var len = 1 << prefix; // 1, 2, 4, or 8
        if (offset + len > b.Length) return false;
        value = (ulong)(first & 0x3f);
        for (var i = 1; i < len; i++)
        {
            value = (value << 8) | b[offset + i];
        }
        offset += len;
        return true;
    }
}
