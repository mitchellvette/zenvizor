using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Phase 8 — verifies the <c>connections.resolved_host</c> write semantics.
/// INSERT writes the value as-given; UPDATE uses
/// <c>COALESCE(resolved_host, excluded.resolved_host)</c> so a non-null
/// existing hostname is never overwritten and a null existing hostname is
/// filled by a later non-null arrival. See Phase 8 design decision D3 in
/// <c>docs/zenvizor-sprint-plan.md</c>.
/// </summary>
public sealed class ResolvedHostUpsertTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly SqliteFlushSink _sink;
    private readonly int _sessionId;

    public ResolvedHostUpsertTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-host-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
        _sink = new SqliteFlushSink(_connections);

        // Open a session that all the tests can attach connections to.
        var first = _sink.Flush(new FlushBatch(
            NewSessions: new[]
            {
                new NewSessionEntry(Pid: 100, App: new AppIdentity(@"C:\app.exe", "app.exe", null, "Unchecked", false),
                    StartTimeUnixMs: 500, HostedServices: null),
            },
            KnownPidToSessionId: new Dictionary<int, int>(),
            Samples: Array.Empty<PendingTrafficSample>(),
            Connections: Array.Empty<PendingConnection>(),
            ClosedSessionIds: Array.Empty<int>(),
            FlushTimeUnixMs: 1_000));
        _sessionId = first.NewPidToSessionId[100];
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

    private FlushBatch ConnectionBatch(string? resolvedHost, long firstSeen, long lastSeen, long up = 100, long down = 0) =>
        new(
            NewSessions:           Array.Empty<NewSessionEntry>(),
            KnownPidToSessionId:   new Dictionary<int, int> { [100] = _sessionId },
            Samples:               Array.Empty<PendingTrafficSample>(),
            Connections:           new[]
            {
                new PendingConnection(
                    Pid: 100, Protocol: Protocol.Tcp,
                    RemoteAddress: "8.8.8.8", RemotePort: 443,
                    RemoteClass: RemoteClass.Wan,
                    BytesUpDelta: up, BytesDownDelta: down,
                    FirstSeenUnixMs: firstSeen, LastSeenUnixMs: lastSeen,
                    ResolvedHost: resolvedHost),
            },
            ClosedSessionIds:      Array.Empty<int>(),
            FlushTimeUnixMs:       lastSeen);

    [Fact]
    public void Insert_with_hostname_persists_resolved_host()
    {
        _sink.Flush(ConnectionBatch(resolvedHost: "dns.google", firstSeen: 1_000, lastSeen: 1_500));

        var rows = QueryAll("SELECT resolved_host FROM connections;");
        rows.Should().ContainSingle();
        rows[0]["resolved_host"].Should().Be("dns.google");
    }

    [Fact]
    public void Insert_without_hostname_persists_null()
    {
        _sink.Flush(ConnectionBatch(resolvedHost: null, firstSeen: 1_000, lastSeen: 1_500));

        var rows = QueryAll("SELECT resolved_host FROM connections;");
        rows.Should().ContainSingle();
        rows[0]["resolved_host"].Should().Be(DBNull.Value);
    }

    [Fact]
    public void Late_arriving_hostname_fills_a_previously_null_row()
    {
        // First flush: no hostname yet (DNS source hadn't seen the response).
        _sink.Flush(ConnectionBatch(resolvedHost: null, firstSeen: 1_000, lastSeen: 1_500));
        // Second flush: DNS observation now exists; the row was already there.
        _sink.Flush(ConnectionBatch(resolvedHost: "dns.google", firstSeen: 2_000, lastSeen: 2_500));

        var rows = QueryAll("SELECT resolved_host FROM connections;");
        rows.Should().ContainSingle();
        rows[0]["resolved_host"].Should().Be("dns.google");
    }

    [Fact]
    public void Established_hostname_is_not_overwritten_by_a_later_null()
    {
        // Race scenario: first flush has hostname; second flush hits the DNS
        // miss window (eviction, race between subscribers). COALESCE preserves.
        _sink.Flush(ConnectionBatch(resolvedHost: "dns.google", firstSeen: 1_000, lastSeen: 1_500));
        _sink.Flush(ConnectionBatch(resolvedHost: null,         firstSeen: 2_000, lastSeen: 2_500));

        var rows = QueryAll("SELECT resolved_host FROM connections;");
        rows.Should().ContainSingle();
        rows[0]["resolved_host"].Should().Be("dns.google");
    }

    [Fact]
    public void Established_hostname_is_not_overwritten_by_a_later_different_hostname()
    {
        // COALESCE behaviour: first non-null wins. A later flush with a
        // different hostname (CDN flip) does not overwrite. This is
        // intentional per design decision D3 — the "freshest name" pivot
        // belongs in the read-side picker, not the write path.
        _sink.Flush(ConnectionBatch(resolvedHost: "outlook.office.com",     firstSeen: 1_000, lastSeen: 1_500));
        _sink.Flush(ConnectionBatch(resolvedHost: "outlook.office365.com",  firstSeen: 2_000, lastSeen: 2_500));

        var rows = QueryAll("SELECT resolved_host FROM connections;");
        rows.Should().ContainSingle();
        rows[0]["resolved_host"].Should().Be("outlook.office.com");
    }

    [Fact]
    public void Upsert_still_aggregates_bytes_correctly_alongside_hostname_coalesce()
    {
        // Sanity check that adding the resolved_host column to the upsert
        // didn't break the existing bytes/last_seen accumulation.
        _sink.Flush(ConnectionBatch(resolvedHost: "dns.google", firstSeen: 1_000, lastSeen: 1_500, up: 100, down: 0));
        _sink.Flush(ConnectionBatch(resolvedHost: null,         firstSeen: 9_999, lastSeen: 2_500, up: 250, down: 500));

        var rows = QueryAll("SELECT bytes_up, bytes_down, first_seen, last_seen, resolved_host FROM connections;");
        rows.Should().ContainSingle();
        rows[0]["bytes_up"].Should().Be(350L);
        rows[0]["bytes_down"].Should().Be(500L);
        rows[0]["first_seen"].Should().Be(1_000L);
        rows[0]["last_seen"].Should().Be(2_500L);
        rows[0]["resolved_host"].Should().Be("dns.google");
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
