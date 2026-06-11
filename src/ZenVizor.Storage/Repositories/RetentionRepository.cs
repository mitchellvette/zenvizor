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
