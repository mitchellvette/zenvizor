// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using FluentAssertions;
using ZenVizor.Core.Classification;
using ZenVizor.Core.Observations;

namespace ZenVizor.Core.Tests;

public sealed class RemoteAddressClassifierTests
{
    [Theory]
    // IPv4 RFC1918
    [InlineData("10.0.0.1",        RemoteClass.Local)]
    [InlineData("10.255.255.254",  RemoteClass.Local)]
    [InlineData("172.16.0.1",      RemoteClass.Local)]
    [InlineData("172.31.255.254",  RemoteClass.Local)]
    [InlineData("192.168.0.1",     RemoteClass.Local)]
    [InlineData("192.168.255.254", RemoteClass.Local)]
    // IPv4 boundaries that should NOT be Local
    [InlineData("172.15.0.1",      RemoteClass.Wan)]
    [InlineData("172.32.0.1",      RemoteClass.Wan)]
    [InlineData("11.0.0.1",        RemoteClass.Wan)]
    [InlineData("192.167.0.1",     RemoteClass.Wan)]
    // IPv4 loopback + link-local
    [InlineData("127.0.0.1",       RemoteClass.Local)]
    [InlineData("169.254.1.1",     RemoteClass.Local)]
    // IPv4 public
    [InlineData("8.8.8.8",         RemoteClass.Wan)]
    [InlineData("1.1.1.1",         RemoteClass.Wan)]
    public void Classify_IPv4(string input, RemoteClass expected)
    {
        RemoteAddressClassifier.Classify(IPAddress.Parse(input))
            .Should().Be(expected);
    }

    [Theory]
    // IPv6 loopback
    [InlineData("::1",               RemoteClass.Local)]
    // IPv6 link-local fe80::/10
    [InlineData("fe80::1",           RemoteClass.Local)]
    [InlineData("febf::1",           RemoteClass.Local)]
    // IPv6 ULA fc00::/7 (both fc:: and fd:: high bytes)
    [InlineData("fc00::1",           RemoteClass.Local)]
    [InlineData("fd12:3456::1",      RemoteClass.Local)]
    [InlineData("fdff:ffff:ffff::1", RemoteClass.Local)]
    // IPv6 GUA
    [InlineData("2001:4860:4860::8888", RemoteClass.Wan)]
    [InlineData("2606:4700:4700::1111", RemoteClass.Wan)]
    // IPv4-mapped IPv6 follows the v4 rule
    [InlineData("::ffff:192.168.1.1", RemoteClass.Local)]
    [InlineData("::ffff:8.8.8.8",     RemoteClass.Wan)]
    public void Classify_IPv6(string input, RemoteClass expected)
    {
        RemoteAddressClassifier.Classify(IPAddress.Parse(input))
            .Should().Be(expected);
    }

    [Fact]
    public void Classify_NullThrows()
    {
        var act = () => RemoteAddressClassifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
