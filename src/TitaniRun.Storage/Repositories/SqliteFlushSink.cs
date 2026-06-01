using Microsoft.Data.Sqlite;
using TitaniRun.Core.Observations;
using TitaniRun.Core.Storage;

namespace TitaniRun.Storage.Repositories;

/// <summary>
/// Single-transaction-per-flush implementation of <see cref="IFlushSink"/>.
/// All writes for a flush tick — apps dedup, session opens, sample inserts,
/// connection upserts, session closes — happen inside ONE
/// <see cref="SqliteTransaction"/>. Eliminates the per-PID-lifecycle write
/// path that violated CLAUDE.md invariant #4 in spirit.
/// </summary>
public sealed class SqliteFlushSink : IFlushSink
{
    private readonly ConnectionFactory _connections;

    public SqliteFlushSink(ConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public FlushBatchResult Flush(FlushBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.NewSessions.Count == 0
            && batch.Samples.Count == 0
            && batch.Connections.Count == 0
            && batch.ClosedSessionIds.Count == 0)
        {
            return new FlushBatchResult(
                NewPidToSessionId: new Dictionary<int, int>(),
                SampleRowsWritten: 0,
                ConnectionUpserts: 0,
                SessionsClosed: 0);
        }

        using var connection = _connections.Open();
        using var transaction = connection.BeginTransaction();

        var newPidToSessionId = InsertNewSessions(connection, transaction, batch.NewSessions, batch.FlushTimeUnixMs);

        // Resolution map for samples/connections: new sessions take precedence
        // over already-persisted snapshot.
        var pidToSessionId = new Dictionary<int, int>(batch.KnownPidToSessionId);
        foreach (var (pid, sid) in newPidToSessionId)
        {
            pidToSessionId[pid] = sid;
        }

        var sampleRowsWritten = InsertSamples(connection, transaction, batch.Samples, pidToSessionId);
        var connectionUpserts = UpsertConnections(connection, transaction, batch.Connections, pidToSessionId);
        var sessionsClosed = CloseSessions(connection, transaction, batch.ClosedSessionIds, batch.FlushTimeUnixMs);

        transaction.Commit();

        return new FlushBatchResult(
            NewPidToSessionId: newPidToSessionId,
            SampleRowsWritten: sampleRowsWritten,
            ConnectionUpserts: connectionUpserts,
            SessionsClosed: sessionsClosed);
    }

