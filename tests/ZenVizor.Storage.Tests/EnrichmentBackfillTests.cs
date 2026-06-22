// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Storage;
using ZenVizor.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Headless tests for the Phase 2 Q10 backfill worker. Uses a fake
/// <see cref="IAppEnricher"/> so the test does not depend on real signed
/// binaries or filesystem state.
/// </summary>
public sealed class EnrichmentBackfillTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly SqliteFlushSink _sink;

    public EnrichmentBackfillTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-backfill-{Guid.NewGuid():N}.db");
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

    private static AppIdentity Unchecked(string path) =>
        new(path, Path.GetFileName(path), Publisher: null,
            SignatureStatus: "Unchecked", IsUserWritablePath: false);

    [Fact]
    public void Run_NoUncheckedRows_NoOp()
    {
        var enricher = new ScriptedEnricher();
        var backfill = new EnrichmentBackfill(_connections, enricher,
            interBatchDelay: TimeSpan.Zero);

        var result = backfill.Run();

        result.Updated.Should().Be(0);
        result.Skipped.Should().Be(0);
        enricher.CallCount.Should().Be(0);
    }

    [Fact]
    public void Run_UpdatesUncheckedRowsInPlace()
    {
        // Seed two Phase-1-shape rows.
        _sink.Flush(new FlushBatch(
            NewSessions: new[]
            {
                new NewSessionEntry(100, Unchecked(@"C:\a\chrome.exe"),   500, null),
                new NewSessionEntry(101, Unchecked(@"C:\b\dropper.exe"),  600, null),
            },
            KnownPidToSessionId: new Dictionary<int, int>(),
            Samples: Array.Empty<PendingTrafficSample>(),
            Connections: Array.Empty<PendingConnection>(),
            ClosedSessionIds: Array.Empty<int>(),
            FlushTimeUnixMs: 1000));

        var enricher = new ScriptedEnricher();
        enricher.SetByImageName("chrome.exe",  new EnrichmentResult("Google LLC", "Signed",   false));
        enricher.SetByImageName("dropper.exe", new EnrichmentResult(null,         "Unsigned", true));

        var result = new EnrichmentBackfill(_connections, enricher,
            interBatchDelay: TimeSpan.Zero).Run();

        result.Updated.Should().Be(2);
        result.Skipped.Should().Be(0);

        var rows = QueryAll(@"
            SELECT image_name, publisher, signature_status, is_user_writable_path
            FROM apps
            ORDER BY image_name;");
        rows.Should().HaveCount(2);
        rows[0]["image_name"].Should().Be("chrome.exe");
        rows[0]["publisher"].Should().Be("Google LLC");
        rows[0]["signature_status"].Should().Be("Signed");
        rows[0]["is_user_writable_path"].Should().Be(0L);
        rows[1]["image_name"].Should().Be("dropper.exe");
        rows[1]["publisher"].Should().Be(DBNull.Value);
        rows[1]["signature_status"].Should().Be("Unsigned");
        rows[1]["is_user_writable_path"].Should().Be(1L);
    }

    [Fact]
    public void Run_EnricherReturnsUnchecked_LeavesRowAlone()
    {
        _sink.Flush(new FlushBatch(
            NewSessions: new[]
            {
                new NewSessionEntry(100, Unchecked(@"C:\missing\ghost.exe"), 500, null),
            },
            KnownPidToSessionId: new Dictionary<int, int>(),
            Samples: Array.Empty<PendingTrafficSample>(),
            Connections: Array.Empty<PendingConnection>(),
            ClosedSessionIds: Array.Empty<int>(),
            FlushTimeUnixMs: 1000));

        var enricher = new ScriptedEnricher(); // default: returns Unchecked
        var result = new EnrichmentBackfill(_connections, enricher,
            interBatchDelay: TimeSpan.Zero).Run();

        result.Updated.Should().Be(0);
        result.Skipped.Should().Be(1);

        var rows = QueryAll("SELECT signature_status FROM apps;");
        rows[0]["signature_status"].Should().Be("Unchecked");
    }

    [Fact]
    public void Run_IsIdempotent()
    {
        _sink.Flush(new FlushBatch(
            NewSessions: new[]
            {
                new NewSessionEntry(100, Unchecked(@"C:\a\chrome.exe"), 500, null),
            },
            KnownPidToSessionId: new Dictionary<int, int>(),
            Samples: Array.Empty<PendingTrafficSample>(),
            Connections: Array.Empty<PendingConnection>(),
            ClosedSessionIds: Array.Empty<int>(),
            FlushTimeUnixMs: 1000));

        var enricher = new ScriptedEnricher();
        enricher.SetByImageName("chrome.exe", new EnrichmentResult("Google LLC", "Signed", false));
        var backfill = new EnrichmentBackfill(_connections, enricher,
            interBatchDelay: TimeSpan.Zero);

        backfill.Run();
        var secondRun = backfill.Run();

        secondRun.Updated.Should().Be(0);
        secondRun.Skipped.Should().Be(0);
        enricher.CallCount.Should().Be(1); // second run found nothing to do
    }

    [Fact]
    public void Run_BatchesWithoutDropping()
    {
        // 25 rows across batches of 10 — exercise the batching loop boundary.
        var entries = Enumerable.Range(0, 25)
            .Select(i => new NewSessionEntry(
                Pid: 1000 + i,
                App: Unchecked($@"C:\bin\app{i:D2}.exe"),
                StartTimeUnixMs: 500 + i,
                HostedServices: null))
            .ToList();
        _sink.Flush(new FlushBatch(
            NewSessions: entries,
            KnownPidToSessionId: new Dictionary<int, int>(),
            Samples: Array.Empty<PendingTrafficSample>(),
            Connections: Array.Empty<PendingConnection>(),
            ClosedSessionIds: Array.Empty<int>(),
            FlushTimeUnixMs: 1000));

        var enricher = new ScriptedEnricher();
        for (var i = 0; i < 25; i++)
        {
            enricher.SetByImageName($"app{i:D2}.exe",
                new EnrichmentResult($"Acme {i}", "Signed", false));
        }

        var result = new EnrichmentBackfill(_connections, enricher,
            interBatchDelay: TimeSpan.Zero,
            batchSize: 10).Run();

        result.Updated.Should().Be(25);
        var count = (long)Query("SELECT COUNT(*) FROM apps WHERE signature_status='Signed';")!;
        count.Should().Be(25);
    }

    [Fact]
    public void Run_RespectsMaxRowsPerRun_LeavesRemainderForNextStart()
    {
        // 20 Unchecked rows but cap to 7. The cap exists so a pathological
        // backlog can't pin the backfill worker — remaining rows are picked
        // up on subsequent service starts.
        var entries = Enumerable.Range(0, 20)
            .Select(i => new NewSessionEntry(
                Pid: 2000 + i,
                App: Unchecked($@"C:\bin\cap{i:D2}.exe"),
                StartTimeUnixMs: 500 + i,
                HostedServices: null))
            .ToList();
        _sink.Flush(new FlushBatch(
            NewSessions: entries,
            KnownPidToSessionId: new Dictionary<int, int>(),
            Samples: Array.Empty<PendingTrafficSample>(),
            Connections: Array.Empty<PendingConnection>(),
            ClosedSessionIds: Array.Empty<int>(),
            FlushTimeUnixMs: 1000));

        var enricher = new ScriptedEnricher();
        for (var i = 0; i < 20; i++)
        {
            enricher.SetByImageName($"cap{i:D2}.exe",
                new EnrichmentResult($"Acme {i}", "Signed", false));
        }

        var result = new EnrichmentBackfill(_connections, enricher,
            interBatchDelay: TimeSpan.Zero,
            batchSize: 5,
            maxRowsPerRun: 7).Run();

        result.Updated.Should().Be(7);
        var stillUnchecked = (long)Query("SELECT COUNT(*) FROM apps WHERE signature_status='Unchecked';")!;
        stillUnchecked.Should().Be(13);

        // A follow-up run picks up where we left off.
        var second = new EnrichmentBackfill(_connections, enricher,
            interBatchDelay: TimeSpan.Zero,
            batchSize: 5,
            maxRowsPerRun: 7).Run();
        second.Updated.Should().Be(7);
        ((long)Query("SELECT COUNT(*) FROM apps WHERE signature_status='Unchecked';")!).Should().Be(6);
    }

    private sealed class ScriptedEnricher : IAppEnricher
    {
        private readonly Dictionary<string, EnrichmentResult> _byImageName = new();
        public int CallCount { get; private set; }

        public void SetByImageName(string imageName, EnrichmentResult result) =>
            _byImageName[imageName] = result;

        public EnrichmentResult Enrich(ProcessImageInfo image)
        {
            CallCount++;
            return _byImageName.TryGetValue(image.ImageName, out var r)
                ? r
                : EnrichmentResult.Unchecked;
        }
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

    private object? Query(string sql)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
