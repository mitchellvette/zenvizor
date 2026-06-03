using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Migration 004 backfills <c>traffic_hourly</c> and <c>traffic_daily</c> from
/// existing <c>traffic_samples</c>. Tested by simulating a pre-Phase-4 database
/// state (apps + sessions + samples present, rollup tables empty) and confirming
/// both tiers get populated when the migrator runs.
/// </summary>
public sealed class RollupBackfillMigrationTests : IDisposable
{
    private const long Day = 86_400_000L;
    private const long Hour = 3_600_000L;
    private const long Now = 1_780_704_000_000L; // 2026-06-02T00:00:00Z

    private readonly string _dbPath;

    public RollupBackfillMigrationTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-backfill-{Guid.NewGuid():N}.db");
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
    public void Migration004_PopulatesBothHourlyAndDaily_FromExistingSamples()
    {
        // Phase 1: bootstrap to schema version 3 (without 004 yet) by removing
        // the .sql for 004 from the migrator's view. The simplest way: just
        // run the full migrator (which applies all 4 migrations including 004),
        // then DELETE the rollup rows it created, then re-seed only samples,
        // then assert that the next migration cycle (which is a no-op since
        // 004 already ran) does NOT re-populate. That's a different test.
        //
        // For the actual backfill, we run the full migrator with samples already
        // in place: that ordering can't easily be tested via the public API.
        // Instead we test the backfill SQL DIRECTLY against a populated DB.

        new Migrator().Migrate(_dbPath);
        SeedSamples();

        // Clear the rollup tables (they would have been populated, but we want
        // to verify the backfill SQL re-populates correctly from samples).
        using (var conn = Open())
        {
            using var clear = conn.CreateCommand();
            clear.CommandText = "DELETE FROM traffic_hourly; DELETE FROM traffic_daily;";
            clear.ExecuteNonQuery();
        }

        // Re-run the migration 004 SQL directly. (In production it runs once;
        // here we trigger it again to verify the math against known fixtures.)
        ApplyBackfillSql();

        // Assertions: 3 samples across 3 distinct hours, all on the same day.
        var hourly = QueryAll("SELECT bucket_start, bytes_up, bytes_down FROM traffic_hourly ORDER BY bucket_start;");
        hourly.Should().HaveCount(3);
        hourly[0]["bucket_start"].Should().Be(Now);
        hourly[0]["bytes_up"].Should().Be(100L);
        hourly[0]["bytes_down"].Should().Be(1_000L);
        hourly[1]["bucket_start"].Should().Be(Now + Hour);
        hourly[1]["bytes_up"].Should().Be(200L);
        hourly[2]["bucket_start"].Should().Be(Now + 2 * Hour);
        hourly[2]["bytes_up"].Should().Be(300L);

        var daily = QueryAll("SELECT bucket_start, bytes_up, bytes_down FROM traffic_daily;");
        daily.Should().ContainSingle("all three hours fall in the same day");
        daily[0]["bucket_start"].Should().Be(Now);
        daily[0]["bytes_up"].Should().Be(600L);   // 100 + 200 + 300
        daily[0]["bytes_down"].Should().Be(6_000L);
    }

    [Fact]
    public void Migration004_SpansMultipleDays_ProducesOneDailyRowPerDay()
    {
        new Migrator().Migrate(_dbPath);

        // Three samples across three different days.
        SeedAppAndSession(appId: 1, sessionId: 1);
        InsertSample(1, Now,              up: 100, down: 0);
        InsertSample(1, Now + 1 * Day,    up: 200, down: 0);
        InsertSample(1, Now + 2 * Day,    up: 300, down: 0);

        // Clear & rerun the backfill SQL.
        using (var conn = Open())
        using (var clear = conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM traffic_hourly; DELETE FROM traffic_daily;";
            clear.ExecuteNonQuery();
        }
        ApplyBackfillSql();

        var daily = QueryAll("SELECT bucket_start, bytes_up FROM traffic_daily ORDER BY bucket_start;");
        daily.Should().HaveCount(3);
        daily[0]["bucket_start"].Should().Be(Now);
        daily[1]["bucket_start"].Should().Be(Now + 1 * Day);
        daily[2]["bucket_start"].Should().Be(Now + 2 * Day);
        daily.Sum(r => (long)r["bytes_up"]).Should().Be(600L);
    }

    [Fact]
    public void Migration004_OnConflictDoNothing_DoesNotDoubleCountIfRunTwice()
    {
        new Migrator().Migrate(_dbPath);
        SeedSamples();

        // Clear rollups so we have a clean slate.
        using (var conn = Open())
        using (var clear = conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM traffic_hourly; DELETE FROM traffic_daily;";
            clear.ExecuteNonQuery();
        }

        // Apply backfill TWICE -- the second run must not double bytes.
        ApplyBackfillSql();
        ApplyBackfillSql();

        var daily = QueryAll("SELECT bytes_up FROM traffic_daily;");
        daily[0]["bytes_up"].Should().Be(600L, "ON CONFLICT DO NOTHING prevents double-counting");
    }

    private void SeedSamples()
    {
        SeedAppAndSession(appId: 1, sessionId: 1);
        InsertSample(sessionId: 1, bucketStart: Now,              up: 100, down: 1_000);
        InsertSample(sessionId: 1, bucketStart: Now + Hour,       up: 200, down: 2_000);
        InsertSample(sessionId: 1, bucketStart: Now + 2 * Hour,   up: 300, down: 3_000);
    }

    private void SeedAppAndSession(int appId, int sessionId)
    {
        using var conn = Open();
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
                INSERT OR IGNORE INTO process_sessions (session_id, app_id, pid, start_time)
                VALUES ($sid, $app, 100, 0);
                """;
            c.Parameters.AddWithValue("$sid", sessionId);
            c.Parameters.AddWithValue("$app", appId);
            c.ExecuteNonQuery();
        }
    }

    private void InsertSample(int sessionId, long bucketStart, long up, long down)
    {
        using var conn = Open();
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO traffic_samples (session_id, bucket_start, bytes_up, bytes_down, remote_class)
            VALUES ($sid, $b, $u, $d, 'Wan');
            """;
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$b", bucketStart);
        c.Parameters.AddWithValue("$u", up);
        c.Parameters.AddWithValue("$d", down);
        c.ExecuteNonQuery();
    }

    /// <summary>
    /// Apply migration 004's SQL directly (via the embedded resource). Used by
    /// tests so we can verify the backfill math against deterministic fixtures.
    /// </summary>
    private void ApplyBackfillSql()
    {
        var assembly = typeof(Migrator).Assembly;
        var resName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("004_phase4_rollup_backfill.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate;");
        connection.Open();
        return connection;
    }

    private IReadOnlyList<Dictionary<string, object>> QueryAll(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
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
