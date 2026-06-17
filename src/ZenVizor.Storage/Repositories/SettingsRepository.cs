using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Typed read/write surface over the <c>settings</c> key/value table. The
/// Settings page IPC handler is the primary caller; <see cref="RetentionRepository"/>
/// keeps its own private <c>LoadPolicy</c> reader (different shape — bulk
/// SELECT projected to <see cref="RetentionPolicy"/>) and is not touched
/// here.
/// </summary>
/// <remarks>
/// Values are stored as TEXT (per the §7.8 schema). Booleans encode as
/// "0"/"1" matching the existing seeds (<c>toast.on_alert</c>,
/// <c>autostart.mirror</c>). Each Get/Set opens a fresh pooled connection
/// — Settings reads are infrequent, no caching is justified.
/// </remarks>
public sealed class SettingsRepository
{
    /// <summary>Settings keys the UI / IPC surface reads or writes.</summary>
    public static class Keys
    {
        public const string AutostartMode    = "autostart.mode";
        public const string AutostartMirror  = "autostart.mirror";
        public const string ToastOnAlert     = "toast.on_alert";
        public const string AppearanceTheme  = "appearance.theme";
        public const string FlushIntervalMs  = "flush.interval_ms";
        public const string FlushBucketSecs  = "flush.bucket_seconds";
        public const string StartMinimized   = "ui.start_minimized";

        // Retention keys mirror RetentionRepository.SettingKeys; redeclared
        // here so callers don't have to reach across repositories.
        public const string SamplesDays            = "retention.traffic_samples_days";
        public const string ConnectionsDays        = "retention.connections_days";
        public const string HourlyDays             = "retention.traffic_hourly_days";
        public const string DailyDays              = "retention.traffic_daily_days";
        public const string AlertsDaysAfterAck     = "retention.alerts_days_after_ack";
    }

    private readonly ConnectionFactory _connections;

    public SettingsRepository(ConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>Read a string value. Returns <c>null</c> if absent.</summary>
    public string? GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var connection = _connections.Open();
        return ReadValue(connection, key);
    }

    /// <summary>Read an integer value. Returns <paramref name="defaultValue"/> on missing or unparseable.</summary>
    public int GetInt(string key, int defaultValue)
    {
        var raw = GetString(key);
        if (raw is null) return defaultValue;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : defaultValue;
    }

    /// <summary>Read a boolean stored as "0"/"1". Returns <paramref name="defaultValue"/> on missing or unparseable.</summary>
    public bool GetBool(string key, bool defaultValue)
    {
        var raw = GetString(key);
        if (raw is null) return defaultValue;
        return raw switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue,
        };
    }

    /// <summary>UPSERT the value for <paramref name="key"/>. Empty string is allowed; nulls are not.</summary>
    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>UPSERT an integer value; invariant-culture formatted.</summary>
    public void SetInt(string key, int value) =>
        Set(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>UPSERT a boolean as "0"/"1".</summary>
    public void SetBool(string key, bool value) =>
        Set(key, value ? "1" : "0");

    private static string? ReadValue(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }
}
