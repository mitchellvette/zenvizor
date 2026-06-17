using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Core.Aggregation;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Phase 4 retention purge correctness. Asserts that rows older than each
/// tier's configured window are deleted while newer rows are preserved, and
/// that rollup tiers and the alerts table are pruned per their policy.
/// </summary>
public sealed class RetentionRepositoryTests : IDisposable
{
    private const long Day = 86_400_000L;
    private const long Now = 1_780_704_000_000L; // 2026-06-02T00:00:00Z

    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly RetentionRepository _retention;

    public RetentionRepositoryTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-retention-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
        _retention = new RetentionRepository(_connections);
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

    [Fact]
    public void PurgeOlderThan_RemovesOldSamples_KeepsNewer()
    {
        SeedAppAndSession(appId: 1, sessionId: 1);
        // Default retention.traffic_samples_days = 30.
        InsertSample(sessionId: 1, bucketStart: Now - 31 * Day, bytesUp: 100, bytesDown: 200);
        InsertSample(sessionId: 1, bucketStart: Now - 1 * Day,  bytesUp: 300, bytesDown: 400);

        var result = _retention.PurgeOlderThan(Now);

        result.SamplesDeleted.Should().Be(1);
        var remaining = QueryAll("SELECT bytes_up FROM traffic_samples;");
        remaining.Should().ContainSingle();
        remaining[0]["bytes_up"].Should().Be(300L);
    }

    [Fact]
    public void PurgeOlderThan_BoundaryRow_ExactlyAtBoundary_IsKept()
    {
        SeedAppAndSession(1, 1);
        // Row at exactly the boundary (boundary = now - 30d) should NOT be deleted
        // because the predicate is strict-less-than.
        InsertSample(sessionId: 1, bucketStart: Now - 30 * Day, bytesUp: 100, bytesDown: 0);

        var result = _retention.PurgeOlderThan(Now);

        result.SamplesDeleted.Should().Be(0);
        QueryAll("SELECT 1 FROM traffic_samples;").Should().ContainSingle();
    }

    [Fact]
    public void PurgeOlderThan_PerTier_AppliesOwnWindow()
    {
        SeedAppAndSession(1, 1);

        // Defaults: samples=30d, connections=30d, hourly=90d, daily=365d.
        InsertSample(sessionId: 1, bucketStart: Now - 31 * Day, bytesUp: 1, bytesDown: 0);
        InsertConnection(sessionId: 1, lastSeen: Now - 31 * Day);
        InsertHourly(appId: 1, bucketStart: Now - 91 * Day, bytesUp: 1, bytesDown: 0);
        InsertDaily(appId: 1,  bucketStart: Now - 366 * Day, bytesUp: 1, bytesDown: 0);

        // Newer rows that should be kept.
        InsertSample(sessionId: 1,  bucketStart: Now - 1 * Day, bytesUp: 2, bytesDown: 0);
        InsertHourly(appId: 1, bucketStart: Now - 1 * Day, bytesUp: 2, bytesDown: 0);
        InsertDaily(appId: 1,  bucketStart: Now - 1 * Day, bytesUp: 2, bytesDown: 0);

        var result = _retention.PurgeOlderThan(Now);

        result.SamplesDeleted.Should().Be(1);
        result.ConnectionsDeleted.Should().Be(1);
        result.HourlyDeleted.Should().Be(1);
        result.DailyDeleted.Should().Be(1);

        QueryAll("SELECT bytes_up FROM traffic_samples;").Should().ContainSingle()
            .Which["bytes_up"].Should().Be(2L);
        QueryAll("SELECT bytes_up FROM traffic_hourly;").Should().ContainSingle()
            .Which["bytes_up"].Should().Be(2L);
        QueryAll("SELECT bytes_up FROM traffic_daily;").Should().ContainSingle()
            .Which["bytes_up"].Should().Be(2L);
    }

    [Fact]
    public void PurgeOlderThan_UnacknowledgedAlerts_NeverDeleted()
    {
        InsertAlert(createdAt: Now - 365 * Day, acknowledgedAt: null);
        InsertAlert(createdAt: Now - 365 * Day, acknowledgedAt: Now - 91 * Day);
        InsertAlert(createdAt: Now - 1 * Day,   acknowledgedAt: Now - 1 * Day);

        var result = _retention.PurgeOlderThan(Now);

        // Only the old-and-acknowledged one (acked 91 days ago, retention 90d) is purged.
        result.AlertsDeleted.Should().Be(1);

        var remaining = QueryAll("SELECT acknowledged_at FROM alerts ORDER BY acknowledged_at IS NULL DESC, acknowledged_at;");
        remaining.Should().HaveCount(2);
    }

