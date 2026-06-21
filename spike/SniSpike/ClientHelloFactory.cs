namespace SniSpike;

/// <summary>
/// Spike test helper — builds a minimal-but-valid TLS ClientHello around a
/// given SNI. Lengths are computed, so fixtures stay correct under edits.
/// </summary>
internal static class ClientHelloFactory
{
    /// <summary>Handshake message only (0x01 + len + body) — what QUIC CRYPTO carries.</summary>
    public static byte[] BuildHandshake(string serverName, int precedingExtensions = 0)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(serverName);

        var entry = new List<byte> { 0x00 };           // name_type host_name
        entry.AddRange(U16(nameBytes.Length));
        entry.AddRange(nameBytes);
        var sniBody = new List<byte>();
        sniBody.AddRange(U16(entry.Count));            // server_name_list length
        sniBody.AddRange(entry);

        var extensions = new List<byte>();
        for (var i = 0; i < precedingExtensions; i++)
        {
            extensions.AddRange(U16(0x002b));          // supported_versions (filler)
            extensions.AddRange(U16(2));
            extensions.AddRange(new byte[] { 0x03, 0x04 });
        }
        extensions.AddRange(U16(0x0000));              // server_name type
        extensions.AddRange(U16(sniBody.Count));
        extensions.AddRange(sniBody);

        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 });      // client_version
        body.AddRange(new byte[32]);                   // random
        body.Add(0x00);                                // session_id length 0
        body.AddRange(U16(2));                          // cipher_suites length
        body.AddRange(new byte[] { 0x13, 0x01 });
        body.Add(0x01);                                // compression methods length
        body.Add(0x00);
        body.AddRange(U16(extensions.Count));
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01 };       // ClientHello
        handshake.AddRange(U24(body.Count));
        handshake.AddRange(body);
        return handshake.ToArray();
    }

    /// <summary>Full TLS record wrapping the handshake — what TCP/443 carries.</summary>
    public static byte[] BuildRecord(string serverName, int precedingExtensions = 0)
    {
        var hs = BuildHandshake(serverName, precedingExtensions);
        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.AddRange(U16(hs.Length));
        record.AddRange(hs);
        return record.ToArray();
    }

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U24(int v) => new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}
