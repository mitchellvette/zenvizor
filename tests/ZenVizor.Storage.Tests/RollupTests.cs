using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Phase 4 incremental-rollup correctness. Asserts that SqliteFlushSink
/// populates traffic_hourly + traffic_daily atomically with traffic_samples,
/// keyed by (app_id, bucket_start, remote_class), accumulating across flushes.
/// </summary>
public sealed class RollupTests : IDisposable
{
    // 2026-06-02T00:00:00Z
    private const long DayStart = 1_780_704_000_000L;
    private const long Hour     = 3_600_000L;

    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly SqliteFlushSink _sink;

    public RollupTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-rollup-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
        _sink = new SqliteFlushSink(_connections);
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

    private static AppIdentity Ident(string path) =>
        new(path, Path.GetFileName(path), null, "Unchecked", false);

    private static FlushBatch Batch(
        IEnumerable<NewSessionEntry>? newSessions = null,
        IReadOnlyDictionary<int, int>? knownPidToSessionId = null,
        IEnumerable<PendingTrafficSample>? samples = null,
        long nowUnixMs = 1_000) =>
        new(
            NewSessions: (newSessions ?? Array.Empty<NewSessionEntry>()).ToList(),
            KnownPidToSessionId: knownPidToSessionId ?? new Dictionary<int, int>(),
            Samples: (samples ?? Array.Empty<PendingTrafficSample>()).ToList(),
            Connections: Array.Empty<PendingConnection>(),
            ClosedSessionIds: Array.Empty<int>(),
            FlushTimeUnixMs: nowUnixMs);

    [Fact]
    public void Flush_SingleSample_PopulatesHourlyAndDaily()
    {
        var result = _sink.Flush(Batch(
            newSessions: new[] { new NewSessionEntry(100, Ident(@"C:\a.exe"), DayStart, null) },
            samples: new[]
            {
                new PendingTrafficSample(Pid: 100, BucketStartUnixMs: DayStart, BytesUp: 1_000, BytesDown: 5_000, RemoteClass: RemoteClass.Wan),
            },
            nowUnixMs: DayStart + 5_000));

        result.SampleRowsWritten.Should().Be(1);

        var hourly = QueryAll("SELECT app_id, bucket_start, remote_class, bytes_up, bytes_down FROM traffic_hourly;");
        hourly.Should().ContainSingle();
        hourly[0]["bucket_start"].Should().Be(DayStart);
        hourly[0]["remote_class"].Should().Be("Wan");
        hourly[0]["bytes_up"].Should().Be(1_000L);
        hourly[0]["bytes_down"].Should().Be(5_000L);

        var daily = QueryAll("SELECT bucket_start, bytes_up, bytes_down FROM traffic_daily;");
        daily.Should().ContainSingle();
        daily[0]["bucket_start"].Should().Be(DayStart);
        daily[0]["bytes_up"].Should().Be(1_000L);
        daily[0]["bytes_down"].Should().Be(5_000L);
    }

    [Fact]
    public void Flush_MultipleSamplesInSameHour_AggregateIntoSingleHourlyRow()
    {
        _sink.Flush(Batch(
            newSessions: new[] { new NewSessionEntry(100, Ident(@"C:\a.exe"), DayStart, null) },
            samples: new[]
            {
                new PendingTrafficSample(100, DayStart + 60_000,  100, 1_000, RemoteClass.Wan),
                new PendingTrafficSample(100, DayStart + 120_000, 200, 2_000, RemoteClass.Wan),
                new PendingTrafficSample(100, DayStart + 180_000, 300, 3_000, RemoteClass.Wan),
            },
            nowUnixMs: DayStart + 240_000));

        var hourly = QueryAll("SELECT bytes_up, bytes_down FROM traffic_hourly;");
        hourly.Should().ContainSingle();
        hourly[0]["bytes_up"].Should().Be(600L);
        hourly[0]["bytes_down"].Should().Be(6_000L);
    }

    [Fact]
    public void Flush_SecondFlushSameHour_UpsertsCumulativeTotals()
    {
        // First flush: 100 / 1000 in hour 0
        _sink.Flush(Batch(
            newSessions: new[] { new NewSessionEntry(100, Ident(@"C:\a.exe"), DayStart, null) },
            samples: new[]
            {
                new PendingTrafficSample(100, DayStart, 100, 1_000, RemoteClass.Wan),
            },
            nowUnixMs: DayStart + 5_000));

        // Second flush for the SAME (app, hour, remote_class): should ACCUMULATE
        // not append a duplicate row.
        _sink.Flush(Batch(
            knownPidToSessionId: new Dictionary<int, int> { [100] = 1 },
            samples: new[]
            {
                new PendingTrafficSample(100, DayStart + 60_000, 50, 500, RemoteClass.Wan),
            },
            nowUnixMs: DayStart + 65_000));

        var hourly = QueryAll("SELECT bytes_up, bytes_down FROM traffic_hourly;");
        hourly.Should().ContainSingle("upsert should merge into the existing (app, hour, class) row");
        hourly[0]["bytes_up"].Should().Be(150L);
        hourly[0]["bytes_down"].Should().Be(1_500L);

        var daily = QueryAll("SELECT bytes_up, bytes_down FROM traffic_daily;");
        daily.Should().ContainSingle();
        daily[0]["bytes_up"].Should().Be(150L);
        daily[0]["bytes_down"].Should().Be(1_500L);
    }

