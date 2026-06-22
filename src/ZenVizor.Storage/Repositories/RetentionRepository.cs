// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Aggregation;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Phase 4 retention job. Deletes rows older than each tier's configured
/// retention window (read from <c>settings</c>). Runs on a dedicated timer
/// thread in the hosted service; never touches the hot capture path.
/// </summary>
/// <remarks>
/// Schedule (Q4 decision): one purge at service start, then once per day at
/// a fixed local hour. The Phase-6 Settings → Purge button kicks the same
/// <see cref="PurgeOlderThan"/> entry point immediately.
/// </remarks>
public sealed class RetentionRepository
{
    /// <summary>Settings keys consulted by the purge job.</summary>
    public static class SettingKeys
    {
        public const string SamplesDays   = "retention.traffic_samples_days";
        public const string ConnectionsDays = "retention.connections_days";
        public const string HourlyDays    = "retention.traffic_hourly_days";
        public const string DailyDays     = "retention.traffic_daily_days";
        public const string AlertsDaysAfterAck = "retention.alerts_days_after_ack";
    }

    /// <summary>
    /// Per-chunk DELETE size. The first purge after long uptime can touch
    /// many days of samples — an unchunked DELETE holds the write lock for
    /// the full sweep, which collides with the 5 s flush tick. Chunking
    /// bounds the time any single statement holds the lock and lets WAL
    /// checkpoint between chunks.
    /// </summary>
    private const int DefaultChunkSize = 5000;

    private readonly ConnectionFactory _connections;
    private readonly ILogger _logger;
    private readonly int _chunkSize;