    private static Dictionary<int, int> InsertNewSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<NewSessionEntry> newSessions,
        long flushTimeUnixMs)
    {
        var result = new Dictionary<int, int>(newSessions.Count);
        if (newSessions.Count == 0)
        {
            return result;
        }

        // Build a per-flush AppIdentity → app_id cache so we don't re-look-up
        // the same app for every session in the batch.
        var appCache = new Dictionary<(string Path, string Publisher), int>();

        foreach (var entry in newSessions)
        {
            var appId = GetOrCreateAppId(connection, transaction, entry.App, flushTimeUnixMs, appCache);
            var sessionId = InsertSession(connection, transaction, appId, entry);
            result[entry.Pid] = sessionId;
        }

        return result;
    }

    private static int GetOrCreateAppId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AppIdentity identity,
        long nowUnixMs,
        Dictionary<(string Path, string Publisher), int> cache)
    {
        var cacheKey = (identity.ImagePath, identity.Publisher ?? string.Empty);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT app_id FROM apps
                WHERE image_path = $path
                  AND IFNULL(publisher, '') = IFNULL($publisher, '');
                """;
            select.Parameters.AddWithValue("$path", identity.ImagePath);
            select.Parameters.AddWithValue("$publisher", (object?)identity.Publisher ?? DBNull.Value);
            var existing = select.ExecuteScalar();
            if (existing is long existingId)
            {
                using var bump = connection.CreateCommand();
                bump.Transaction = transaction;
                bump.CommandText = "UPDATE apps SET last_seen = $now WHERE app_id = $id;";
                bump.Parameters.AddWithValue("$now", nowUnixMs);
                bump.Parameters.AddWithValue("$id", existingId);
                bump.ExecuteNonQuery();
                cache[cacheKey] = (int)existingId;
                return (int)existingId;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO apps
                (image_path, image_name, publisher, signature_status, is_user_writable_path,
                 first_seen, last_seen)
            VALUES
                ($path, $name, $publisher, $sig, $userWritable, $now, $now);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$path", identity.ImagePath);
        insert.Parameters.AddWithValue("$name", identity.ImageName);
        insert.Parameters.AddWithValue("$publisher", (object?)identity.Publisher ?? DBNull.Value);
        insert.Parameters.AddWithValue("$sig", identity.SignatureStatus);
        insert.Parameters.AddWithValue("$userWritable", identity.IsUserWritablePath ? 1 : 0);
        insert.Parameters.AddWithValue("$now", nowUnixMs);
        var newId = (int)(long)insert.ExecuteScalar()!;
        cache[cacheKey] = newId;
        return newId;
    }

    private static int InsertSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int appId,
        NewSessionEntry entry)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO process_sessions
                (app_id, pid, start_time, end_time, hosted_services)
            VALUES
                ($appId, $pid, $start, NULL, $hosted);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$appId", appId);
        cmd.Parameters.AddWithValue("$pid", entry.Pid);
        cmd.Parameters.AddWithValue("$start", entry.StartTimeUnixMs);
        cmd.Parameters.AddWithValue("$hosted", (object?)entry.HostedServices ?? DBNull.Value);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private static int InsertSamples(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PendingTrafficSample> samples,
        Dictionary<int, int> pidToSessionId)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO traffic_samples
                (session_id, bucket_start, bytes_up, bytes_down, remote_class)
            VALUES
                ($session, $bucket, $up, $down, $class);
            """;
        var pSession = cmd.Parameters.Add("$session", SqliteType.Integer);
        var pBucket  = cmd.Parameters.Add("$bucket",  SqliteType.Integer);
        var pUp      = cmd.Parameters.Add("$up",      SqliteType.Integer);
        var pDown    = cmd.Parameters.Add("$down",    SqliteType.Integer);
        var pClass   = cmd.Parameters.Add("$class",   SqliteType.Text);
        cmd.Prepare();

        var written = 0;
        foreach (var s in samples)
        {
            if (!pidToSessionId.TryGetValue(s.Pid, out var sessionId))
            {
                // Orphan — shouldn't happen, but skip rather than crash the flush.
                continue;
            }
            pSession.Value = sessionId;
            pBucket.Value  = s.BucketStartUnixMs;
            pUp.Value      = s.BytesUp;
            pDown.Value    = s.BytesDown;
            pClass.Value   = s.RemoteClass.ToStorageString();
            cmd.ExecuteNonQuery();
            written++;
        }
        return written;
    }

    private static int UpsertConnections(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PendingConnection> connections,
        Dictionary<int, int> pidToSessionId)
    {
        if (connections.Count == 0)
        {
            return 0;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO connections
                (session_id, protocol, remote_addr, remote_port, remote_class,
                 bytes_up, bytes_down, first_seen, last_seen)
            VALUES
                ($session, $proto, $addr, $port, $class,
                 $up, $down, $first, $last)
            ON CONFLICT (session_id, protocol, remote_addr, remote_port) DO UPDATE SET
                bytes_up   = bytes_up   + excluded.bytes_up,
                bytes_down = bytes_down + excluded.bytes_down,
                last_seen  = MAX(last_seen, excluded.last_seen);
            """;
        var pSession = cmd.Parameters.Add("$session", SqliteType.Integer);
        var pProto   = cmd.Parameters.Add("$proto",   SqliteType.Text);
        var pAddr    = cmd.Parameters.Add("$addr",    SqliteType.Text);
        var pPort    = cmd.Parameters.Add("$port",    SqliteType.Integer);
        var pClass   = cmd.Parameters.Add("$class",   SqliteType.Text);
        var pUp      = cmd.Parameters.Add("$up",      SqliteType.Integer);
        var pDown    = cmd.Parameters.Add("$down",    SqliteType.Integer);
        var pFirst   = cmd.Parameters.Add("$first",   SqliteType.Integer);
        var pLast    = cmd.Parameters.Add("$last",    SqliteType.Integer);
        cmd.Prepare();

        var written = 0;
        foreach (var c in connections)
        {
            if (!pidToSessionId.TryGetValue(c.Pid, out var sessionId))
            {
                continue;
            }
            pSession.Value = sessionId;
            pProto.Value   = ProtocolToText(c.Protocol);
            pAddr.Value    = c.RemoteAddress;
            pPort.Value    = c.RemotePort;
            pClass.Value   = c.RemoteClass.ToStorageString();
            pUp.Value      = c.BytesUpDelta;
            pDown.Value    = c.BytesDownDelta;
            pFirst.Value   = c.FirstSeenUnixMs;
            pLast.Value    = c.LastSeenUnixMs;
            cmd.ExecuteNonQuery();
            written++;
        }
        return written;
    }

    private static int CloseSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<int> closedSessionIds,
        long endTimeUnixMs)
    {
        if (closedSessionIds.Count == 0)
        {
            return 0;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE process_sessions
            SET end_time = $end
            WHERE session_id = $id AND end_time IS NULL;
            """;
        var pEnd = cmd.Parameters.Add("$end", SqliteType.Integer);
        var pId  = cmd.Parameters.Add("$id",  SqliteType.Integer);
        cmd.Prepare();

        var closed = 0;
        foreach (var id in closedSessionIds)
        {
            pEnd.Value = endTimeUnixMs;
            pId.Value  = id;
            closed += cmd.ExecuteNonQuery();
        }
        return closed;
    }

    private static string ProtocolToText(Protocol p) => p switch
    {
        Protocol.Tcp => "TCP",
        Protocol.Udp => "UDP",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };
}
