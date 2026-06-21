using System.Net;
using System.Security.Cryptography;
using ZenVizor.Core.Dns;

namespace SniSpike;

/// <summary>
/// Offline validation of the QUIC Initial path (no elevation, no network).
/// Two anchors against false confidence:
///   1. Key schedule asserted against the RFC 9001 §A.1 published vector — so
///      a bug in HKDF/derivation can't hide behind a symmetric encrypt path.
///   2. Closed loop — build a protected v1 Initial carrying a real SNI, run it
///      through the production decrypt+parse, assert the SNI comes back.
/// </summary>
internal static class QuicSelfTest
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

        Console.WriteLine("QUIC v1 Initial SNI path — offline self-test");

        // 1. RFC 9001 A.1 key schedule.
        var dcid = Convert.FromHexString("8394c8f03e515708");
        var keys = QuicCrypto.DeriveClientInitialKeys(dcid);
        Check($"RFC9001 A.1 client key (got {Hex(keys.Key)})",
            Hex(keys.Key) == "1f369613dd76d5467730efcbe3b1a22d");
        Check($"RFC9001 A.1 client iv (got {Hex(keys.Iv)})",
            Hex(keys.Iv) == "fa044b2f42a3fd3b46fb255c");
        Check($"RFC9001 A.1 client hp (got {Hex(keys.Hp)})",
            Hex(keys.Hp) == "9f50449e04a0e810283a1e9933adedd2");

        // 2. Closed loop with a real-looking DCID + SNI.
        var connId = Convert.FromHexString("0011223344556677");
        var initial = BuildProtectedInitial(connId, "youtube.com");
        var got = QuicInitialParser.TryParse(initial, out var sni);
        Check($"closed-loop decrypt -> 'youtube.com' (got '{sni}')", got && sni == "youtube.com");

        // 3. Different DCID changes keys; same packet must NOT decrypt with wrong DCID.
        //    (Implicitly covered: parser derives keys from the packet's own DCID,
        //    so a tampered DCID fails AEAD auth.)
        var tampered = (byte[])initial.Clone();
        tampered[6] ^= 0xff; // flip a DCID byte
        Check("tampered DCID -> AEAD auth fails (empty)", !QuicInitialParser.TryParse(tampered, out _));

        // 4. Garbage / short inputs -> false, never throw.
        Check("reject short input", !QuicInitialParser.TryParse(new byte[] { 0xc0, 0, 0, 0, 1 }, out _));
        Check("reject random bytes", !QuicInitialParser.TryParse(RandomBytes(200), out _));

        // 5. Prove the feed into the REAL store.
        var store = new DnsResolutionStore();
        var ip = IPAddress.Parse("142.250.0.1");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (QuicInitialParser.TryParse(initial, out var feed)) store.Record(ip, feed, 300, now);
        Check("DnsResolutionStore round-trip",
            store.TryGetHostname(ip, now + 1000, out var stored) && stored == "youtube.com");

        Console.WriteLine($"\n{pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Build a header-protected, AEAD-encrypted QUIC v1 client Initial carrying
    /// a ClientHello with <paramref name="serverName"/>. The inverse of the
    /// parser, used only to exercise it end-to-end.
    /// </summary>
    private static byte[] BuildProtectedInitial(byte[] dcid, string serverName)
    {
        var keys = QuicCrypto.DeriveClientInitialKeys(dcid);
        var handshake = ClientHelloFactory.BuildHandshake(serverName);

        // CRYPTO frame + a little PADDING.
        var frames = new List<byte> { 0x06 };          // CRYPTO
        frames.AddRange(VarInt(0));                     // offset
        frames.AddRange(VarInt(handshake.Length));      // length
        frames.AddRange(handshake);
        frames.AddRange(new byte[16]);                  // PADDING (0x00)
        var plaintext = frames.ToArray();

        const int pnLength = 1;
        var length = pnLength + plaintext.Length + 16;  // pn + ciphertext + tag

        var header = new List<byte> { 0xC0 };          // long, fixed, Initial, pnLen=1
        header.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 }); // version v1
        header.Add((byte)dcid.Length);
        header.AddRange(dcid);
        header.Add(0x00);                               // SCID length 0
        header.Add(0x00);                               // token length varint 0
        header.AddRange(VarInt(length));
        var pnOffset = header.Count;
        header.Add(0x00);                               // packet number = 0 (1 byte)

        var ad = header.ToArray();                      // AD = unprotected header incl pn
        var nonce = QuicCrypto.BuildNonce(keys.Iv, 0);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(keys.Key, 16))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, ad);
        }

        // Header protection over the assembled packet.
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

    private static byte[] VarInt(int v)
    {
        if (v < 64) return new[] { (byte)v };
        if (v < 16384) return new[] { (byte)(0x40 | (v >> 8)), (byte)v };
        return new[] { (byte)(0x80 | (v >> 24)), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static byte[] RandomBytes(int n) { var b = new byte[n]; RandomNumberGenerator.Fill(b); return b; }
}
