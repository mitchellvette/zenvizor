using FluentAssertions;
using Xunit;
using ZenVizor.Core.Aggregation;

namespace ZenVizor.Core.Tests;

public class SeriesDownsamplerTests
{
    [Fact]
    public void Downsample_pair_passes_through_when_under_max()
    {
        var up = MakeSeries(start: 0, count: 100, valuePerBucket: 5);
        var down = MakeSeries(start: 0, count: 100, valuePerBucket: 7);

        var (rsUp, rsDown) = SeriesDownsampler.DownsamplePair(up, down, maxBuckets: 240);

        rsUp.Should().BeSameAs(up);
        rsDown.Should().BeSameAs(down);
    }

    [Fact]
    public void Downsample_pair_preserves_total_value_for_each_series()
    {
        var up = MakeSeries(start: 0, count: 1440, valuePerBucket: 13);
        var down = MakeSeries(start: 0, count: 1440, valuePerBucket: 17);
        var upTotal = up.Sum(b => b.Value);
        var downTotal = down.Sum(b => b.Value);

        var (rsUp, rsDown) = SeriesDownsampler.DownsamplePair(up, down, maxBuckets: 240);

        rsUp.Sum(b => b.Value).Should().Be(upTotal,
            because: "downsampling must not lose or invent bytes (Up).");
        rsDown.Sum(b => b.Value).Should().Be(downTotal,
            because: "downsampling must not lose or invent bytes (Down).");
    }

    [Fact]
    public void Downsample_pair_caps_at_max_buckets()
    {
        var up = MakeSeries(start: 0, count: 1440, valuePerBucket: 1);
        var down = MakeSeries(start: 0, count: 1440, valuePerBucket: 1);

        var (rsUp, rsDown) = SeriesDownsampler.DownsamplePair(up, down, maxBuckets: 240);

        rsUp.Count.Should().BeLessThanOrEqualTo(240);
        rsDown.Count.Should().BeLessThanOrEqualTo(240);
    }

    [Fact]
    public void Downsample_pair_uses_shared_factor_so_series_stay_aligned()
    {
        // Up is much longer than down; both must be downsampled with the same
        // factor so a stacked / overlay chart still pairs them by timestamp.
        var up = MakeSeries(start: 1_000_000L, count: 1000, valuePerBucket: 1);
        var down = MakeSeries(start: 1_000_000L, count: 100, valuePerBucket: 1);

        var (rsUp, rsDown) = SeriesDownsampler.DownsamplePair(up, down, maxBuckets: 240);

        // First grouped bucket of each series shares the same start.
        rsUp[0].TimestampUnixMs.Should().Be(rsDown[0].TimestampUnixMs);
    }

    [Fact]
    public void Downsample_pair_uses_first_bucket_start_as_group_timestamp()
    {
        // factor will be ceil(720/240) = 3.
        var series = MakeSeries(start: 0L, count: 720, valuePerBucket: 1, stepMs: 60_000L);

        var (result, _) = SeriesDownsampler.DownsamplePair(series, series, maxBuckets: 240);

        result[0].TimestampUnixMs.Should().Be(0L);
        result[1].TimestampUnixMs.Should().Be(60_000L * 3, because: "second group starts at index 3.");
        result[2].TimestampUnixMs.Should().Be(60_000L * 6);
    }

    [Fact]
    public void Downsample_pair_handles_trailing_partial_group()
    {
        // 7 buckets, factor 3 → groups of 3 + 3 + 1.
        var series = new[]
        {
            new SeriesBucket(0,  10),
            new SeriesBucket(1,  20),
            new SeriesBucket(2,  30),
            new SeriesBucket(3,  40),
            new SeriesBucket(4,  50),
            new SeriesBucket(5,  60),
            new SeriesBucket(6,  70),
        };

        var result = SeriesDownsampler.DownsampleOne(series, factor: 3);

        result.Should().BeEquivalentTo(new[]
        {
            new SeriesBucket(0, 60),   // 10+20+30
            new SeriesBucket(3, 150),  // 40+50+60
            new SeriesBucket(6, 70),   // 70 alone
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void DownsampleOne_rejects_factor_less_than_1()
    {
        var series = new[] { new SeriesBucket(0, 1) };

        var act = () => SeriesDownsampler.DownsampleOne(series, factor: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DownsamplePair_rejects_maxBuckets_less_than_1()
    {
        var series = new[] { new SeriesBucket(0, 1) };

        var act = () => SeriesDownsampler.DownsamplePair(series, series, maxBuckets: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static SeriesBucket[] MakeSeries(
        long start, int count, long valuePerBucket, long stepMs = 1L)
    {
        var arr = new SeriesBucket[count];
        for (int i = 0; i < count; i++)
        {
            arr[i] = new SeriesBucket(start + i * stepMs, valuePerBucket);
        }
        return arr;
    }
}