    [Fact]
    public void PurgeOlderThan_OrphanSession_DeletedAfterSamplesAndConnectionsGone()
    {
        SeedAppAndSession(appId: 1, sessionId: 1, endTime: Now - 60 * Day);
        // Session has only OLD samples/connections — these get purged first,
        // then the now-empty session is reclaimed.
        InsertSample(sessionId: 1, bucketStart: Now - 60 * Day, bytesUp: 1, bytesDown: 0);
        InsertConnection(sessionId: 1, lastSeen: Now - 60 * Day);

        var result = _retention.PurgeOlderThan(Now);

        result.SamplesDeleted.Should().Be(1);
        result.ConnectionsDeleted.Should().Be(1);
        result.OrphanSessionsDeleted.Should().Be(1);

        QueryAll("SELECT 1 FROM process_sessions;").Should().BeEmpty();
        // App row stays — by design; first_seen/last_seen are still meaningful.
        QueryAll("SELECT 1 FROM apps;").Should().ContainSingle();
    }

    [Fact]
    public void PurgeOlderThan_OrphanSession_NotDeletedIfStillReferenced()
    {
        SeedAppAndSession(appId: 1, sessionId: 1, endTime: Now - 60 * Day);
        // Recent sample keeps the session alive.
        InsertSample(sessionId: 1, bucketStart: Now - 1 * Day, bytesUp: 1, bytesDown: 0);

        var result = _retention.PurgeOlderThan(Now);

        result.SamplesDeleted.Should().Be(0);
        result.OrphanSessionsDeleted.Should().Be(0);
        QueryAll("SELECT 1 FROM process_sessions;").Should().ContainSingle();
    }

    [Fact]
    public void PurgeOlderThan_ChunkedDelete_RemovesAllEligibleRowsAcrossMultipleChunks()
    {
        // Drives a tiny chunk size against more rows than fit in one chunk, so
        // the implementation must iterate. Asserts every eligible row is gone.
        SeedAppAndSession(1, 1);
        for (var i = 0; i < 25; i++)
        {
            InsertSample(sessionId: 1, bucketStart: Now - (31 + i) * Day, bytesUp: 1, bytesDown: 0);
        }

        var retention = new RetentionRepository(_connections, chunkSize: 4);
        var result = retention.PurgeOlderThan(Now);

        result.SamplesDeleted.Should().Be(25);
        QueryAll("SELECT 1 FROM traffic_samples;").Should().BeEmpty();
    }

    [Fact]
    public void PurgeOlderThan_AlignsCutoffsSoBucketSurvivalIsTimeOfDayIndependent()
    {
        // Sample sits in the 60s bucket ending at 30d-23:59:00 ago. At 30d
        // exactly it should be on the keep side of the cutoff. The aligned
        // cutoff makes that determination identical regardless of the
        // wall-clock seconds component of the purge time.
        SeedAppAndSession(1, 1);
        var bucketStart = BucketAligner.AlignToBucket(Now) - 30 * Day;
        InsertSample(sessionId: 1, bucketStart: bucketStart, bytesUp: 1, bytesDown: 0);

        // Same now drifted by 47 seconds — without alignment, the cutoff
        // wobbles and the same bucket survives one call but not another.
        var resultEarly = _retention.PurgeOlderThan(Now);
        var resultLate  = _retention.PurgeOlderThan(Now + 47_000);

        resultEarly.SamplesDeleted.Should().Be(0);
        resultLate.SamplesDeleted.Should().Be(0);
        QueryAll("SELECT 1 FROM traffic_samples;").Should().ContainSingle();
    }

    [Fact]
    public void PurgeOlderThan_CustomRetentionFromSettings_Honored()
    {
        // Override default retention windows.
        SetSetting(RetentionRepository.SettingKeys.SamplesDays, "7");
        SetSetting(RetentionRepository.SettingKeys.HourlyDays,  "14");

        SeedAppAndSession(1, 1);
        InsertSample(sessionId: 1, bucketStart: Now - 8 * Day, bytesUp: 1, bytesDown: 0);
        InsertHourly(appId: 1, bucketStart: Now - 15 * Day, bytesUp: 1, bytesDown: 0);

        var result = _retention.PurgeOlderThan(Now);

        result.SamplesDeleted.Should().Be(1);
        result.HourlyDeleted.Should().Be(1);
    }

    // ---- Phase 6.2 wipe-history ----

    [Fact]
    public void WipeHistory_DeletesEveryDataRow_PreservesAppsAndSettings()
    {
        SeedAppAndSession(1, 1);
        InsertSample(sessionId: 1, bucketStart: Now - 1 * Day, bytesUp: 100, bytesDown: 200);
        InsertConnection(sessionId: 1, lastSeen: Now - 1 * Day);
        InsertHourly(appId: 1, bucketStart: Now - 1 * Day, bytesUp: 1, bytesDown: 0);
        InsertDaily(appId: 1, bucketStart: Now - 1 * Day, bytesUp: 1, bytesDown: 0);
        InsertAlert(createdAt: Now - 1 * Day, acknowledgedAt: null);

        var result = _retention.WipeHistory();

        result.SamplesDeleted.Should().Be(1);
        result.ConnectionsDeleted.Should().Be(1);
        result.HourlyDeleted.Should().Be(1);
        result.DailyDeleted.Should().Be(1);
        result.AlertsDeleted.Should().Be(1);
        result.SessionsDeleted.Should().Be(1);

        QueryAll("SELECT 1 FROM traffic_samples;").Should().BeEmpty();
        QueryAll("SELECT 1 FROM connections;").Should().BeEmpty();
        QueryAll("SELECT 1 FROM traffic_hourly;").Should().BeEmpty();
        QueryAll("SELECT 1 FROM traffic_daily;").Should().BeEmpty();
        QueryAll("SELECT 1 FROM alerts;").Should().BeEmpty();
        QueryAll("SELECT 1 FROM process_sessions;").Should().BeEmpty();

        // Preserved.
        QueryAll("SELECT 1 FROM apps;").Should().ContainSingle();
        QueryAll("SELECT 1 FROM settings WHERE key = 'retention.traffic_samples_days';")
            .Should().ContainSingle();
    }

