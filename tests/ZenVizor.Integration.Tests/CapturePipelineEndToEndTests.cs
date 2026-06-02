using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Capture;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Sprint Plan Phase 1 CI gate: "Synthetic event streams produce exact expected
/// traffic_samples and connections rows." Wires the SyntheticCaptureSource →
/// real aggregator → real SQLite via SqliteFlushSink (single transaction per
/// flush) and asserts exact row contents.
/// </summary>
public sealed class CapturePipelineEndToEndTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;

    public CapturePipelineEndToEndTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-e2e-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
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
    public async Task EndToEnd_SampleRows_HaveExactExpectedValues()
    {
        var (aggregator, _, resolver, snapshotSource) = BuildPipeline();

        var pidA = 1001;
        var pidBwrong = 999;
        var pidBcorrect = 1002;

        var localA  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51_000);
        var remoteAwan = new IPEndPoint(IPAddress.Parse("8.8.8.8"),  443);
        var localB  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 52_000);
        var remoteBlan = new IPEndPoint(IPAddress.Parse("10.0.0.99"), 445);
        var localC  = new IPEndPoint(IPAddress.Parse("fd00::5"), 53_000);
        var remoteCv6Wan = new IPEndPoint(IPAddress.Parse("2606:4700:4700::1111"), 443);

        resolver.Set(new ProcessImageInfo(pidA,        @"C:\Programs\webapp.exe", "webapp.exe", 100));
        resolver.Set(new ProcessImageInfo(pidBcorrect, @"C:\Programs\smbcli.exe", "smbcli.exe", 200));

        snapshotSource.SetSnapshot(new PidTableSnapshot(60_000, new[]
        {
            new PidTableEntry(Protocol.Tcp, localB, OwningPid: pidBcorrect),
        }));

        var source = new SyntheticCaptureSource();
        source.TryEmit(new NetworkObservation(60_500,  pidA,      Protocol.Tcp, localA, remoteAwan,    Direction.Up,   500));
        source.TryEmit(new NetworkObservation(60_500,  pidA,      Protocol.Tcp, localA, remoteAwan,    Direction.Down, 1500));
        source.TryEmit(new NetworkObservation(70_000,  pidBwrong, Protocol.Tcp, localB, remoteBlan,    Direction.Down, 4096));
        source.TryEmit(new NetworkObservation(80_000,  pidA,      Protocol.Tcp, localC, remoteCv6Wan,  Direction.Up,   100));
        source.TryEmit(new NetworkObservation(125_000, pidA,      Protocol.Tcp, localA, remoteAwan,    Direction.Up,   250));
        source.Complete();

        await foreach (var obs in source.ObserveAsync(CancellationToken.None))
        {
            aggregator.Observe(obs);
        }
        aggregator.Flush(130_000);

        var rows = QueryAll(@"
            SELECT s.bucket_start, s.remote_class, s.bytes_up, s.bytes_down, a.image_name
            FROM traffic_samples s
            JOIN process_sessions ps ON ps.session_id = s.session_id
            JOIN apps a ON a.app_id = ps.app_id
            ORDER BY s.bucket_start, a.image_name, s.remote_class;");

        rows.Should().HaveCount(3);

        rows[0]["bucket_start"].Should().Be(60_000L);
        rows[0]["image_name"].Should().Be("smbcli.exe");
        rows[0]["remote_class"].Should().Be("Local");
        rows[0]["bytes_up"].Should().Be(0L);
        rows[0]["bytes_down"].Should().Be(4096L);

        rows[1]["bucket_start"].Should().Be(60_000L);
        rows[1]["image_name"].Should().Be("webapp.exe");
        rows[1]["remote_class"].Should().Be("Wan");
        rows[1]["bytes_up"].Should().Be(600L);
        rows[1]["bytes_down"].Should().Be(1500L);

        rows[2]["bucket_start"].Should().Be(120_000L);
        rows[2]["image_name"].Should().Be("webapp.exe");
        rows[2]["remote_class"].Should().Be("Wan");
        rows[2]["bytes_up"].Should().Be(250L);
        rows[2]["bytes_down"].Should().Be(0L);
    }

    [Fact]
    public async Task EndToEnd_ConnectionRows_HaveExactExpectedAggregates()
    {
        var (aggregator, _, resolver, _) = BuildPipeline();

        var pidA = 1001;
        var localA = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51_000);
        var remoteAwan = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        var localC = new IPEndPoint(IPAddress.Parse("fd00::5"), 53_000);
        var remoteCv6 = new IPEndPoint(IPAddress.Parse("2606:4700:4700::1111"), 443);

        resolver.Set(new ProcessImageInfo(pidA, @"C:\Programs\webapp.exe", "webapp.exe", 100));

        var source = new SyntheticCaptureSource();
        source.TryEmit(new NetworkObservation(60_500, pidA, Protocol.Tcp, localA, remoteAwan, Direction.Up,   500));
        source.TryEmit(new NetworkObservation(60_500, pidA, Protocol.Tcp, localA, remoteAwan, Direction.Down, 1500));
        source.TryEmit(new NetworkObservation(80_000, pidA, Protocol.Tcp, localC, remoteCv6,  Direction.Up,   100));
        source.Complete();

        await foreach (var obs in source.ObserveAsync(CancellationToken.None))
        {
            aggregator.Observe(obs);
        }
        aggregator.Flush(90_000);

        var rows = QueryAll(@"
            SELECT protocol, remote_addr, remote_port, remote_class, bytes_up, bytes_down
            FROM connections
            ORDER BY remote_addr;");

        rows.Should().HaveCount(2);

        rows[0]["protocol"].Should().Be("TCP");
        rows[0]["remote_addr"].Should().Be("2606:4700:4700::1111");
        rows[0]["remote_port"].Should().Be(443L);
        rows[0]["remote_class"].Should().Be("Wan");
        rows[0]["bytes_up"].Should().Be(100L);
        rows[0]["bytes_down"].Should().Be(0L);

        rows[1]["remote_addr"].Should().Be("8.8.8.8");
        rows[1]["bytes_up"].Should().Be(500L);
        rows[1]["bytes_down"].Should().Be(1500L);
    }

    [Fact]
    public async Task EndToEnd_PidReuse_ProducesTwoDistinctSessions()
    {
        var (aggregator, _, resolver, _) = BuildPipeline();

        var pid = 9999;
        var local = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12_345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        resolver.Set(new ProcessImageInfo(pid, @"C:\a\old.exe", "old.exe", StartTimeUnixMs: 100));
        aggregator.Observe(new NetworkObservation(60_500, pid, Protocol.Tcp, local, remote, Direction.Up, 100));
        aggregator.Flush(65_000);

        // Process exits, new process inherits PID 9999 with a later start time.
        resolver.Set(new ProcessImageInfo(pid, @"C:\a\new.exe", "new.exe", StartTimeUnixMs: 60_000));
        aggregator.Observe(new NetworkObservation(125_000, pid, Protocol.Tcp, local, remote, Direction.Up, 200));
        aggregator.Flush(130_000);

        var rows = QueryAll(@"
            SELECT ps.pid, ps.start_time, ps.end_time, a.image_name
            FROM process_sessions ps
            JOIN apps a ON a.app_id = ps.app_id
            ORDER BY ps.start_time;");

        rows.Should().HaveCount(2);
        rows[0]["pid"].Should().Be(9999L);
        rows[0]["start_time"].Should().Be(100L);
        rows[0]["image_name"].Should().Be("old.exe");
        rows[0]["end_time"].Should().NotBe(DBNull.Value);

        rows[1]["pid"].Should().Be(9999L);
        rows[1]["start_time"].Should().Be(60_000L);
        rows[1]["image_name"].Should().Be("new.exe");
        rows[1]["end_time"].Should().Be(DBNull.Value);
    }

    [Fact]
    public void Observe_DoesNotTouchDatabase_OnlyFlushWrites()
    {
        // Architectural guard: Observe() must not produce any writes. Take the
        // file's last-write timestamp before Observe and confirm it doesn't change.
        var (aggregator, _, resolver, _) = BuildPipeline();
        resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));

        SqliteConnection.ClearAllPools();
        var sizeBeforeObserve = new FileInfo(_dbPath).Length;
        var walBefore = File.Exists(_dbPath + "-wal") ? new FileInfo(_dbPath + "-wal").Length : 0L;

        for (var i = 0; i < 100; i++)
        {
            aggregator.Observe(new NetworkObservation(
                60_500 + i, 100, Protocol.Tcp,
                new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345),
                new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443),
                Direction.Up, 100));
        }

        SqliteConnection.ClearAllPools();
        var sizeAfterObserve = new FileInfo(_dbPath).Length;
        var walAfter = File.Exists(_dbPath + "-wal") ? new FileInfo(_dbPath + "-wal").Length : 0L;

        sizeAfterObserve.Should().Be(sizeBeforeObserve, "Observe() must not write to zenvizor.db");
        walAfter.Should().Be(walBefore, "Observe() must not write to zenvizor.db-wal");
    }

    private (TrafficAggregator Aggregator,
             SessionTracker Tracker,
             InMemoryProcessImageResolver Resolver,
             InMemoryPidTableSource SnapshotSource) BuildPipeline()
    {
        var sink = new SqliteFlushSink(_connections);
        var resolver = new InMemoryProcessImageResolver();
        var snapshotSource = new InMemoryPidTableSource();
        var tracker = new SessionTracker(resolver);
        var aggregator = new TrafficAggregator(tracker, new PidCorrector(), snapshotSource, sink);
        return (aggregator, tracker, resolver, snapshotSource);
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
