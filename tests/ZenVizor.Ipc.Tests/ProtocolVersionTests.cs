// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Ipc.Contracts;

namespace ZenVizor.Ipc.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void Current_MatchesMajorMinor()
    {
        ProtocolVersion.Current.Should().Be($"{ProtocolVersion.Major}.{ProtocolVersion.Minor}");
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.5", true)]   // forward-compatible minor on the same major
    [InlineData("2.0", false)]  // different major
    [InlineData("0.9", false)]
    [InlineData("",    false)]
    [InlineData("abc", false)]
    public void IsCompatible_GatesOnMajorOnly(string clientVersion, bool expected)
    {
        ProtocolVersion.IsCompatible(clientVersion).Should().Be(expected);
    }
}