    [Fact]
    public void WipeHistory_OnEmptyDatabase_ReturnsAllZeros()
    {
        var result = _retention.WipeHistory();

        result.SamplesDeleted.Should().Be(0);
        result.ConnectionsDeleted.Should().Be(0);
        result.HourlyDeleted.Should().Be(0);
        result.DailyDeleted.Should().Be(0);
        result.AlertsDeleted.Should().Be(0);
        result.SessionsDeleted.Should().Be(0);
    }

    // ---- seed helpers ----

    private void SeedAppAndSession(int appId, int sessionId, long? endTime = null)
    {
        using var conn = _connections.Open();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = """
                INSERT OR IGNORE INTO apps (app_id, image_path, image_name, signature_status, first_seen, last_seen)
                VALUES ($id, '/a.exe', 'a.exe', 'Unchecked', 0, $now);
                """;
            c.Parameters.AddWithValue("$id", appId);
            c.Parameters.AddWithValue("$now", Now);
            c.ExecuteNonQuery();
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = """
                INSERT OR IGNORE INTO process_sessions (session_id, app_id, pid, start_time, end_time)
                VALUES ($sid, $app, 100, 0, $end);
                """;
            c.Parameters.AddWithValue("$sid", sessionId);
            c.Parameters.AddWithValue("$app", appId);
            c.Parameters.AddWithValue("$end", (object?)endTime ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    private void InsertSample(int sessionId, long bucketStart, long bytesUp, long bytesDown)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO traffic_samples (session_id, bucket_start, bytes_up, bytes_down, remote_class)
            VALUES ($sid, $b, $u, $d, 'Wan');
            """;
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$b", bucketStart);
        c.Parameters.AddWithValue("$u", bytesUp);
        c.Parameters.AddWithValue("$d", bytesDown);
        c.ExecuteNonQuery();
    }

    private void InsertConnection(int sessionId, long lastSeen)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO connections (session_id, protocol, remote_addr, remote_port, remote_class,
                                     bytes_up, bytes_down, first_seen, last_seen)
            VALUES ($sid, 'TCP', '8.8.8.8', 443, 'Wan', 0, 0, $last, $last);
            """;
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$last", lastSeen);
        c.ExecuteNonQuery();
    }

    private void InsertHourly(int appId, long bucketStart, long bytesUp, long bytesDown)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO traffic_hourly (app_id, bucket_start, remote_class, bytes_up, bytes_down)
            VALUES ($a, $b, 'Wan', $u, $d);
            """;
        c.Parameters.AddWithValue("$a", appId);
        c.Parameters.AddWithValue("$b", bucketStart);
        c.Parameters.AddWithValue("$u", bytesUp);
        c.Parameters.AddWithValue("$d", bytesDown);
        c.ExecuteNonQuery();
    }

    private void InsertDaily(int appId, long bucketStart, long bytesUp, long bytesDown)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO traffic_daily (app_id, bucket_start, remote_class, bytes_up, bytes_down)
            VALUES ($a, $b, 'Wan', $u, $d);
            """;
        c.Parameters.AddWithValue("$a", appId);
        c.Parameters.AddWithValue("$b", bucketStart);
        c.Parameters.AddWithValue("$u", bytesUp);
        c.Parameters.AddWithValue("$d", bytesDown);
        c.ExecuteNonQuery();
    }

    private void InsertAlert(long createdAt, long? acknowledgedAt)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO alerts (type, severity, created_at, source_monitor,
                                entity_kind, entity_ref, title, detail, acknowledged_at)
            VALUES ('Test', 'Info', $c, 'test', 'App', '1', 't', 'd', $ack);
            """;
        c.Parameters.AddWithValue("$c", createdAt);
        c.Parameters.AddWithValue("$ack", (object?)acknowledgedAt ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    private void SetSetting(string key, string value)
    {
        using var conn = _connections.Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        c.Parameters.AddWithValue("$k", key);
        c.Parameters.AddWithValue("$v", value);
        c.ExecuteNonQuery();
    }

    private IReadOnlyList<Dictionary<string, object>> QueryAll(string sql)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var rows = new List<Dictionary<string, object>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}
