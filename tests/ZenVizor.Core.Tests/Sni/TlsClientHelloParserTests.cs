// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FluentAssertions;
using ZenVizor.Capture.Sni;

namespace ZenVizor.Core.Tests.Sni;

public sealed class TlsClientHelloParserTests
{
    [Fact]
    public void Tls12_ClientHello_yields_exact_sni()
    {
        var record = SniTestFixtures.BuildClientHelloRecord("outlook.office.com");

        TlsClientHelloParser.TryParse(record, out var sni).Should().BeTrue();
        sni.Should().Be("outlook.office.com");
    }

    [Fact]
    public void Tls13_no_ech_ClientHello_with_leading_extensions_yields_exact_sni()
    {
        // SNI not the first extension — two filler extensions precede it.
        var record = SniTestFixtures.BuildClientHelloRecord("www.google.com", precedingExtensions: 2);

        TlsClientHelloParser.TryParse(record, out var sni).Should().BeTrue();
        sni.Should().Be("www.google.com");
    }

    [Fact]
    public void Handshake_layer_parse_used_by_quic_yields_exact_sni()
    {
        var hs = SniTestFixtures.BuildHandshake("youtube.com");

        TlsClientHelloParser.TryParseHandshake(hs, out var sni).Should().BeTrue();
        sni.Should().Be("youtube.com");
    }

    [Fact]
    public void Application_data_record_is_rejected()
    {
        var notHandshake = new byte[] { 0x17, 0x03, 0x03, 0x00, 0x10, 1, 2, 3, 4 };

        TlsClientHelloParser.TryParse(notHandshake, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }

    [Fact]
    public void Truncated_mid_sni_returns_false_without_throwing()
    {
        var full = SniTestFixtures.BuildClientHelloRecord("outlook.office.com");
        var truncated = full[..(full.Length - 6)];

        TlsClientHelloParser.TryParse(truncated, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }

    [Fact]
    public void Empty_input_returns_false()
    {
        TlsClientHelloParser.TryParse(ReadOnlySpan<byte>.Empty, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }

    [Fact]
    public void Random_bytes_return_false_without_throwing()
    {
        var junk = Encoding.ASCII.GetBytes("this is not a tls record at all, just text");

        TlsClientHelloParser.TryParse(junk, out var sni).Should().BeFalse();
        sni.Should().BeEmpty();
    }
}
