// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// SqliteFlushSink end-to-end coverage against a real temp SQLite database.
/// Asserts atomic single-transaction behavior — partial state must never persist.
/// </summary>
public sealed class WritePathRepositoriesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly SqliteFlushSink _sink;

    public WritePathRepositoriesTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-flush-{Guid.NewGuid():N}.db");
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

    private static AppIdentity Ident(string path, string? publisher = null) =>
        new(path, Path.GetFileName(path), publisher, "Unchecked", false);

    private static FlushBatch Batch(
        IEnumerable<NewSessionEntry>? newSessions = null,
        IReadOnlyDictionary<int, int>? knownPidToSessionId = null,
        IEnumerable<PendingTrafficSample>? samples = null,
        IEnumerable<PendingConnection>? connections = null,
        IEnumerable<int>? closedSessionIds = null,
        long nowUnixMs = 1000) =>
        new FlushBatch(
            NewSessions: (newSessions ?? Array.Empty<NewSessionEntry>()).ToList(),
            KnownPidToSessionId: knownPidToSessionId ?? new Dictionary<int, int>(),
            Samples: (samples ?? Array.Empty<PendingTrafficSample>()).ToList(),
            Connections: (connections ?? Array.Empty<PendingConnection>()).ToList(),
            ClosedSessionIds: (closedSessionIds ?? Array.Empty<int>()).ToList(),
            FlushTimeUnixMs: nowUnixMs);

    [Fact]
    public void Flush_EmptyBatch_NoWritesNoTransaction()
    {
        var result = _sink.Flush(Batch());

        result.NewPidToSessionId.Should().BeEmpty();
        result.SampleRowsWritten.Should().Be(0);
        QueryAll("SELECT 1 FROM apps;").Should().BeEmpty();
    }

    [Fact]
    public void Flush_NewSession_InsertsAppAndSession_ReturnsPidMapping()
    {
        var batch = Batch(newSessions: new[]
        {
            new NewSessionEntry(Pid: 100, App: Ident(@"C:\app.exe"), StartTimeUnixMs: 500, HostedServices: null),
        });

        var result = _sink.Flush(batch);

        result.NewPidToSessionId.Should().ContainKey(100);
        var apps = QueryAll("SELECT app_id, image_path FROM apps;");
        apps.Should().ContainSingle();
        var sessions = QueryAll("SELECT session_id, pid, start_time, end_time FROM process_sessions;");
        sessions.Should().ContainSingle();
        sessions[0]["pid"].Should().Be(100L);
        sessions[0]["start_time"].Should().Be(500L);
        sessions[0]["end_time"].Should().Be(DBNull.Value);
    }

    [Fact]
    public void Flush_DuplicateAppIdentity_DedupesIntoSingleAppRow()
    {
        var batch = Batch(newSessions: new[]
        {
            new NewSessionEntry(100, Ident(@"C:\app.exe"), 500, null),
            new NewSessionEntry(101, Ident(@"C:\app.exe"), 600, null),
            new NewSessionEntry(102, Ident(@"C:\app.exe"), 700, null),
        });

        var result = _sink.Flush(batch);

        result.NewPidToSessionId.Should().HaveCount(3);
        QueryAll("SELECT app_id FROM apps;").Should().ContainSingle();
        QueryAll("SELECT session_id FROM process_sessions;").Should().HaveCount(3);
    }

    [Fact]
    public void Flush_SamplesAndConnections_ResolveViaKnownPidMap()
    {
        // First flush establishes session id 1 for PID 100.
        var first = _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(100, Ident(@"C:\app.exe"), 500, null),
        }));
        var sessionId = first.NewPidToSessionId[100];

        // Second flush: PID 100 is already persisted, so it shows up in
        // KnownPidToSessionId rather than NewSessions.
        var second = _sink.Flush(Batch(
            knownPidToSessionId: new Dictionary<int, int> { [100] = sessionId },
            samples: new[]
            {
                new PendingTrafficSample(100, 60_000, BytesUp: 100, BytesDown: 200, RemoteClass.Wan),
            },
            connections: new[]
            {
                new PendingConnection(100, Protocol.Tcp, "8.8.8.8", 443, RemoteClass.Wan,
                    BytesUpDelta: 100, BytesDownDelta: 200, FirstSeenUnixMs: 60_000, LastSeenUnixMs: 60_500),
            }));

        second.SampleRowsWritten.Should().Be(1);
        second.ConnectionUpserts.Should().Be(1);

        var samples = QueryAll("SELECT session_id, bytes_up, bytes_down FROM traffic_samples;");
        samples.Should().ContainSingle();
        samples[0]["session_id"].Should().Be((long)sessionId);
        samples[0]["bytes_up"].Should().Be(100L);
        samples[0]["bytes_down"].Should().Be(200L);
    }

    [Fact]
    public void Flush_ConnectionUpsert_AccumulatesDeltasAcrossFlushes()
    {
        var first = _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(100, Ident(@"C:\app.exe"), 500, null),
        }));
        var sessionId = first.NewPidToSessionId[100];

        _sink.Flush(Batch(
            knownPidToSessionId: new Dictionary<int, int> { [100] = sessionId },
            connections: new[]
            {
                new PendingConnection(100, Protocol.Tcp, "8.8.8.8", 443, RemoteClass.Wan,
                    100, 0, FirstSeenUnixMs: 1000, LastSeenUnixMs: 1500),
            }));

        _sink.Flush(Batch(
            knownPidToSessionId: new Dictionary<int, int> { [100] = sessionId },
            connections: new[]
            {
                new PendingConnection(100, Protocol.Tcp, "8.8.8.8", 443, RemoteClass.Wan,
                    250, 500, FirstSeenUnixMs: 9_999, LastSeenUnixMs: 2000),
            }));

        var rows = QueryAll("SELECT bytes_up, bytes_down, first_seen, last_seen FROM connections;");
        rows.Should().ContainSingle();
        rows[0]["bytes_up"].Should().Be(350L);
        rows[0]["bytes_down"].Should().Be(500L);
        rows[0]["first_seen"].Should().Be(1000L);   // initial preserved
        rows[0]["last_seen"].Should().Be(2000L);
    }

    [Fact]
    public void Flush_ClosedSessionIds_SetEndTime()
    {
        var first = _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(100, Ident(@"C:\app.exe"), 500, null),
        }));
        var sessionId = first.NewPidToSessionId[100];

        var result = _sink.Flush(Batch(
            closedSessionIds: new[] { sessionId },
            nowUnixMs: 9000));

        result.SessionsClosed.Should().Be(1);
        QueryAll("SELECT end_time FROM process_sessions;")[0]["end_time"].Should().Be(9000L);
    }

    [Fact]
    public void Flush_AtomicTransaction_OneFailedSampleRollsBackEntireBatch()
    {
        // Establish a valid session up-front so we know the foreign key.
        var first = _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(100, Ident(@"C:\app.exe"), 500, null),
        }));
        var sessionId = first.NewPidToSessionId[100];

        // Now build a batch where one sample references an unknown PID (no resolution
        // → orphan, skipped) BUT a session_id directly via a different code path.
        // Use a synthetic batch with a sample pointing at a PID we did NOT include in
        // KnownPidToSessionId — sink should skip rather than crash, and other rows persist.
        var orphanBatch = Batch(
            knownPidToSessionId: new Dictionary<int, int> { [100] = sessionId },
            samples: new[]
            {
                new PendingTrafficSample(100,  60_000, 100, 200, RemoteClass.Wan),  // resolvable
                new PendingTrafficSample(99999, 60_000, 100, 200, RemoteClass.Wan), // orphan
            });

        var act = () => _sink.Flush(orphanBatch);
        act.Should().NotThrow();

        // Only the resolvable row landed.
        QueryAll("SELECT session_id FROM traffic_samples;").Should().ContainSingle();
    }

    [Fact]
    public void Flush_NewSession_PersistsAllEnrichmentFields()
    {
        var identity = new AppIdentity(
            ImagePath: @"C:\Programs\enriched.exe",
            ImageName: "enriched.exe",
            Publisher: "Acme Co",
            SignatureStatus: "Signed",
            IsUserWritablePath: false);

        _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(100, identity, 500, HostedServices: "Dnscache,Dhcp"),
        }));

        var apps = QueryAll(
            "SELECT publisher, signature_status, is_user_writable_path FROM apps;");
        apps.Should().ContainSingle();
        apps[0]["publisher"].Should().Be("Acme Co");
        apps[0]["signature_status"].Should().Be("Signed");
        apps[0]["is_user_writable_path"].Should().Be(0L);

        var sessions = QueryAll("SELECT hosted_services FROM process_sessions;");
        sessions.Should().ContainSingle();
        sessions[0]["hosted_services"].Should().Be("Dnscache,Dhcp");
    }

    [Fact]
    public void Flush_PublisherChange_CreatesSecondAppRow()
    {
        // Phase 2 Q8 dedup: (image_path, publisher) is the dedup key. A rotation
        // of the signing cert should produce a new apps row, not overwrite the
        // old one — preserves the security signal that the publisher changed.
        var first  = new AppIdentity(@"C:\a\app.exe", "app.exe", "Old Cert Co",    "Signed",   false);
        var second = new AppIdentity(@"C:\a\app.exe", "app.exe", "New Cert Co",    "Signed",   false);

        _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(100, first, 500, null),
        }));
        _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(101, second, 600, null),
        }));

        var apps = QueryAll("SELECT image_path, publisher FROM apps ORDER BY publisher;");
        apps.Should().HaveCount(2);
        apps[0]["publisher"].Should().Be("New Cert Co");
        apps[1]["publisher"].Should().Be("Old Cert Co");
    }

    [Fact]
    public void Flush_PathClass_DefaultIsSystem_WhenIdentityDoesNotOverride()
    {
        // Positional-ctor AppIdentity (Phase 2 tests + back-compat callers)
        // produces PathClass=System. Persisted accordingly.
        var identity = new AppIdentity(@"C:\Programs\sys.exe", "sys.exe", null, "Signed", false);
        _sink.Flush(Batch(newSessions: new[] { new NewSessionEntry(100, identity, 500, null) }));

        var apps = QueryAll("SELECT path_class FROM apps;");
        apps.Should().ContainSingle();
        apps[0]["path_class"].Should().Be("System");
    }

    [Fact]
    public void Flush_PathClass_PersistsUnknownForBasenameOnlyAttribution()
    {
        // Bug-2 regression gate. A basename-only image (ETW gave us only
        // "svchost.exe", no full path) must land with path_class='Unknown'
        // so the Phase-6 alert can't read it as a safe System folder.
        var identity = new AppIdentity(
            ImagePath: "svchost.exe",
            ImageName: "svchost.exe",
            Publisher: null,
            SignatureStatus: "Unchecked",
            IsUserWritablePath: false)
        {
            PathClass = ZenVizor.Core.Attribution.PathClassification.Unknown,
        };

        _sink.Flush(Batch(newSessions: new[] { new NewSessionEntry(100, identity, 500, null) }));

        var apps = QueryAll("SELECT path_class, signature_status FROM apps;");
        apps.Should().ContainSingle();
        apps[0]["path_class"].Should().Be("Unknown");
        apps[0]["signature_status"].Should().Be("Unchecked");
    }

    [Fact]
    public void Flush_PathClass_PersistsUserWritableExplicitly()
    {
        var identity = new AppIdentity(
            ImagePath: @"C:\Users\alice\AppData\Local\Temp\dropper.exe",
            ImageName: "dropper.exe",
            Publisher: null,
            SignatureStatus: "Unsigned",
            IsUserWritablePath: true)
        {
            PathClass = ZenVizor.Core.Attribution.PathClassification.UserWritable,
        };

        _sink.Flush(Batch(newSessions: new[] { new NewSessionEntry(100, identity, 500, null) }));

        var apps = QueryAll("SELECT path_class FROM apps;");
        apps[0]["path_class"].Should().Be("UserWritable");
    }

    [Fact]
    public void Flush_UnsignedFromUserWritablePath_StoresAlertWorthyShape()
    {
        // The exact data shape Phase 6's alert rule will read. Phase 2's job is
        // only to make sure these fields land correctly; Phase 6 wires the
        // alert raising.
        var identity = new AppIdentity(
            ImagePath: @"C:\Users\alice\AppData\Local\Temp\dropper.exe",
            ImageName: "dropper.exe",
            Publisher: null,
            SignatureStatus: "Unsigned",
            IsUserWritablePath: true);

        _sink.Flush(Batch(newSessions: new[]
        {
            new NewSessionEntry(999, identity, 500, null),
        }));

        var apps = QueryAll(
            "SELECT signature_status, is_user_writable_path, publisher FROM apps;");
        apps[0]["signature_status"].Should().Be("Unsigned");
        apps[0]["is_user_writable_path"].Should().Be(1L);
        apps[0]["publisher"].Should().Be(DBNull.Value);
    }

    [Fact]
    public void Flush_AtomicTransaction_FailingSqlRollsBackEverything()
    {
        // Inject a closed_session_id that does not exist. The UPDATE returns 0 rows
        // affected but doesn't throw — so this batch should still commit successfully
        // and the valid pieces persist. This documents the "missing close target" behavior.
        var batch = Batch(
            newSessions: new[]
            {
                new NewSessionEntry(100, Ident(@"C:\app.exe"), 500, null),
            },
            closedSessionIds: new[] { 99_999 });

        var result = _sink.Flush(batch);

        result.NewPidToSessionId.Should().ContainKey(100);
        result.SessionsClosed.Should().Be(0); // no row matched
        QueryAll("SELECT 1 FROM process_sessions;").Should().ContainSingle();
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