    [Fact]
    public void Flush_SamplesSpanningMultipleHours_ProduceOneRowPerHour()
    {
        _sink.Flush(Batch(
            newSessions: new[] { new NewSessionEntry(100, Ident(@"C:\a.exe"), DayStart, null) },
            samples: new[]
            {
                new PendingTrafficSample(100, DayStart,             100, 0, RemoteClass.Wan),
                new PendingTrafficSample(100, DayStart + Hour,      200, 0, RemoteClass.Wan),
                new PendingTrafficSample(100, DayStart + Hour * 2,  300, 0, RemoteClass.Wan),
            },
            nowUnixMs: DayStart + Hour * 2 + 5_000));

        var hourly = QueryAll(
            "SELECT bucket_start, bytes_up FROM traffic_hourly ORDER BY bucket_start;");
        hourly.Should().HaveCount(3);
        hourly[0]["bucket_start"].Should().Be(DayStart);
        hourly[0]["bytes_up"].Should().Be(100L);
        hourly[1]["bucket_start"].Should().Be(DayStart + Hour);
        hourly[1]["bytes_up"].Should().Be(200L);
        hourly[2]["bucket_start"].Should().Be(DayStart + Hour * 2);
        hourly[2]["bytes_up"].Should().Be(300L);

        // All three hours roll into the same day.
        var daily = QueryAll("SELECT bucket_start, bytes_up FROM traffic_daily;");
        daily.Should().ContainSingle();
        daily[0]["bucket_start"].Should().Be(DayStart);
        daily[0]["bytes_up"].Should().Be(600L);
    }

    [Fact]
    public void Flush_DifferentRemoteClasses_KeepDistinctRollupRows()
    {
        // CLAUDE.md / PRD requirement: rollups are keyed by remote_class so the
        // WAN-vs-Local distinction is preserved into the rollup tiers.
        _sink.Flush(Batch(
            newSessions: new[] { new NewSessionEntry(100, Ident(@"C:\a.exe"), DayStart, null) },
            samples: new[]
            {
                new PendingTrafficSample(100, DayStart, 1_000, 0, RemoteClass.Wan),
                new PendingTrafficSample(100, DayStart,    50, 0, RemoteClass.Local),
            },
            nowUnixMs: DayStart + 5_000));

        var hourly = QueryAll(
            "SELECT remote_class, bytes_up FROM traffic_hourly ORDER BY remote_class;");
        hourly.Should().HaveCount(2);
        hourly[0]["remote_class"].Should().Be("Local");
        hourly[0]["bytes_up"].Should().Be(50L);
        hourly[1]["remote_class"].Should().Be("Wan");
        hourly[1]["bytes_up"].Should().Be(1_000L);
    }

    [Fact]
    public void Flush_TwoApps_RollupsKeyedByAppId()
    {
        _sink.Flush(Batch(
            newSessions: new[]
            {
                new NewSessionEntry(100, Ident(@"C:\a.exe"), DayStart, null),
                new NewSessionEntry(200, Ident(@"C:\b.exe"), DayStart, null),
            },
            samples: new[]
            {
                new PendingTrafficSample(100, DayStart, 1_000, 0, RemoteClass.Wan),
                new PendingTrafficSample(200, DayStart, 2_000, 0, RemoteClass.Wan),
            },
            nowUnixMs: DayStart + 5_000));

        var hourly = QueryAll(@"
            SELECT a.image_name, h.bytes_up
            FROM traffic_hourly h JOIN apps a ON a.app_id = h.app_id
            ORDER BY a.image_name;");
        hourly.Should().HaveCount(2);
        hourly[0]["image_name"].Should().Be("a.exe");
        hourly[0]["bytes_up"].Should().Be(1_000L);
        hourly[1]["image_name"].Should().Be("b.exe");
        hourly[1]["bytes_up"].Should().Be(2_000L);
    }

    [Fact]
    public void Flush_PreviouslyKnownSession_ResolvesAppIdForRollup()
    {
        // First flush opens the session; second flush uses the known map only.
        _sink.Flush(Batch(
            newSessions: new[] { new NewSessionEntry(100, Ident(@"C:\a.exe"), DayStart, null) },
            samples: new[] { new PendingTrafficSample(100, DayStart, 100, 0, RemoteClass.Wan) },
            nowUnixMs: DayStart + 5_000));

        // Second flush: session is already persisted; sink must look up app_id
        // from process_sessions to populate rollups correctly.
        _sink.Flush(Batch(
            knownPidToSessionId: new Dictionary<int, int> { [100] = 1 },
            samples: new[] { new PendingTrafficSample(100, DayStart + 60_000, 200, 0, RemoteClass.Wan) },
            nowUnixMs: DayStart + 65_000));

        var hourly = QueryAll("SELECT bytes_up FROM traffic_hourly;");
        hourly.Should().ContainSingle();
        hourly[0]["bytes_up"].Should().Be(300L,
            "second flush must still attribute its samples to the same app_id and accumulate the rollup");
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
