using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Phase 5b daily-report aggregator correctness. Each test seeds the
/// SQLite tiers and asserts the DTO shape coming out of
/// <see cref="DailyReportRepository.GetDailyReport"/>. All tests use
/// <see cref="TimeZoneInfo.Utc"/> as the "local" timezone so the local-day
/// window math is independent of the host system's timezone — DST behaviour
/// is documented as a known boundary, not exercised here.
/// </summary>
public sealed class DailyReportRepositoryTests : IDisposable
{
    private static readonly DateOnly ReportDate = new(2026, 6, 8);
    private static readonly long DayStartMs = new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long DayEndMs   = DayStartMs + 86_400_000L;
    private const long Hour = 3_600_000L;
    private const long Day  = 86_400_000L;

    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly DailyReportRepository _repo;

    public DailyReportRepositoryTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-dailyreport-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
        _repo = new DailyReportRepository(_connections);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private DailyReportResult Run(AnchorMode anchor = AnchorMode.Avg7d) =>
        _repo.GetDailyReport(ReportDate, anchor, null, TimeZoneInfo.Utc);

    // ─── Empty day ─────────────────────────────────────────────────────────

    [Fact]
    public void EmptyDay_ReturnsZeroPayload()
    {
        var result = Run();

        result.Hero.TotalUpBytes.Should().Be(0);
        result.Hero.TotalDownBytes.Should().Be(0);
        result.Hero.WanRatio.Should().Be(0);
        // Empty apps table → no observations yet → baseline-days-available is 0
        // (treatment (a) suppression in the UI; chips hide, "Comparisons
        // unlock on…" caption surfaces).
        result.Hero.BaselineDaysAvailable.Should().Be(0);
        result.HourlyTraffic.Should().HaveCount(24);
        result.HourlyTraffic.Should().OnlyContain(p => p.BytesUp == 0 && p.BytesDown == 0);
        result.TopApps.Should().BeEmpty();
        result.UncommonTalkers.Should().BeEmpty();
        result.Notable.Should().BeEmpty();
    }

    // ─── Hero totals + WAN/Local ratio ─────────────────────────────────────

    [Fact]
    public void Hero_TotalsAndRatio_AggregateAcrossApps()
    {
        SeedApp(1, "a.exe");
        SeedApp(2, "b.exe");
        InsertHourly(1, DayStartMs + 10 * Hour, bytesUp: 100, bytesDown: 200, "Wan");
        InsertHourly(2, DayStartMs + 15 * Hour, bytesUp:  50, bytesDown:  50, "Local");

        var result = Run();

        result.Hero.TotalUpBytes.Should().Be(150);
        result.Hero.TotalDownBytes.Should().Be(250);
        // WAN = 100+200 = 300; Local = 50+50 = 100; ratio 300/(300+100) = 0.75.
        result.Hero.WanRatio.Should().BeApproximately(0.75, 0.001);
        result.Hero.LocalRatio.Should().BeApproximately(0.25, 0.001);
    }

    // ─── Sparkline series (24 hour points) ────────────────────────────────

    [Fact]
    public void HourlyTraffic_Has24Points_AndBucketsByLocalHour()
    {
        SeedApp(1, "a.exe");
        InsertHourly(1, DayStartMs + 8 * Hour,  bytesUp: 0, bytesDown: 500, "Wan");
        InsertHourly(1, DayStartMs + 19 * Hour, bytesUp: 0, bytesDown: 999, "Wan");

        var result = Run();

        result.HourlyTraffic.Should().HaveCount(24);
        result.HourlyTraffic[8].BytesDown.Should().Be(500);
        result.HourlyTraffic[19].BytesDown.Should().Be(999);
        // Other hours zero.
        result.HourlyTraffic.Where((_, h) => h != 8 && h != 19)
            .Should().OnlyContain(p => p.BytesDown == 0);
    }

    // ─── Top apps order + svchost bracket suffix ───────────────────────────

