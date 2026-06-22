// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Phase 4 query-surface correctness. Each test seeds deterministic rows in
/// the underlying tier(s) and asserts exact-byte query results.
/// </summary>
public sealed class AppHistoryQueryRepositoryTests : IDisposable
{
    private const long Hour = 3_600_000L;
    private const long Day  = 86_400_000L;
    private const long Now  = 1_780_704_000_000L; // 2026-06-02T00:00:00Z

    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly AppHistoryQueryRepository _repo;

    public AppHistoryQueryRepositoryTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-query-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
        _repo = new AppHistoryQueryRepository(_connections);
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

    // ---- GetAppList --------------------------------------------------

    [Fact]
    public void GetAppList_SamplesTier_RanksByTotalBytesDesc()
    {
        // Samples grain: window must be <= 6h.
        SeedApp(1, "low.exe");
        SeedApp(2, "high.exe");
        SeedSession(sessionId: 11, appId: 1);
        SeedSession(sessionId: 12, appId: 2);
        InsertSample(11, Now - 60_000, 100, 200, "Wan");
        InsertSample(12, Now - 60_000, 1_000, 2_000, "Wan");

        var result = _repo.GetAppList(new QueryWindow(Now - Hour, Now));

        result.Apps.Should().HaveCount(2);
        result.Apps[0].ImageName.Should().Be("high.exe");
        result.Apps[0].BytesUp.Should().Be(1_000);
        result.Apps[0].BytesDown.Should().Be(2_000);
        result.Apps[1].ImageName.Should().Be("low.exe");
    }

    [Fact]
    public void GetAppList_HourlyTier_UsedForWindowsBetween6hAnd30d()
    {
        SeedApp(1, "chrome.exe");
        // Hourly tier — write a row 5 days ago into traffic_hourly directly.
        InsertHourly(appId: 1, bucketStart: Now - 5 * Day, bytesUp: 50_000, bytesDown: 5_000_000, "Wan");

        // Also seed samples-tier data at the SAME timestamp; if the query
        // wrongly hit samples tier (no row there), totals would be 0.
        // (We omit the samples row, so even if it did hit, it'd see 0 and
        //  return no rows — which would fail the assertion.)

        var result = _repo.GetAppList(new QueryWindow(Now - 7 * Day, Now));

        result.Apps.Should().ContainSingle();
        result.Apps[0].BytesUp.Should().Be(50_000);
        result.Apps[0].BytesDown.Should().Be(5_000_000);
    }

    [Fact]
    public void GetAppList_DailyTier_UsedForWindowsOver30d()
    {
        SeedApp(1, "chrome.exe");
        InsertDaily(appId: 1, bucketStart: Now - 60 * Day, bytesUp: 1_000_000, bytesDown: 10_000_000, "Wan");

        var result = _repo.GetAppList(new QueryWindow(Now - 90 * Day, Now));

        result.Apps.Should().ContainSingle();
        result.Apps[0].BytesUp.Should().Be(1_000_000);
    }

    [Fact]
    public void GetAppList_AppWithZeroBytesInWindow_Excluded()
    {
        SeedApp(1, "idle.exe");
        SeedSession(11, 1);
        // No samples / hourly / daily rows for this app — should be excluded.

        var result = _repo.GetAppList(new QueryWindow(Now - Hour, Now));

        result.Apps.Should().BeEmpty();
    }

    [Fact]
    public void GetAppList_HonorsWindowBoundaries()
    {
        SeedApp(1, "a.exe");
        SeedSession(11, 1);
        InsertSample(11, Now - 2 * Hour, 100, 0, "Wan"); // outside [now-1h, now)
        InsertSample(11, Now - 30 * 60_000, 200, 0, "Wan"); // inside

        var result = _repo.GetAppList(new QueryWindow(Now - Hour, Now));

        result.Apps.Should().ContainSingle();
        result.Apps[0].BytesUp.Should().Be(200);
    }

    [Fact]
    public void GetAppList_DailyTier_IncludesTodaysInProgressBucket_OnShortWindow()
    {
        // REGRESSION: with strict bucket_start >= $from semantics, a daily bucket
        // whose start time is BEFORE the query window's $from was excluded even
        // though its [start, start+24h) range overlaps the window. With overlap
        // semantics, today's bucket (started at 00:00 UTC) is included for any
        // window that touches today.
        //
        // "Now" for this test is 14:00 UTC on the day — typical mid-day query.
        // Today's daily bucket starts at midnight (= the base Now constant).
        var nowMidDay = Now + 14 * Hour;
        SeedApp(1, "chrome.exe");
        InsertDaily(appId: 1, bucketStart: Now, bytesUp: 5_000, bytesDown: 50_000, "Wan");

        // Window = [13:00, 14:00) — entirely after today's bucket started but
        // before today ends. Old strict semantics excluded today's bucket because
        // 00:00 < 13:00. Overlap semantics includes it because [00:00, 24:00)
        // overlaps [13:00, 14:00).
        var window = new QueryWindow(nowMidDay - Hour, nowMidDay);
        var detail = _repo.GetAppDetail(appId: 1, window, TrafficGrain.Daily);

        detail.Summary.BytesUp.Should().Be(5_000,
            "overlap semantics must include today's in-progress daily bucket");
        detail.Summary.BytesDown.Should().Be(50_000);
        detail.Series.Should().NotBeEmpty();
    }

