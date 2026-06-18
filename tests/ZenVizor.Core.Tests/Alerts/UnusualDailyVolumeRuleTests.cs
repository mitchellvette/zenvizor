using FluentAssertions;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Alerts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests.Alerts;

/// <summary>
/// Unit tests for <see cref="UnusualDailyVolumeRule"/>. Covers the
/// date-roll gate, the k × median formula, the 50 MB delta floor, the
/// 14-day baseline minimum, and the <see cref="IResettableRollupRule"/>
/// QA reset path.
/// </summary>
public sealed class UnusualDailyVolumeRuleTests
{
    // Pick a UTC midnight anchor so day arithmetic stays clean.
    private static readonly long TodayUtcMs = BucketAligner.AlignToDay(1_780_704_000_000L);
    private static readonly long YesterdayUtcMs = TodayUtcMs - BucketAligner.DayMs;

    private const long Mb = 1024L * 1024L;

    private static FlushAlertEvent EvtAt(long flushTimeUnixMs) =>
        new(FlushTimeUnixMs: flushTimeUnixMs,
            FlushIntervalMs: 0,
            Connections:     Array.Empty<FlushConnectionState>());

    private static List<DailyVolumeRow> BaselineFor(int appId, long perDayBytes, string image = "chrome.exe",
        string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe")
    {
        // 14 baseline days (days -2 through -15 from today UTC) +
        // optional yesterday row added by callers.
        var rows = new List<DailyVolumeRow>();
        for (var i = 2; i <= 15; i++)
        {
            rows.Add(new DailyVolumeRow(
                AppId:      appId,
                ImageName:  image,
                ImagePath:  path,
                DayUnixMs:  TodayUtcMs - i * BucketAligner.DayMs,
                BytesUp:    perDayBytes / 2,
                BytesDown:  perDayBytes / 2));
        }
        return rows;
    }

    [Fact]
    public void Evaluate_YesterdayClearsBothKAndFloor_Raises()
    {
        // Baseline: 100 MB/day for 14 days; yesterday: 300 MB.
        // k=2.5 → threshold 250 MB; delta 200 MB > 50 MB floor.
        var rows = BaselineFor(appId: 7, perDayBytes: 100 * Mb);
        rows.Add(new DailyVolumeRow(7, "chrome.exe", @"C:\Chrome\chrome.exe", YesterdayUtcMs,
                                    150 * Mb, 150 * Mb));

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 25),
            query: (_, _) => rows);

        var results = rule.Evaluate(EvtAt(TodayUtcMs + 1_000)).ToList();

