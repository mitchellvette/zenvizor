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
/// Sprint Plan Phase 3 CI gate: "Snapshot is served from the in-memory
/// aggregate (test asserts no SQLite read on the snapshot path)."
/// Wraps <see cref="ConnectionFactory"/> with a tracking subclass that throws
/// on any <see cref="ConnectionFactory.Open"/> call after the guard is armed.
/// Mirrors the Phase-1 "Observe must not write to disk" architectural guard.
/// </summary>
public sealed class SnapshotPathDoesNotReadSqliteTests : IDisposable
{
    private readonly string _dbPath;
    private readonly OpenAssertingConnectionFactory _connections;

    public SnapshotPathDoesNotReadSqliteTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-snap-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new OpenAssertingConnectionFactory(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    [Fact]
    public void TakeActivitySnapshot_NeverOpensASqliteConnection()
    {
        var sink = new SqliteFlushSink(_connections);
        var resolver = new InMemoryProcessImageResolver();
        var snapshotSource = new InMemoryPidTableSource();
        var tracker = new SessionTracker(resolver);
        var aggregator = new TrafficAggregator(
            tracker, new PidCorrector(), snapshotSource, sink,
            nowProvider: () => 5_000);

        resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));
        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        aggregator.Observe(new NetworkObservation(
            1_000, 100, Protocol.Tcp, local, remote, Direction.Up, 5_000));

        // One real flush to populate the rolling window. After this point
        // any further Open() is forbidden.
        aggregator.Flush(5_000);
        var opensFromFlush = _connections.OpenCount;
        opensFromFlush.Should().BeGreaterThan(0, "flush should have used SQLite");

        // ARM the guard.
        _connections.ForbidOpens = true;

        // Take 50 back-to-back snapshots. Add some observes between them too,
        // since Observe() also must not open a connection.
        for (var i = 0; i < 50; i++)
        {
            aggregator.Observe(new NetworkObservation(
                6_000 + i, 100, Protocol.Tcp, local, remote, Direction.Up, 10));
            var snap = aggregator.TakeActivitySnapshot();
            snap.Should().NotBeNull();
        }

        _connections.OpenCount.Should().Be(opensFromFlush,
            "TakeActivitySnapshot and Observe must not open any SQLite connection");
    }

    private sealed class OpenAssertingConnectionFactory : ConnectionFactory
    {
        public OpenAssertingConnectionFactory(string databasePath) : base(databasePath) { }

        public int OpenCount { get; private set; }
        public bool ForbidOpens { get; set; }

        public override SqliteConnection Open()
        {
            if (ForbidOpens)
            {
                throw new InvalidOperationException(
                    "ConnectionFactory.Open() called while snapshot-path guard is armed — " +
                    "the snapshot path must NOT touch SQLite.");
            }
            OpenCount++;
            return base.Open();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
