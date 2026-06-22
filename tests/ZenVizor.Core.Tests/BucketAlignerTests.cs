// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Core.Aggregation;

namespace ZenVizor.Core.Tests;

public sealed class BucketAlignerTests
{
    [Theory]
    [InlineData(0,            60, 0)]
    [InlineData(59_999,       60, 0)]
    [InlineData(60_000,       60, 60_000)]
    [InlineData(60_001,       60, 60_000)]
    [InlineData(123_456_000,  60, 123_420_000)]   // 2:17:36 -> aligned to 2:17:00
    public void AlignToBucket_SnapsDownToBoundary(long input, int width, long expected)
    {
        BucketAligner.AlignToBucket(input, width).Should().Be(expected);
    }

    [Fact]
    public void AlignToBucket_DefaultWidthIsSixtySeconds()
    {
        BucketAligner.AlignToBucket(123_456_789)
            .Should().Be(BucketAligner.AlignToBucket(123_456_789, BucketAligner.DefaultBucketSeconds));
        BucketAligner.DefaultBucketSeconds.Should().Be(60);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AlignToBucket_NonPositiveWidthThrows(int width)
    {
        var act = () => BucketAligner.AlignToBucket(1000, width);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AlignToBucket_HandlesNegativeTimestamp()
    {
        // Pre-epoch -- not expected in practice, but the math should still floor correctly.
        BucketAligner.AlignToBucket(-1, 60).Should().Be(-60_000);
        BucketAligner.AlignToBucket(-60_000, 60).Should().Be(-60_000);
        BucketAligner.AlignToBucket(-60_001, 60).Should().Be(-120_000);
    }
}