        results.Should().ContainSingle();
        var (req, detail) = results[0];
        req.Type.Should().Be(AlertType.UnusualDailyVolume);
        req.Severity.Should().Be(NotableSeverity.Warning);
        req.SourceMonitor.Should().Be(SourceMonitor.Rollup);
        req.EntityKind.Should().Be(AlertEntityKind.App);
        req.EntityRef.Should().Be("7");
        detail.Should().Contain("chrome.exe");
        detail.Should().Contain("300 MB");
        detail.Should().Contain("100 MB typical");
        detail.Should().Contain("3.0x baseline");
        detail.Should().Contain("threshold is 2.5x");
    }

    [Fact]
    public void Evaluate_BelowKThreshold_DoesNotFire()
    {
        // Baseline 100 MB; yesterday 200 MB; k=2.5 → threshold 250 MB.
        var rows = BaselineFor(appId: 7, perDayBytes: 100 * Mb);
        rows.Add(new DailyVolumeRow(7, "chrome.exe", "", YesterdayUtcMs, 100 * Mb, 100 * Mb));

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 25),
            query: (_, _) => rows);

        rule.Evaluate(EvtAt(TodayUtcMs)).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_DeltaBelowFloor_DoesNotFire()
    {
        // Low-variance app: median 10 MB, yesterday 30 MB (3x clears
        // k=2.5 threshold of 25 MB), but the absolute delta is only
        // 20 MB — below the 50 MB hard floor.
        var rows = BaselineFor(appId: 7, perDayBytes: 10 * Mb);
        rows.Add(new DailyVolumeRow(7, "tiny.exe", "", YesterdayUtcMs, 15 * Mb, 15 * Mb));

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 25),
            query: (_, _) => rows);

        rule.Evaluate(EvtAt(TodayUtcMs)).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_InsufficientBaselineDays_DoesNotFire()
    {
        // Only 13 baseline days — below the 14-day minimum.
        var rows = new List<DailyVolumeRow>();
        for (var i = 2; i <= 14; i++)
        {
            rows.Add(new DailyVolumeRow(7, "chrome.exe", "", TodayUtcMs - i * BucketAligner.DayMs,
                                        50 * Mb, 50 * Mb));
        }
        rows.Add(new DailyVolumeRow(7, "chrome.exe", "", YesterdayUtcMs, 500 * Mb, 500 * Mb));

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(),
            query: (_, _) => rows);

        rule.Evaluate(EvtAt(TodayUtcMs)).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NoYesterdayRow_DoesNotFire()
    {
        // Baseline present but yesterday's row is missing entirely.
        var rows = BaselineFor(appId: 7, perDayBytes: 100 * Mb);

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(),
            query: (_, _) => rows);

        rule.Evaluate(EvtAt(TodayUtcMs)).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_SameDay_OnlyEvaluatesOnce()
    {
        var rows = BaselineFor(appId: 7, perDayBytes: 100 * Mb);
        rows.Add(new DailyVolumeRow(7, "chrome.exe", "", YesterdayUtcMs, 200 * Mb, 200 * Mb));

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 25),
            query: (_, _) => rows);

        // First evaluation fires.
        rule.Evaluate(EvtAt(TodayUtcMs + 1_000)).Count().Should().Be(1);
        // Second evaluation on the same UTC day is gated out.
        rule.Evaluate(EvtAt(TodayUtcMs + 60_000)).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NextDay_FiresAgain()
    {
        var rows = BaselineFor(appId: 7, perDayBytes: 100 * Mb);
        rows.Add(new DailyVolumeRow(7, "chrome.exe", "", YesterdayUtcMs, 200 * Mb, 200 * Mb));

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 25),
            query: (from, to) => rows.Where(r => r.DayUnixMs >= from && r.DayUnixMs < to).ToList());

        rule.Evaluate(EvtAt(TodayUtcMs + 1_000)).Count().Should().Be(1);

        // Tomorrow's flush. Drop the date gate; baseline shifts forward
        // by one day. With the same per-day base, the rule re-evaluates
        // and fires again (yesterday from the tomorrow frame = today
        // from the original frame).
        rows.Add(new DailyVolumeRow(7, "chrome.exe", "", TodayUtcMs, 200 * Mb, 200 * Mb));
        rule.Evaluate(EvtAt(TodayUtcMs + BucketAligner.DayMs + 1_000)).Count().Should().Be(1);
    }

    [Fact]
    public void ResetLastEvalDate_AllowsRefireOnSameDay()
    {
        var rows = BaselineFor(appId: 7, perDayBytes: 100 * Mb);
        rows.Add(new DailyVolumeRow(7, "chrome.exe", "", YesterdayUtcMs, 200 * Mb, 200 * Mb));

        var rule = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 25),
            query: (_, _) => rows);

        rule.Evaluate(EvtAt(TodayUtcMs + 1_000)).Count().Should().Be(1);
        rule.Evaluate(EvtAt(TodayUtcMs + 60_000)).Should().BeEmpty();

        // QA hook: reset the date gate, then evaluate same-day again.
        rule.ResetLastEvalDate();
        rule.Evaluate(EvtAt(TodayUtcMs + 90_000)).Count().Should().Be(1);
    }

    [Fact]
    public void Evaluate_ScalesWithKSetting()
    {
        // BaselineFor splits its perDayBytes equally between BytesUp and
        // BytesDown; the rule sums them so per-day total == perDayBytes.
        var rows = BaselineFor(appId: 7, perDayBytes: 100 * Mb);
        // Yesterday total = 200 MB (100 up + 100 down).
        rows.Add(new DailyVolumeRow(7, "chrome.exe", "", YesterdayUtcMs, 100 * Mb, 100 * Mb));

        // At k=2.5, threshold = 250 MB. Yesterday (200 MB) is below. No fire.
        var rule25 = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 25),
            query: (_, _) => rows);
        rule25.Evaluate(EvtAt(TodayUtcMs)).Should().BeEmpty();

        // At k=1.5, threshold = 150 MB. Yesterday (200 MB) clears it; delta
        // 100 MB > 50 MB floor. Fires.
        var rule15 = new UnusualDailyVolumeRule(
            settings: new StaticAlertSettingsLookup(unusualDailyVolumeKTimesTen: 15),
            query: (_, _) => rows);
        rule15.Evaluate(EvtAt(TodayUtcMs)).Count().Should().Be(1);
    }

    [Fact]
    public void CooldownMs_Is24Hours()
    {
        new UnusualDailyVolumeRule(new StaticAlertSettingsLookup(), (_, _) => Array.Empty<DailyVolumeRow>())
            .CooldownMs.Should().Be((long)TimeSpan.FromHours(24).TotalMilliseconds);
    }
}