    [Fact]
    public void GetTrafficHistory_HourlyTier_IncludesPartialEdgeBuckets()
    {
        // Window starts mid-hour. The hourly bucket aligned to the hour
        // BEFORE the window contains traffic that partially falls inside
        // the window — must be included.
        SeedApp(1, "a.exe");
        // Window = [Now - 90 min, Now). Bucket at (Now - 2h) starts before
        // the window but ends inside it (at Now-1h, 30 min into the window).
        InsertHourly(appId: 1, bucketStart: Now - 2 * Hour, bytesUp: 7_777, bytesDown: 0, "Wan");

        var history = _repo.GetTrafficHistory(new QueryWindow(Now - 90 * 60_000, Now), TrafficGrain.Hourly);

        history.Series.Should().ContainSingle(
            "the bucket at Now-2h spans [Now-2h, Now-1h), which overlaps the window [Now-90m, Now)");
        history.Series[0].BytesUp.Should().Be(7_777);
    }

    [Fact]
    public void GetTrafficHistory_SamplesTier_BucketWhollyBeforeWindow_Excluded()
    {
        // Overlap semantics must NOT include buckets whose range ends BEFORE
        // the window starts. 60s bucket ending 30s before window start is out.
        SeedApp(1, "a.exe");
        SeedSession(11, 1);
        InsertSample(sessionId: 11, bucketStart: Now - Hour - 60_000 - 30_000, bytesUp: 1, bytesDown: 0, "Wan");

        var history = _repo.GetTrafficHistory(new QueryWindow(Now - Hour, Now), TrafficGrain.Samples);

        history.Series.Should().BeEmpty();
    }

    // ---- GetAppDetail -----------------------------------------------

    [Fact]
    public void GetAppDetail_ReturnsSummarySeriesAndSessions()
    {
        SeedApp(1, "chrome.exe");
        SeedSession(11, 1, startTime: Now - 30 * 60_000, hostedServices: null);
        InsertSample(11, Now - 5 * 60_000, 500, 5_000, "Wan");
        InsertSample(11, Now - 4 * 60_000, 600, 6_000, "Wan");

        var result = _repo.GetAppDetail(appId: 1, new QueryWindow(Now - Hour, Now), TrafficGrain.Auto);

        result.GrainUsed.Should().Be(TrafficGrain.Samples);
        result.Summary.AppId.Should().Be(1);
        result.Summary.BytesUp.Should().Be(1_100);
        result.Summary.BytesDown.Should().Be(11_000);

        result.Series.Should().HaveCount(2);
        result.Series.Sum(p => p.BytesUp).Should().Be(1_100);

        result.RecentSessions.Should().ContainSingle();
        result.RecentSessions[0].SessionId.Should().Be(11);
    }

    [Fact]
    public void GetAppDetail_ExplicitGrainOverridesAuto()
    {
        SeedApp(1, "chrome.exe");
        InsertHourly(appId: 1, bucketStart: Now - 2 * Hour, bytesUp: 999, bytesDown: 0, "Wan");

        // Short window would normally auto-resolve to Samples; force Hourly.
        var result = _repo.GetAppDetail(1, new QueryWindow(Now - Hour, Now), TrafficGrain.Hourly);

        result.GrainUsed.Should().Be(TrafficGrain.Hourly);
    }

    // ---- GetConnections ---------------------------------------------

    [Fact]
    public void GetConnections_AggregatesByEndpointAcrossSessions()
    {
        SeedApp(1, "chrome.exe");
        SeedSession(11, 1);
        SeedSession(12, 1);
        // Two sessions of the same app, both connecting to the same endpoint.
        InsertConnection(11, "TCP", "8.8.8.8", 443, "Wan", up: 100, down: 1_000, first: Now - Hour, last: Now - 30 * 60_000);
        InsertConnection(12, "TCP", "8.8.8.8", 443, "Wan", up: 200, down: 2_000, first: Now - 20 * 60_000, last: Now - 5 * 60_000);
        InsertConnection(12, "UDP", "1.1.1.1", 53, "Wan", up: 10, down: 100, first: Now - 10 * 60_000, last: Now - 1 * 60_000);

        var result = _repo.GetConnections(1, new QueryWindow(Now - Hour, Now));

        result.Connections.Should().HaveCount(2);
        var dns = result.Connections.Single(c => c.RemoteAddress == "8.8.8.8");
        dns.BytesUp.Should().Be(300);
        dns.BytesDown.Should().Be(3_000);
        dns.FirstSeenUnixMs.Should().Be(Now - Hour);
        dns.LastSeenUnixMs.Should().Be(Now - 5 * 60_000);

        var udp = result.Connections.Single(c => c.Protocol == "UDP");
        udp.BytesUp.Should().Be(10);
    }

