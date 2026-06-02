using FluentAssertions;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests;

public sealed class RollingActivityWindowTests
{
    private static readonly AppIdentity ChromeApp = new(
        ImagePath: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        ImageName: "chrome.exe",
        Publisher: "Google LLC",
        SignatureStatus: "Signed",
        IsUserWritablePath: false);

    private static readonly AppIdentity Svchost = new(
        ImagePath: @"C:\Windows\System32\svchost.exe",
        ImageName: "svchost.exe",
        Publisher: "Microsoft Corporation",
        SignatureStatus: "Signed",
        IsUserWritablePath: false);

    [Fact]
    public void TakeSnapshot_BeforeFirstFlush_ReturnsEmptyWithZeroWindow()
    {
        var window = new RollingActivityWindow();

        var snap = window.TakeSnapshot(EmptyPartial(), nowUnixMs: 1_000);

        snap.WindowSeconds.Should().Be(0.0);
        snap.Apps.Should().BeEmpty();
        snap.CapturedAtUnixMs.Should().Be(1_000);
    }

    [Fact]
    public void TakeSnapshot_BeforeFirstFlush_IgnoresPartialBytes()
    {
        // Cold-start invariant: even if the partial accumulator has bytes, the
        // snapshot reports nothing until at least one flush has sealed a bucket.
        var window = new RollingActivityWindow();
        var partial = new Dictionary<ActivityKey, ActivityBytes>
        {
            [new ActivityKey(ChromeApp, null)] = new(100, 200),
        };

        var snap = window.TakeSnapshot(partial, nowUnixMs: 2_500);

        snap.WindowSeconds.Should().Be(0.0);
        snap.Apps.Should().BeEmpty();
    }

