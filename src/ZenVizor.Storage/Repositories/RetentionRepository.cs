using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    private readonly ConnectionFactory _connections;
    private readonly ILogger _logger;

    public RetentionRepository(
        ConnectionFactory connections,
        ILogger<RetentionRepository>? logger = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Run the purge using <paramref name="nowUnixMs"/> as the reference point.
    /// Each tier is purged with its own retention window from settings.
    /// </summary>
    public PurgeResult PurgeOlderThan(long nowUnixMs)
    {
        using var connection = _connections.Open();
        var policy = LoadPolicy(connection);

        // Each DELETE runs in its own implicit transaction; we don't wrap the
        // whole thing because long DELETEs would hold a write-lock against the
        // capture flush. Individual tiers are independent — partial progress is fine.
        var samplesDeleted   = DeleteBefore(connection, "traffic_samples", "bucket_start",   nowUnixMs - DaysToMs(policy.SamplesDays));
        var connsDeleted     = DeleteBefore(connection, "connections",    "last_seen",       nowUnixMs - DaysToMs(policy.ConnectionsDays));
        var hourlyDeleted    = DeleteBefore(connection, "traffic_hourly", "bucket_start",    nowUnixMs - DaysToMs(policy.HourlyDays));
        var dailyDeleted     = DeleteBefore(connection, "traffic_daily",  "bucket_start",    nowUnixMs - DaysToMs(policy.DailyDays));
        var alertsDeleted    = DeleteAcknowledgedAlertsBefore(connection,                    nowUnixMs - DaysToMs(policy.AlertsDaysAfterAck));
        var orphanSessions   = DeleteOrphanSessions(connection);

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

    private static int DeleteBefore(SqliteConnection connection, string table, string column, long boundaryUnixMs)
    {
        if (boundaryUnixMs <= 0) return 0;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE {column} < $boundary;";
        cmd.Parameters.AddWithValue("$boundary", boundaryUnixMs);
        return cmd.ExecuteNonQuery();
    }

    private static int DeleteAcknowledgedAlertsBefore(SqliteConnection connection, long boundaryUnixMs)
    {
        if (boundaryUnixMs <= 0) return 0;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM alerts
            WHERE acknowledged_at IS NOT NULL
              AND acknowledged_at < $boundary;
            """;
        cmd.Parameters.AddWithValue("$boundary", boundaryUnixMs);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drop process_sessions whose end_time is set and which no longer have any
    /// traffic_samples or connections rows referencing them (those rows aged out).
    /// Keeps the sessions table from growing unbounded.
    /// </summary>
    private static int DeleteOrphanSessions(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM process_sessions
            WHERE end_time IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM traffic_samples WHERE session_id = process_sessions.session_id)
              AND NOT EXISTS (SELECT 1 FROM connections    WHERE session_id = process_sessions.session_id);
            """;
        return cmd.ExecuteNonQuery();
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