    [Fact]
    public void GetConnections_ConnectionsOutsideWindow_Excluded()
    {
        SeedApp(1, "chrome.exe");
        SeedSession(11, 1);
        // Connection that ended before window starts.
        InsertConnection(11, "TCP", "1.1.1.1", 443, "Wan", up: 100, down: 100,
                         first: Now - 3 * Hour, last: Now - 2 * Hour);

        var result = _repo.GetConnections(1, new QueryWindow(Now - Hour, Now));

        result.Connections.Should().BeEmpty();
    }

    // ---- GetTrafficHistory ------------------------------------------

    [Fact]
    public void GetTrafficHistory_SumsAcrossApps_RespectsGrain()
    {
        SeedApp(1, "a.exe");
        SeedApp(2, "b.exe");
        SeedSession(11, 1);
        SeedSession(12, 2);
        InsertSample(11, Now - 5 * 60_000, 100, 0, "Wan");
        InsertSample(12, Now - 5 * 60_000, 200, 0, "Wan");
        InsertSample(11, Now - 4 * 60_000, 50,  0, "Wan");

        var result = _repo.GetTrafficHistory(new QueryWindow(Now - Hour, Now), TrafficGrain.Auto);

        result.GrainUsed.Should().Be(TrafficGrain.Samples);
        // Two buckets: (Now-5m) summed across both apps = 300, (Now-4m) = 50.
        result.Series.Should().HaveCount(2);
        result.Series.Sum(p => p.BytesUp).Should().Be(350);
    }

    [Fact]
    public void GetTrafficHistory_ForcesGrainExplicit()
    {
        SeedApp(1, "a.exe");
        InsertDaily(1, Now - 100 * Day, 5_000, 0, "Wan");

        var result = _repo.GetTrafficHistory(new QueryWindow(Now - 200 * Day, Now), TrafficGrain.Daily);

        result.GrainUsed.Should().Be(TrafficGrain.Daily);
        result.Series.Should().ContainSingle();
        result.Series[0].BytesUp.Should().Be(5_000);
    }

    // ---- Auto-grain resolver -----------------------------------------

    [Theory]
    [InlineData(1 * Hour,       TrafficGrain.Samples)]
    [InlineData(24 * Hour,      TrafficGrain.Samples)]   // boundary inclusive
    [InlineData(24 * Hour + 1L, TrafficGrain.Hourly)]
    [InlineData(7 * Day,        TrafficGrain.Hourly)]
    [InlineData(30 * Day,       TrafficGrain.Hourly)]    // boundary inclusive
    [InlineData(30 * Day + 1L,  TrafficGrain.Daily)]
    [InlineData(365 * Day,      TrafficGrain.Daily)]
    public void TrafficGrainResolver_Auto_PicksExpectedTier(long spanMs, TrafficGrain expected)
    {
        var window = new QueryWindow(0, spanMs);
        TrafficGrainResolver.Resolve(window, TrafficGrain.Auto).Should().Be(expected);
    }

    [Fact]
    public void TrafficGrainResolver_ExplicitGrain_NotOverridden()
    {
        var window = new QueryWindow(0, 365 * Day); // would auto-resolve to Daily
        TrafficGrainResolver.Resolve(window, TrafficGrain.Samples).Should().Be(TrafficGrain.Samples);
        TrafficGrainResolver.Resolve(window, TrafficGrain.Hourly).Should().Be(TrafficGrain.Hourly);
    }

    // ---- seed helpers ----

    private void SeedApp(int appId, string imageName)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT OR IGNORE INTO apps
              (app_id, image_path, image_name, publisher, signature_status, is_user_writable_path, first_seen, last_seen)
            VALUES ($id, $path, $name, NULL, 'Signed', 0, 0, $now);
            """;
        c.Parameters.AddWithValue("$id", appId);
        c.Parameters.AddWithValue("$path", $@"C:\bin\{imageName}");
        c.Parameters.AddWithValue("$name", imageName);
        c.Parameters.AddWithValue("$now", Now);
        c.ExecuteNonQuery();
    }

    private void SeedSession(int sessionId, int appId, long startTime = 0, long? endTime = null, string? hostedServices = null)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT OR IGNORE INTO process_sessions
              (session_id, app_id, pid, start_time, end_time, hosted_services)
            VALUES ($sid, $app, 100, $start, $end, $hosted);
            """;
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$app", appId);
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