    [Fact]
    public void TakeSnapshot_JustAfterFlush_DenominatorEqualsBucketSpan()
    {
        // Bucket covers [0, 5000]. Snapshot taken at t=5000 (immediately after seal).
        // WindowSeconds = 5, partial empty, rate = bucket_bytes / 5.
        var window = new RollingActivityWindow();
        window.OnFlush(
            bucketPerApp: new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(ChromeApp, null)] = new(BytesUp: 5_000, BytesDown: 50_000),
            },
            bucketStartUnixMs: 0,
            bucketEndUnixMs: 5_000);

        var snap = window.TakeSnapshot(EmptyPartial(), nowUnixMs: 5_000);

        snap.WindowSeconds.Should().Be(5.0);
        snap.Apps.Should().ContainSingle();
        var chrome = snap.Apps.Single();
        chrome.ImageName.Should().Be("chrome.exe");
        chrome.BytesUpTotal.Should().Be(5_000);
        chrome.BytesDownTotal.Should().Be(50_000);
        chrome.BytesUpPerSec.Should().Be(1_000.0);
        chrome.BytesDownPerSec.Should().Be(10_000.0);
    }

    [Fact]
    public void TakeSnapshot_PartialAccumulatesIntoFullSpanRate()
    {
        // Option-A rate math: WindowSeconds grows with the partial elapsed.
        // Bucket [0,5000]: chrome up=5_000. Partial t=5000→7000: chrome up=2_000 more.
        // Full span = 7s. Total up = 7_000. Rate up = 7000/7 = 1000 B/s.
        var window = new RollingActivityWindow();
        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(ChromeApp, null)] = new(5_000, 0),
            },
            bucketStartUnixMs: 0,
            bucketEndUnixMs: 5_000);

        var partial = new Dictionary<ActivityKey, ActivityBytes>
        {
            [new ActivityKey(ChromeApp, null)] = new(2_000, 0),
        };

        var snap = window.TakeSnapshot(partial, nowUnixMs: 7_000);

        snap.WindowSeconds.Should().Be(7.0);
        var chrome = snap.Apps.Single();
        chrome.BytesUpTotal.Should().Be(7_000);
        chrome.BytesUpPerSec.Should().Be(1_000.0);
    }

    [Fact]
    public void TakeSnapshot_AppOnlyInPartial_StillAppearsWithCorrectRate()
    {
        // New app shows up after the last flush — present only in the partial.
        var window = new RollingActivityWindow();
        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(ChromeApp, null)] = new(1_000, 1_000),
            },
            bucketStartUnixMs: 0,
            bucketEndUnixMs: 5_000);

        var partial = new Dictionary<ActivityKey, ActivityBytes>
        {
            [new ActivityKey(Svchost, "Dnscache")] = new(0, 250),
        };

        var snap = window.TakeSnapshot(partial, nowUnixMs: 10_000);

        snap.WindowSeconds.Should().Be(10.0);
        snap.Apps.Should().HaveCount(2);

        var dnscache = snap.Apps.Single(a => a.ImageName == "svchost.exe");
        dnscache.HostedServices.Should().Be("Dnscache");
        dnscache.BytesDownTotal.Should().Be(250);
        dnscache.BytesDownPerSec.Should().Be(25.0);
        dnscache.BytesUpTotal.Should().Be(0);
    }

    [Fact]
    public void TakeSnapshot_TwoSvchostPidsWithDifferentServices_StayDistinct()
    {
        // CLAUDE.md invariant #5: distinct svchost PIDs hosting different
        // service sets do NOT collapse. Two distinct ActivityKey values.
        var window = new RollingActivityWindow();
        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(Svchost, "Dnscache")]  = new(0, 500),
                [new ActivityKey(Svchost, "DiagTrack")] = new(100, 0),
            },
            bucketStartUnixMs: 0,
            bucketEndUnixMs: 5_000);

        var snap = window.TakeSnapshot(EmptyPartial(), nowUnixMs: 5_000);

        snap.Apps.Should().HaveCount(2);
        snap.Apps.Single(a => a.HostedServices == "Dnscache").BytesDownTotal.Should().Be(500);
        snap.Apps.Single(a => a.HostedServices == "DiagTrack").BytesUpTotal.Should().Be(100);
    }

    [Fact]
    public void TakeSnapshot_SameAppKeyInBucketAndPartial_CombinesTotals()
    {
        var window = new RollingActivityWindow();
        var key = new ActivityKey(ChromeApp, null);

        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [key] = new(BytesUp: 1_000, BytesDown: 4_000),
            },
            bucketStartUnixMs: 0,
            bucketEndUnixMs: 5_000);

        var partial = new Dictionary<ActivityKey, ActivityBytes>
        {
            [key] = new(BytesUp: 500, BytesDown: 6_000),
        };

        var snap = window.TakeSnapshot(partial, nowUnixMs: 8_000);

        snap.WindowSeconds.Should().Be(8.0);
        var chrome = snap.Apps.Single();
        chrome.BytesUpTotal.Should().Be(1_500);
        chrome.BytesDownTotal.Should().Be(10_000);
        chrome.BytesUpPerSec.Should().BeApproximately(187.5, 0.0001);
        chrome.BytesDownPerSec.Should().BeApproximately(1_250.0, 0.0001);
    }

    [Fact]
    public void TakeSnapshot_ZeroByteEntriesAreOmitted()
    {
        var window = new RollingActivityWindow();
        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(ChromeApp, null)] = new(0, 0),
                [new ActivityKey(Svchost, "Dnscache")] = new(0, 100),
            },
            bucketStartUnixMs: 0,
            bucketEndUnixMs: 5_000);

        var snap = window.TakeSnapshot(EmptyPartial(), nowUnixMs: 5_000);

        snap.Apps.Should().ContainSingle().Which.ImageName.Should().Be("svchost.exe");
    }

    [Fact]
    public void TakeSnapshot_TwoConsecutiveFlushes_OnlyMostRecentBucketCounts()
    {
        // The window is "previous completed bucket" — older buckets do NOT
        // contribute. Otherwise the sliding-window invariant would break.
        var window = new RollingActivityWindow();

        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(ChromeApp, null)] = new(BytesUp: 99_999, BytesDown: 0),
            },
            bucketStartUnixMs: 0,
            bucketEndUnixMs: 5_000);

        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(ChromeApp, null)] = new(BytesUp: 100, BytesDown: 0),
            },
            bucketStartUnixMs: 5_000,
            bucketEndUnixMs: 10_000);

        var snap = window.TakeSnapshot(EmptyPartial(), nowUnixMs: 10_000);

        snap.WindowSeconds.Should().Be(5.0);
        snap.Apps.Single().BytesUpTotal.Should().Be(100);
    }

    [Fact]
    public void TakeSnapshot_NowEqualsBucketStart_DoesNotDivideByZero()
    {
        // Pathological: snapshot at the exact ms of the bucket start.
        // WindowMs clamps to 1, so WindowSeconds = 0.001 — finite rate, no NaN.
        var window = new RollingActivityWindow();
        window.OnFlush(
            new Dictionary<ActivityKey, ActivityBytes>
            {
                [new ActivityKey(ChromeApp, null)] = new(1, 0),
            },
            bucketStartUnixMs: 5_000,
            bucketEndUnixMs: 5_000);

        var snap = window.TakeSnapshot(EmptyPartial(), nowUnixMs: 5_000);

        snap.WindowSeconds.Should().Be(0.001);
        snap.Apps.Single().BytesUpPerSec.Should().Be(1_000.0);
    }

    private static IReadOnlyDictionary<ActivityKey, ActivityBytes> EmptyPartial() =>
        new Dictionary<ActivityKey, ActivityBytes>();
}
