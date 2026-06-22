// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FluentAssertions;
using ZenVizor.Capture.Sni;

namespace ZenVizor.Core.Tests.Sni;

public sealed class HttpHostParserTests
{
    [Fact]
    public void Get_request_yields_exact_host()
    {
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: neverssl.com\r\nUser-Agent: x\r\n\r\n");

        HttpHostParser.TryParse(req, out var host).Should().BeTrue();
        host.Should().Be("neverssl.com");
    }

    [Fact]
    public void Host_header_port_suffix_is_stripped()
    {
        var req = Encoding.ASCII.GetBytes(
            "POST /a HTTP/1.1\r\nHost: example.com:8080\r\n\r\n");

        HttpHostParser.TryParse(req, out var host).Should().BeTrue();
        host.Should().Be("example.com");
    }

    [Fact]
    public void Host_header_matched_case_insensitively()
    {
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nhOsT:   www.example.com  \r\n\r\n");

        HttpHostParser.TryParse(req, out var host).Should().BeTrue();
        host.Should().Be("www.example.com");
    }

    [Fact]
    public void Non_http_bytes_are_rejected()
    {
        var tlsRecordStart = new byte[] { 0x16, 0x03, 0x01, 0x02, 0x00 };

        HttpHostParser.TryParse(tlsRecordStart, out var host).Should().BeFalse();
        host.Should().BeEmpty();
    }

    [Fact]
    public void Request_without_host_header_returns_false()
    {
        var req = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nUser-Agent: x\r\n\r\n");

        HttpHostParser.TryParse(req, out var host).Should().BeFalse();
        host.Should().BeEmpty();
    }
}