    [Fact]
    public void TopApps_OrderedByTotalBytesDesc()
    {
        SeedApp(1, "low.exe");
        SeedApp(2, "high.exe");
        InsertHourly(1, DayStartMs + 1 * Hour, bytesUp: 1,    bytesDown: 1,    "Wan");
        InsertHourly(2, DayStartMs + 2 * Hour, bytesUp: 1000, bytesDown: 1000, "Wan");

        var result = Run();

        result.TopApps.Should().HaveCount(2);
        result.TopApps[0].ImageName.Should().Be("high.exe");
        result.TopApps[1].ImageName.Should().Be("low.exe");
    }

    [Fact]
    public void TopApps_SvchostRow_AppendsBracketedServiceList()
    {
        SeedApp(1, "svchost.exe");
        SeedSession(11, appId: 1, startTime: DayStartMs + 1 * Hour, hostedServices: "Dnscache, NlaSvc");
        InsertHourly(1, DayStartMs + 2 * Hour, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run();

        result.TopApps.Should().ContainSingle();
        result.TopApps[0].ImageName.Should().Be("svchost.exe [Dnscache, NlaSvc]");
    }

    [Fact]
    public void TopApps_NonSvchost_NoBracketEvenWithHostedServices()
    {
        SeedApp(1, "chrome.exe");
        SeedSession(11, appId: 1, startTime: DayStartMs + 1 * Hour, hostedServices: "noise");
        InsertHourly(1, DayStartMs + 2 * Hour, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run();
        result.TopApps[0].ImageName.Should().Be("chrome.exe");
    }

    // ─── NewToday heuristic ────────────────────────────────────────────────

    [Fact]
    public void NewToday_FiresWhenPublisherFirstSeenIsInWindow_AndHasTrafficToday()
    {
        SeedApp(1, "fresh.exe", publisher: "Brand New Co.", firstSeen: DayStartMs + 9 * Hour);
        InsertHourly(1, DayStartMs + 9 * Hour, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run();
        var newToday = result.UncommonTalkers.Where(t => t.Category == UncommonCategory.NewToday).ToList();

        newToday.Should().ContainSingle();
        newToday[0].ImageName.Should().Be("fresh.exe");
        newToday[0].Publisher.Should().Be("Brand New Co.");
        newToday[0].Reason.Should().Contain("First publisher seen on this machine");
        newToday[0].Reason.Should().Contain("09:00");
    }

    [Fact]
    public void NewToday_DoesNotFire_WhenPublisherWasSeenEarlier()
    {
        SeedApp(1, "old.exe",    publisher: "Returning Co.", firstSeen: DayStartMs - 30 * Day);
        SeedApp(2, "second.exe", publisher: "Returning Co.", firstSeen: DayStartMs + 1 * Hour);
        InsertHourly(2, DayStartMs + 2 * Hour, bytesUp: 100, bytesDown: 0, "Wan");

        var result = Run();
        result.UncommonTalkers.Where(t => t.Category == UncommonCategory.NewToday).Should().BeEmpty();
    }

    [Fact]
    public void NewToday_DoesNotFire_WhenPublisherIsNull()
    {
        SeedApp(1, "anon.exe", publisher: null, firstSeen: DayStartMs + 1 * Hour);
        InsertHourly(1, DayStartMs + 2 * Hour, bytesUp: 100, bytesDown: 0, "Wan");

        var result = Run();
        result.UncommonTalkers.Where(t => t.Category == UncommonCategory.NewToday).Should().BeEmpty();
    }

    // ─── UnusualVolume heuristic ──────────────────────────────────────────

    [Fact]
    public void UnusualVolume_FiresWhenTodayExceedsThreeXMedian()
    {
        SeedApp(1, "spiky.exe", publisher: "Spiky Co.", firstSeen: DayStartMs - 30 * Day);
        // Today: 10 MB up
        InsertHourly(1, DayStartMs + 5 * Hour, bytesUp: 10_000_000L, bytesDown: 0, "Wan");
        // Baseline 4 non-zero days: each ~1 MB up. Median = 1MB. 10M / 1M = 10x — fires.
        for (var d = 1; d <= 4; d++)
            InsertDaily(1, DayStartMs - d * Day, bytesUp: 1_000_000L, bytesDown: 0, "Wan");

        var result = Run();
        var unusual = result.UncommonTalkers.Where(t => t.Category == UncommonCategory.UnusualVolume).ToList();

        unusual.Should().ContainSingle();
        unusual[0].ImageName.Should().Be("spiky.exe");
        unusual[0].Reason.Should().StartWith("Uploaded ");
        unusual[0].Reason.Should().Contain("× the 7-day median");
    }

    [Fact]
    public void UnusualVolume_DoesNotFire_BelowThreshold()
    {
        SeedApp(1, "steady.exe", publisher: "Steady Co.", firstSeen: DayStartMs - 30 * Day);
        InsertHourly(1, DayStartMs + 5 * Hour, bytesUp: 1_500_000L, bytesDown: 0, "Wan");
        for (var d = 1; d <= 7; d++)
            InsertDaily(1, DayStartMs - d * Day, bytesUp: 1_000_000L, bytesDown: 0, "Wan");

        var result = Run();
        result.UncommonTalkers.Where(t => t.Category == UncommonCategory.UnusualVolume).Should().BeEmpty();
    }

    [Fact]
    public void UnusualVolume_DoesNotFire_WithInsufficientBaseline()
    {
        SeedApp(1, "young.exe", publisher: "Young Co.", firstSeen: DayStartMs - 5 * Day);
        InsertHourly(1, DayStartMs + 5 * Hour, bytesUp: 100_000_000L, bytesDown: 0, "Wan");
        // Only 3 baseline days — below UnusualVolumeMinBaselineDays=4.
        for (var d = 1; d <= 3; d++)
            InsertDaily(1, DayStartMs - d * Day, bytesUp: 1_000L, bytesDown: 0, "Wan");

        var result = Run();
        result.UncommonTalkers.Where(t => t.Category == UncommonCategory.UnusualVolume).Should().BeEmpty();
    }

    [Fact]
    public void UnusualVolume_DownDirectionReason_WhenDownDominates()
    {
        SeedApp(1, "downer.exe", publisher: "Down Co.", firstSeen: DayStartMs - 30 * Day);
        InsertHourly(1, DayStartMs + 5 * Hour, bytesUp: 0, bytesDown: 10_000_000L, "Wan");
        for (var d = 1; d <= 4; d++)
            InsertDaily(1, DayStartMs - d * Day, bytesUp: 0, bytesDown: 1_000_000L, "Wan");

        var result = Run();
        var unusual = result.UncommonTalkers.Single(t => t.Category == UncommonCategory.UnusualVolume);
        unusual.Reason.Should().StartWith("Downloaded ");
    }

    // ─── RiskyPaths heuristic ──────────────────────────────────────────────

    [Fact]
    public void RiskyPaths_FiresForUnsignedAppInUserWritablePath()
    {
        SeedApp(1, "updater_x.exe",
            publisher: null,
            signatureStatus: "Unsigned",
            isUserWritable: true,
            imagePath: @"C:\Users\mitch\AppData\Local\Temp\updater_x.exe",
            firstSeen: DayStartMs + 1 * Hour);
        SeedSession(11, appId: 1, startTime: DayStartMs + 1 * Hour);
        InsertHourly(1, DayStartMs + 2 * Hour, bytesUp: 100, bytesDown: 200, "Wan");
        InsertConnection(11, "TCP", "1.2.3.4", 443, "Wan", up: 100, down: 200, first: DayStartMs + 2*Hour, last: DayStartMs + 2*Hour);

        var result = Run();
        var risky = result.UncommonTalkers.Where(t => t.Category == UncommonCategory.RiskyPaths).ToList();

        risky.Should().ContainSingle();
        risky[0].ImageName.Should().Be("updater_x.exe");
        risky[0].SignatureStatus.Should().Be("Unsigned");
        risky[0].Reason.Should().Contain("%TEMP%");
        risky[0].Reason.Should().Contain("1 WAN endpoint");
    }

    [Fact]
    public void RiskyPaths_DoesNotFire_ForSignedApp()
    {
        SeedApp(1, "signed.exe", signatureStatus: "Signed", isUserWritable: true,
                imagePath: @"C:\Users\mitch\AppData\Local\signed.exe");
        InsertHourly(1, DayStartMs + 2 * Hour, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run();
        result.UncommonTalkers.Where(t => t.Category == UncommonCategory.RiskyPaths).Should().BeEmpty();
    }

    // ─── Notable: UnsignedFromUserPath ─────────────────────────────────────

    [Fact]
    public void Notable_UnsignedFromUserPath_EmitsCriticalEntry()
    {
        SeedApp(1, "updater_x.exe",
            publisher: null,
            signatureStatus: "Unsigned",
            isUserWritable: true,
            imagePath: @"C:\Users\mitch\AppData\Local\Temp\updater_x.exe",
            firstSeen: DayStartMs + 1 * Hour);
        SeedSession(11, appId: 1, pid: 8841, startTime: DayStartMs + 1 * Hour);
        InsertSample(11, DayStartMs + 14 * Hour + 22 * 60_000, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run();

        result.Notable.Should().ContainSingle();
        result.Notable[0].Severity.Should().Be(NotableSeverity.Critical);
        result.Notable[0].Pid.Should().Be(8841);
        result.Notable[0].Title.Should().Be("Unsigned binary from a user-writable path");
        result.Notable[0].Detail.Should().Contain("updater_x.exe");
        result.Notable[0].Detail.Should().Contain("%TEMP%");
    }

    [Fact]
    public void Notable_DoesNotFire_WithoutWanSampleEvenWithMatchingApp()
    {
        SeedApp(1, "updater_x.exe", signatureStatus: "Unsigned", isUserWritable: true,
                imagePath: @"C:\Users\mitch\AppData\Local\Temp\updater_x.exe");
        SeedSession(11, appId: 1, startTime: DayStartMs + 1 * Hour);
        // Local sample only — no WAN flow.
        InsertSample(11, DayStartMs + 14 * Hour, bytesUp: 100, bytesDown: 200, "Local");

        var result = Run();
        result.Notable.Should().BeEmpty();
    }

    [Fact]
    public void Notable_AlertId_ProjectsRealAlertWhenProducerInsertedDuringDay()
    {
        // Phase 6.4 deep-link: the Notable row should carry the alerts.alert_id
        // that the producer inserted for the same (type, entity_kind, entity_ref)
        // within the day window.
        SeedApp(1, "updater_x.exe",
            publisher: null,
            signatureStatus: "Unsigned",
            isUserWritable: true,
            imagePath: @"C:\Users\mitch\AppData\Local\Temp\updater_x.exe",
            firstSeen: DayStartMs + 1 * Hour);
        SeedSession(11, appId: 1, pid: 8841, startTime: DayStartMs + 1 * Hour);
        InsertSample(11, DayStartMs + 14 * Hour, bytesUp: 100, bytesDown: 200, "Wan");

        // Producer's matching alerts row — entity_ref is the app_id as a string,
        // matching what UnsignedFromUserPathRule writes.
        InsertAlert(
            type: "UnsignedFromUserPath",
            entityKind: "App",
            entityRef: "1",
            severity: "Critical",
            createdAtUnixMs: DayStartMs + 14 * Hour + 30 * 60_000,
            title: "Unsigned binary from a user-writable path",
            detail: "...",
            sourceMonitor: "Capture");

        var result = Run();

        result.Notable.Should().ContainSingle();
        result.Notable[0].AppId.Should().Be(1);
        result.Notable[0].AlertId.Should().BeGreaterThan(0,
            "the LEFT JOIN should project the real alerts.alert_id, not the Phase 5b sentinel");
    }

    [Fact]
    public void Notable_AlertId_StaysZero_WhenNoMatchingAlertExists()
    {
        // The producer should have inserted by report time but doesn't have to
        // have. The LEFT JOIN keeps Reports honest: zero sentinel means "no
        // deep-link target" and the chip stays inert.
        SeedApp(1, "updater_x.exe",
            publisher: null,
            signatureStatus: "Unsigned",
            isUserWritable: true,
            imagePath: @"C:\Users\mitch\AppData\Local\Temp\updater_x.exe",
            firstSeen: DayStartMs + 1 * Hour);
        SeedSession(11, appId: 1, pid: 8841, startTime: DayStartMs + 1 * Hour);
        InsertSample(11, DayStartMs + 14 * Hour, bytesUp: 100, bytesDown: 200, "Wan");
        // NO alerts row inserted.

        var result = Run();

        result.Notable.Should().ContainSingle();
        result.Notable[0].AlertId.Should().Be(0);
    }

    [Fact]
    public void Notable_AlertId_IgnoresAlertOutsideDayWindow()
    {
        // An alert raised yesterday for the same app must not link to today's
        // Notable card — the day scope is part of the JOIN predicate.
        SeedApp(1, "updater_x.exe",
            signatureStatus: "Unsigned",
            isUserWritable: true,
            imagePath: @"C:\Users\mitch\AppData\Local\Temp\updater_x.exe",
            firstSeen: DayStartMs + 1 * Hour);
        SeedSession(11, appId: 1, pid: 8841, startTime: DayStartMs + 1 * Hour);
        InsertSample(11, DayStartMs + 14 * Hour, bytesUp: 100, bytesDown: 200, "Wan");

        // Yesterday's alert — should NOT be joined to today's Notable.
        InsertAlert(
            type: "UnsignedFromUserPath",
            entityKind: "App",
            entityRef: "1",
            severity: "Critical",
            createdAtUnixMs: DayStartMs - 6 * Hour,
            title: "Unsigned binary from a user-writable path",
            detail: "...",
            sourceMonitor: "Capture");

        var result = Run();
        result.Notable.Should().ContainSingle();
        result.Notable[0].AlertId.Should().Be(0);
    }

    // ─── HasOverlap propagation ────────────────────────────────────────────

    [Fact]
    public void HasOverlap_FlagsAppPresentInBothTopAppsAndTalkers()
    {
        SeedApp(1, "updater_x.exe", signatureStatus: "Unsigned", isUserWritable: true,
                imagePath: @"C:\Users\mitch\AppData\Local\Temp\updater_x.exe");
        InsertHourly(1, DayStartMs + 2 * Hour, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run();
        // App should appear in both Top Apps AND RiskyPaths.
        result.TopApps[0].HasOverlap.Should().BeTrue();
        result.UncommonTalkers.Single(t => t.Category == UncommonCategory.RiskyPaths).HasOverlap.Should().BeTrue();
    }

    // ─── Anchor baseline / delta ───────────────────────────────────────────

    [Fact]
    public void HeroDelta_AgainstAvg7d_IsRelativeToPriorWeekAverage()
    {
        SeedApp(1, "a.exe");
        // Today: 10 MB total
        InsertHourly(1, DayStartMs + 10 * Hour, bytesUp: 5_000_000L, bytesDown: 5_000_000L, "Wan");
        // 7 prior days: 5 MB total each (per-day baseline avg = 5 MB).
        for (var d = 1; d <= 7; d++)
            InsertDaily(1, DayStartMs - d * Day, bytesUp: 2_500_000L, bytesDown: 2_500_000L, "Wan");

        var result = Run(AnchorMode.Avg7d);

        // 10M vs 5M baseline = +100%. The fixture is exact integers — no
        // floating slop justifies BeApproximately here; assert the exact value
        // so a drift in the delta math doesn't silently slide under 0.5%.
        result.Hero.TotalDeltaPct.Should().Be(100.0);
    }

    // ─── Phase 9.3 baseline-sufficiency guard ──────────────────────────────

    [Fact]
    public void BaselineDaysAvailable_NoTrafficDaily_IsZero()
    {
        // Fresh install OR post-Reset-History state: traffic_daily is empty
        // (the wipe clears the tier the baseline math reads from). apps may
        // still exist — the registry survives by design — but the guard
        // sources from the data tier, so it correctly returns 0.
        SeedApp(1, "a.exe");

        var result = Run(AnchorMode.Avg7d);
        result.Hero.BaselineDaysAvailable.Should().Be(0);
    }

    [Fact]
    public void BaselineDaysAvailable_Avg7d_PartialHistory_ReturnsExactDaySpan()
    {
        // traffic_daily earliest row 3 days before report day → 3 days of
        // pre-report history available.
        SeedApp(1, "a.exe");
        InsertDaily(1, DayStartMs - 3 * Day, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run(AnchorMode.Avg7d);
        result.Hero.BaselineDaysAvailable.Should().Be(3);
    }

    [Fact]
    public void BaselineDaysAvailable_Avg7d_FullHistory_CapsAtAnchorSize()
    {
        // traffic_daily earliest row 30 days before report day → far older
        // than the 7-day anchor window; clamp to 7.
        SeedApp(1, "a.exe");
        InsertDaily(1, DayStartMs - 30 * Day, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run(AnchorMode.Avg7d);
        result.Hero.BaselineDaysAvailable.Should().Be(7);
    }

    [Fact]
    public void BaselineDaysAvailable_Avg30d_TwentyDayHistory_ReturnsTwenty()
    {
        // Partial baseline on a wider anchor window — treatment (b) territory:
        // 20 of 30 days available, deltas stay visible with the partial-
        // baseline caution caption.
        SeedApp(1, "a.exe");
        InsertDaily(1, DayStartMs - 20 * Day, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run(AnchorMode.Avg30d);
        result.Hero.BaselineDaysAvailable.Should().Be(20);
    }

    [Fact]
    public void BaselineDaysAvailable_MultipleDailyRows_UsesEarliestBucket()
    {
        // Two traffic_daily rows; the earlier bucket_start wins. Confirms
        // the MIN(bucket_start) sourcing — newer rows don't shorten the
        // baseline.
        SeedApp(1, "a.exe");
        InsertDaily(1, DayStartMs - 1 * Day, bytesUp: 100, bytesDown: 200, "Wan");
        InsertDaily(1, DayStartMs - 5 * Day, bytesUp: 100, bytesDown: 200, "Wan");

        var result = Run(AnchorMode.Avg7d);
        result.Hero.BaselineDaysAvailable.Should().Be(5);
    }

    // ─── Path abbreviation helper ──────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Users\mitch\AppData\Local\Temp\updater_x.exe", @"%TEMP%\updater_x.exe")]
    [InlineData(@"C:\Users\mitch\AppData\Roaming\Spotify\Spotify.exe", @"%APPDATA%\Spotify\Spotify.exe")]
    [InlineData(@"C:\Users\mitch\AppData\Local\Programs\app.exe", @"%LOCALAPPDATA%\Programs\app.exe")]
    [InlineData(@"C:\Users\mitch\Downloads\thing.exe", @"%USERPROFILE%\Downloads\thing.exe")]
    [InlineData(@"C:\Program Files\app.exe", @"C:\Program Files\app.exe")]
    public void AbbreviateUserWritablePath_ReplacesKnownTokens(string input, string expected)
    {
        DailyReportRepository.AbbreviateUserWritablePath(input).Should().Be(expected);
    }

    // ─── seed helpers ──────────────────────────────────────────────────────

    private void SeedApp(
        int appId,
        string imageName,
        string? publisher = null,
        string signatureStatus = "Signed",
        bool isUserWritable = false,
        string? imagePath = null,
        long? firstSeen = null)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT OR IGNORE INTO apps
              (app_id, image_path, image_name, publisher, signature_status, is_user_writable_path, first_seen, last_seen)
            VALUES ($id, $path, $name, $pub, $sig, $uwp, $first, $first);
            """;
        c.Parameters.AddWithValue("$id", appId);
        c.Parameters.AddWithValue("$path", imagePath ?? $@"C:\bin\{imageName}");
        c.Parameters.AddWithValue("$name", imageName);
        c.Parameters.AddWithValue("$pub", (object?)publisher ?? DBNull.Value);
        c.Parameters.AddWithValue("$sig", signatureStatus);
        c.Parameters.AddWithValue("$uwp", isUserWritable ? 1 : 0);
        c.Parameters.AddWithValue("$first", firstSeen ?? DayStartMs - 30 * Day);
        c.ExecuteNonQuery();
    }

    private void SeedSession(int sessionId, int appId, int pid = 100,
        long startTime = 0, long? endTime = null, string? hostedServices = null)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT OR IGNORE INTO process_sessions
              (session_id, app_id, pid, start_time, end_time, hosted_services)
            VALUES ($sid, $app, $pid, $start, $end, $hosted);
            """;
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$app", appId);
        c.Parameters.AddWithValue("$pid", pid);
        c.Parameters.AddWithValue("$start", startTime);
        c.Parameters.AddWithValue("$end", (object?)endTime ?? DBNull.Value);
        c.Parameters.AddWithValue("$hosted", (object?)hostedServices ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    private void InsertSample(int sessionId, long bucketStart, long bytesUp, long bytesDown, string remoteClass)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO traffic_samples (session_id, bucket_start, bytes_up, bytes_down, remote_class)
            VALUES ($sid, $b, $u, $d, $cls);
            """;
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$b", bucketStart);
        c.Parameters.AddWithValue("$u", bytesUp);
        c.Parameters.AddWithValue("$d", bytesDown);
        c.Parameters.AddWithValue("$cls", remoteClass);
        c.ExecuteNonQuery();
    }

    private void InsertHourly(int appId, long bucketStart, long bytesUp, long bytesDown, string remoteClass)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO traffic_hourly (app_id, bucket_start, remote_class, bytes_up, bytes_down)
            VALUES ($a, $b, $cls, $u, $d);
            """;
        c.Parameters.AddWithValue("$a", appId);
        c.Parameters.AddWithValue("$b", bucketStart);
        c.Parameters.AddWithValue("$cls", remoteClass);
        c.Parameters.AddWithValue("$u", bytesUp);
        c.Parameters.AddWithValue("$d", bytesDown);
        c.ExecuteNonQuery();
    }

    private void InsertDaily(int appId, long bucketStart, long bytesUp, long bytesDown, string remoteClass)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO traffic_daily (app_id, bucket_start, remote_class, bytes_up, bytes_down)
            VALUES ($a, $b, $cls, $u, $d);
            """;
        c.Parameters.AddWithValue("$a", appId);
        c.Parameters.AddWithValue("$b", bucketStart);
        c.Parameters.AddWithValue("$cls", remoteClass);
        c.Parameters.AddWithValue("$u", bytesUp);
        c.Parameters.AddWithValue("$d", bytesDown);
        c.ExecuteNonQuery();
    }

    private void InsertAlert(
        string type, string entityKind, string entityRef, string severity,
        long createdAtUnixMs, string title, string detail, string sourceMonitor)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO alerts
              (type, severity, created_at, source_monitor, entity_kind, entity_ref, title, detail)
            VALUES ($type, $sev, $ts, $src, $ek, $er, $title, $detail);
            """;
        c.Parameters.AddWithValue("$type", type);
        c.Parameters.AddWithValue("$sev", severity);
        c.Parameters.AddWithValue("$ts", createdAtUnixMs);
        c.Parameters.AddWithValue("$src", sourceMonitor);
        c.Parameters.AddWithValue("$ek", entityKind);
        c.Parameters.AddWithValue("$er", entityRef);
        c.Parameters.AddWithValue("$title", title);
        c.Parameters.AddWithValue("$detail", detail);
        c.ExecuteNonQuery();
    }

    private void InsertConnection(int sessionId, string protocol, string addr, int port, string cls,
        long up, long down, long first, long last)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO connections (session_id, protocol, remote_addr, remote_port, remote_class,
                                     bytes_up, bytes_down, first_seen, last_seen)
            VALUES ($sid, $p, $a, $port, $cls, $u, $d, $first, $last);
            """;
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$p", protocol);
        c.Parameters.AddWithValue("$a", addr);
        c.Parameters.AddWithValue("$port", port);
        c.Parameters.AddWithValue("$cls", cls);
        c.Parameters.AddWithValue("$u", up);
        c.Parameters.AddWithValue("$d", down);
        c.Parameters.AddWithValue("$first", first);
        c.Parameters.AddWithValue("$last", last);
        c.ExecuteNonQuery();
    }
}