    public RetentionRepository(
        ConnectionFactory connections,
        ILogger<RetentionRepository>? logger = null,
        int chunkSize = DefaultChunkSize)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _chunkSize = chunkSize <= 0 ? DefaultChunkSize : chunkSize;
    }

    /// <summary>
    /// Run the purge using <paramref name="nowUnixMs"/> as the reference point.
    /// Each tier is purged with its own retention window from settings.
    /// </summary>
    public PurgeResult PurgeOlderThan(long nowUnixMs)
    {
        using var connection = _connections.Open();
        var policy = LoadPolicy(connection);

        // Align each tier's cutoff to that tier's bucket boundary. Without
        // alignment, the raw nowUnixMs - DaysToMs cutoff drifts with the
        // wall-clock time of the purge tick: a sample bucket whose start is
        // exactly retention.days ago survives a 03:00 purge but not an 04:00
        // purge, which makes the visible "oldest data" wobble across runs.
        var samplesCutoff = BucketAligner.AlignToBucket(nowUnixMs) - DaysToMs(policy.SamplesDays);
        var hourlyCutoff  = BucketAligner.AlignToHour(nowUnixMs)   - DaysToMs(policy.HourlyDays);
        var dailyCutoff   = BucketAligner.AlignToDay(nowUnixMs)    - DaysToMs(policy.DailyDays);
        // connections / alerts use raw timestamps (last_seen, acknowledged_at)
        // rather than aligned buckets, so no bucket alignment applies.
        var connsCutoff   = nowUnixMs - DaysToMs(policy.ConnectionsDays);
        var alertsCutoff  = nowUnixMs - DaysToMs(policy.AlertsDaysAfterAck);

        // Each DELETE runs in its own implicit transaction; we don't wrap the
        // whole thing because long DELETEs would hold a write-lock against the
        // capture flush. Individual tiers are independent — partial progress is fine.
        var samplesDeleted   = DeleteBeforeChunked(connection, "traffic_samples", "bucket_start", samplesCutoff);
        var connsDeleted     = DeleteBeforeChunked(connection, "connections",    "last_seen",    connsCutoff);
        var hourlyDeleted    = DeleteBeforeChunked(connection, "traffic_hourly", "bucket_start", hourlyCutoff);
        var dailyDeleted     = DeleteBeforeChunked(connection, "traffic_daily",  "bucket_start", dailyCutoff);
        var alertsDeleted    = DeleteAcknowledgedAlertsBeforeChunked(connection, alertsCutoff);
        var orphanSessions   = DeleteOrphanSessionsChunked(connection);

        _logger.LogInformation(
            "Retention purge: samples={S} connections={C} hourly={H} daily={D} alerts={A} orphan_sessions={O}",
            samplesDeleted, connsDeleted, hourlyDeleted, dailyDeleted, alertsDeleted, orphanSessions);

        return new PurgeResult(
            SamplesDeleted: samplesDeleted,
            ConnectionsDeleted: connsDeleted,
            HourlyDeleted: hourlyDeleted,
            DailyDeleted: dailyDeleted,
            AlertsDeleted: alertsDeleted,
            OrphanSessionsDeleted: orphanSessions);
    }

    /// <summary>
    /// Wipes ALL collected history — every row from traffic_samples,
    /// connections, traffic_hourly, traffic_daily, alerts, and
    /// process_sessions. Preserves <c>apps</c> (the catalog row dedup is
    /// expensive to rebuild and harmless to keep) and <c>settings</c> (user
    /// config). Settings page's "Reset history" button is the only caller.
    /// </summary>
    /// <remarks>
    /// All tables are deleted inside a single transaction so the wipe is
    /// atomic — a crash mid-wipe doesn't leave the rollup tiers populated
    /// while traffic_samples is empty (which would skew Reports' "delta vs
    /// trailing week" math). Unlike <see cref="PurgeOlderThan"/>, the
    /// flush sink can't contend here in any meaningful way: the wipe takes
    /// the write lock for the full transaction, but the data being deleted
    /// is everything that was there at the start, so contention with a
    /// concurrent flush merely re-populates a few rows that survive the
    /// wipe (acceptable — the user invoked "start fresh", not "snapshot of
    /// the exact moment").
    /// </remarks>
    public WipeResult WipeHistory()
    {
        using var connection = _connections.Open();
        using var transaction = connection.BeginTransaction();

        var samples       = DeleteAll(connection, transaction, "traffic_samples");
        var connsRows     = DeleteAll(connection, transaction, "connections");
        var hourly        = DeleteAll(connection, transaction, "traffic_hourly");
        var daily         = DeleteAll(connection, transaction, "traffic_daily");
        var alertsRows    = DeleteAll(connection, transaction, "alerts");
        var sessions      = DeleteAll(connection, transaction, "process_sessions");

        transaction.Commit();

        _logger.LogInformation(
            "History wipe: samples={S} connections={C} hourly={H} daily={D} alerts={A} sessions={Ss}",
            samples, connsRows, hourly, daily, alertsRows, sessions);

        return new WipeResult(
            SamplesDeleted: samples,
            ConnectionsDeleted: connsRows,
            HourlyDeleted: hourly,
            DailyDeleted: daily,
            AlertsDeleted: alertsRows,
            SessionsDeleted: sessions);
    }

    private static int DeleteAll(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"DELETE FROM {table};";
        return cmd.ExecuteNonQuery();
    }

    private int DeleteBeforeChunked(SqliteConnection connection, string table, string column, long boundaryUnixMs)
    {
        if (boundaryUnixMs <= 0) return 0;

        // DELETE ... LIMIT requires SQLITE_ENABLE_UPDATE_DELETE_LIMIT, which
        // the e_sqlite3 build shipped with Microsoft.Data.Sqlite does NOT
        // enable. We get the same effect via "WHERE rowid IN (SELECT rowid
        // ... LIMIT chunk)" without needing the compile option.
        var sql = $"""
            DELETE FROM {table}
            WHERE rowid IN (
                SELECT rowid FROM {table}
                WHERE {column} < $boundary
                LIMIT $chunk
            );
            """;

        var total = 0;
        while (true)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$boundary", boundaryUnixMs);
            cmd.Parameters.AddWithValue("$chunk", _chunkSize);
            var deleted = cmd.ExecuteNonQuery();
            total += deleted;
            if (deleted < _chunkSize) break;
        }
        return total;
    }

    private int DeleteAcknowledgedAlertsBeforeChunked(SqliteConnection connection, long boundaryUnixMs)
    {
        if (boundaryUnixMs <= 0) return 0;
        var sql = """
            DELETE FROM alerts
            WHERE rowid IN (
                SELECT rowid FROM alerts
                WHERE acknowledged_at IS NOT NULL
                  AND acknowledged_at < $boundary
                LIMIT $chunk
            );
            """;

        var total = 0;
        while (true)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$boundary", boundaryUnixMs);
            cmd.Parameters.AddWithValue("$chunk", _chunkSize);
            var deleted = cmd.ExecuteNonQuery();
            total += deleted;
            if (deleted < _chunkSize) break;
        }
        return total;
    }

    /// <summary>
    /// Drop process_sessions whose end_time is set and which no longer have any
    /// traffic_samples or connections rows referencing them (those rows aged out).
    /// Keeps the sessions table from growing unbounded.
    /// </summary>
    private int DeleteOrphanSessionsChunked(SqliteConnection connection)
    {
        var sql = """
            DELETE FROM process_sessions
            WHERE session_id IN (
                SELECT session_id FROM process_sessions
                WHERE end_time IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM traffic_samples WHERE session_id = process_sessions.session_id)
                  AND NOT EXISTS (SELECT 1 FROM connections    WHERE session_id = process_sessions.session_id)
                LIMIT $chunk
            );
            """;

        var total = 0;
        while (true)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$chunk", _chunkSize);
            var deleted = cmd.ExecuteNonQuery();
            total += deleted;
            if (deleted < _chunkSize) break;
        }
        return total;
    }

    private static long DaysToMs(int days) => days * 86_400_000L;

    private static RetentionPolicy LoadPolicy(SqliteConnection connection)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM settings;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                settings[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return new RetentionPolicy(
            SamplesDays:   GetInt(settings, SettingKeys.SamplesDays,   30),
            ConnectionsDays: GetInt(settings, SettingKeys.ConnectionsDays, 30),
            HourlyDays:    GetInt(settings, SettingKeys.HourlyDays,    90),
            DailyDays:     GetInt(settings, SettingKeys.DailyDays,     365),
            AlertsDaysAfterAck: GetInt(settings, SettingKeys.AlertsDaysAfterAck, 90));
    }

    private static int GetInt(IReadOnlyDictionary<string, string> settings, string key, int fallback)
    {
        if (settings.TryGetValue(key, out var raw) && int.TryParse(raw, out var v) && v > 0)
        {
            return v;
        }
        return fallback;
    }
}

public sealed record RetentionPolicy(
    int SamplesDays,
    int ConnectionsDays,
    int HourlyDays,
    int DailyDays,
    int AlertsDaysAfterAck);

public sealed record PurgeResult(
    int SamplesDeleted,
    int ConnectionsDeleted,
    int HourlyDeleted,
    int DailyDeleted,
    int AlertsDeleted,
    int OrphanSessionsDeleted);

public sealed record WipeResult(
    int SamplesDeleted,
    int ConnectionsDeleted,
    int HourlyDeleted,
    int DailyDeleted,
    int AlertsDeleted,
    int SessionsDeleted);
