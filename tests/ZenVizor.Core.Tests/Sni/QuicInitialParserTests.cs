// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using FluentAssertions;
using ZenVizor.Capture.Sni;

namespace ZenVizor.Core.Tests.Sni;

public sealed class QuicInitialParserTests
{
    // RFC 9001 §A.1 published vector. DCID 8394c8f03e515708 -> the client
    // Initial key/iv/hp below. Pinning these guards the key schedule so a
    // derivation bug cannot hide behind the symmetric encrypt path.
    private static readonly byte[] Rfc9001Dcid = Convert.FromHexString("8394c8f03e515708");

    [Fact]
    public void Rfc9001_a1_client_initial_key_schedule_matches_published_vector()
    {
        var keys = QuicCrypto.DeriveClientInitialKeys(Rfc9001Dcid);

        Hex(keys.Key).Should().Be("1f369613dd76d5467730efcbe3b1a22d");
        Hex(keys.Iv).Should().Be("fa044b2f42a3fd3b46fb255c");
        Hex(keys.Hp).Should().Be("9f50449e04a0e810283a1e9933adedd2");
    }

    [Fact]
    public void Closed_loop_protected_initial_decrypts_to_exact_sni()
    {
        var dcid = Convert.FromHexString("0011223344556677");
        var initial = SniTestFixtures.BuildProtectedQuicInitial(dcid, "youtube.com");

        QuicInitialParser.TryParse(initial, out var sni).Should().BeTrue();
        sni.Should().Be("youtube.com");
    }

    [Fact]
    public void Tampered_dcid_fails_aead_auth_and_yields_nothing()
    {
        var dcid = Convert.FromHexString("0011223344556677");
        var initial = SniTestFixtures.BuildProtectedQuicInitial(dcid, "youtube.com");
        var tampered = (byte[])initial.Clone();
        tampered[6] ^= 0xff; // flip a DCID byte -> wrong keys -> auth fails

        QuicInitialParser.TryParse(tampered, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }

    [Fact]
    public void Short_input_returns_false()
    {
        QuicInitialParser.TryParse(new byte[] { 0xc0, 0, 0, 0, 1 }, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }

    [Fact]
    public void Random_bytes_return_false_without_throwing()
    {
        var junk = new byte[200];
        RandomNumberGenerator.Fill(junk);

        QuicInitialParser.TryParse(junk, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }

    [Fact]
    public void Non_v1_version_is_rejected()
    {
        var dcid = Convert.FromHexString("0011223344556677");
        var initial = SniTestFixtures.BuildProtectedQuicInitial(dcid, "youtube.com");
        // Bytes 1..4 are the version (long-header, unprotected). Flip to v2.
        initial[4] = 0x02;

        QuicInitialParser.TryParse(initial, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
