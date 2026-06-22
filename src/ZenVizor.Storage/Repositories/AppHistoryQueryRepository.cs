// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Data.Sqlite;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Per-tier bucket widths (ms). Used by the query SQL to express
/// bucket-overlap semantics: a bucket counts if its <c>[start, start+width)</c>
/// range overlaps the query window <c>[from, to)</c>. This is necessary so
/// that, e.g., today's daily bucket is included in a "Last 24 hours" query
/// even when the bucket started before <c>from</c>.
/// </summary>
internal static class TierBucketWidths
{
    public const long SamplesMs = 60_000L;        // PRD §7.3 default
    public const long HourlyMs  = 3_600_000L;
    public const long DailyMs   = 86_400_000L;
}

/// <summary>
/// Phase 4 read-side query surface. Serves the per-app, app-detail,
/// connections, and traffic-history endpoints. Picks the appropriate tier
/// (<c>traffic_samples</c> / <c>traffic_hourly</c> / <c>traffic_daily</c>)
/// based on the resolved grain so long-window queries don't scan the
/// 60s-bucket high-res tier.
/// </summary>
/// <remarks>
/// All queries are read-only and use a dedicated connection from the pool.
/// They MUST NOT touch the in-memory rolling window — that's the snapshot
/// path's job (Phase 3). The UI distinguishes "live" (snapshot) from
/// "history" (this repo) by which method it calls.
/// </remarks>
public sealed class AppHistoryQueryRepository
{
    private readonly ConnectionFactory _connections;

    public AppHistoryQueryRepository(ConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    // ---- GetAppList ----------------------------------------------------

    public AppListResult GetAppList(QueryWindow window)
    {
        var grain = TrafficGrainResolver.Resolve(window, TrafficGrain.Auto);
        using var connection = _connections.Open();

        var sql = grain switch
        {
            TrafficGrain.Samples => SqlAppListFromSamples,
            TrafficGrain.Hourly  => SqlAppListFromRollup("traffic_hourly"),
            TrafficGrain.Daily   => SqlAppListFromRollup("traffic_daily"),
            _ => throw new InvalidOperationException($"Unsupported grain {grain}"),
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$from", window.FromUnixMs);
        cmd.Parameters.AddWithValue("$to",   window.ToUnixMs);
        cmd.Parameters.AddWithValue("$bucketMs", BucketWidthFor(grain));

        var rows = new List<AppListEntry>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new AppListEntry(
                    AppId:              reader.GetInt32(0),
                    ImageName:          reader.GetString(1),
                    ImagePath:          reader.GetString(2),
                    Publisher:          reader.IsDBNull(3) ? null : reader.GetString(3),
                    SignatureStatus:    reader.GetString(4),
                    IsUserWritablePath: reader.GetInt32(5) != 0,
                    BytesUp:            reader.GetInt64(6),
                    BytesDown:          reader.GetInt64(7),
                    FirstSeenUnixMs:    reader.GetInt64(8),
                    LastSeenUnixMs:     reader.GetInt64(9)));
            }
        }

        return new AppListResult(window, rows);
    }

    // ---- Bucket-overlap predicate ----
    //
    // A bucket at <c>bucket_start</c> represents data in <c>[bucket_start, bucket_start + bucketMs)</c>.
    // It overlaps the query window <c>[from, to)</c> iff <c>bucket_start &lt; to</c> AND
    // <c>bucket_start + bucketMs &gt; from</c>. This is necessary so that, e.g., today's daily
    // bucket (started at 00:00 UTC) is included for a "Last 24 hours" query that started
    // at 14:00 — strict <c>bucket_start IN [from, to)</c> would exclude it incorrectly.

    /// <summary>
    /// Samples-tier app list. Joins through process_sessions to map sessions to apps.
    /// </summary>
    private const string SqlAppListFromSamples = """
        SELECT a.app_id, a.image_name, a.image_path, a.publisher,
               a.signature_status, a.is_user_writable_path,
               COALESCE(SUM(s.bytes_up),   0) AS up,
               COALESCE(SUM(s.bytes_down), 0) AS down,
               a.first_seen, a.last_seen
        FROM apps a
        JOIN process_sessions ps ON ps.app_id = a.app_id
        JOIN traffic_samples  s  ON s.session_id = ps.session_id
                                AND s.bucket_start < $to
                                AND s.bucket_start > $from - $bucketMs
        GROUP BY a.app_id
        HAVING up + down > 0
        ORDER BY (up + down) DESC, a.image_name;
        """;

    private static string SqlAppListFromRollup(string table) => $"""
        SELECT a.app_id, a.image_name, a.image_path, a.publisher,
               a.signature_status, a.is_user_writable_path,
               COALESCE(SUM(r.bytes_up),   0) AS up,
               COALESCE(SUM(r.bytes_down), 0) AS down,
               a.first_seen, a.last_seen
        FROM apps a
        JOIN {table} r ON r.app_id = a.app_id
                      AND r.bucket_start < $to
                      AND r.bucket_start > $from - $bucketMs
        GROUP BY a.app_id
        HAVING up + down > 0
        ORDER BY (up + down) DESC, a.image_name;
        """;

    private static long BucketWidthFor(TrafficGrain grain) => grain switch
    {
        TrafficGrain.Samples => TierBucketWidths.SamplesMs,
        TrafficGrain.Hourly  => TierBucketWidths.HourlyMs,
        TrafficGrain.Daily   => TierBucketWidths.DailyMs,
        _ => throw new InvalidOperationException($"Unknown grain {grain}"),
    };

    // ---- GetAppDetail --------------------------------------------------

    public AppDetailResult GetAppDetail(int appId, QueryWindow window, TrafficGrain grain)
    {
        var resolvedGrain = TrafficGrainResolver.Resolve(window, grain);
        using var connection = _connections.Open();

        // App metadata is one tiny PK lookup against the small `apps` table.
        // The byte totals used to live in two correlated subqueries against
        // traffic_samples (or the rollup tier) — same predicate as the series
        // query, just summed without the (bucket_start, remote_class) GROUP BY.
        // We load the series anyway, so summing it in C# is byte-equivalent
        // and saves an extra traffic-tier scan per refresh.
        var series = LoadAppSeries(connection, appId, window, resolvedGrain);
        var sessions = LoadRecentSessions(connection, appId, window);

        long totalUp = 0, totalDown = 0;
        for (int i = 0; i < series.Count; i++)
        {
            totalUp   += series[i].BytesUp;
            totalDown += series[i].BytesDown;
        }

        var summary = LoadAppMetadata(connection, appId, totalUp, totalDown);
        return new AppDetailResult(window, resolvedGrain, summary, series, sessions);
    }

    private static AppListEntry LoadAppMetadata(
        SqliteConnection connection, int appId, long bytesUp, long bytesDown)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlAppMetadata;
        cmd.Parameters.AddWithValue("$appId", appId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new AppListEntry(appId, "", "", null, "Unchecked", false, bytesUp, bytesDown, 0, 0);
        }
        return new AppListEntry(
            AppId:              reader.GetInt32(0),
            ImageName:          reader.GetString(1),
            ImagePath:          reader.GetString(2),
            Publisher:          reader.IsDBNull(3) ? null : reader.GetString(3),
            SignatureStatus:    reader.GetString(4),
            IsUserWritablePath: reader.GetInt32(5) != 0,
            BytesUp:            bytesUp,
            BytesDown:          bytesDown,
            FirstSeenUnixMs:    reader.GetInt64(6),
            LastSeenUnixMs:     reader.GetInt64(7));
    }

    private const string SqlAppMetadata = """
        SELECT app_id, image_name, image_path, publisher,
               signature_status, is_user_writable_path,
               first_seen, last_seen
        FROM apps WHERE app_id = $appId;
        """;

    private static IReadOnlyList<TrafficPoint> LoadAppSeries(
        SqliteConnection connection, int appId, QueryWindow window, TrafficGrain grain)
    {
        var sql = grain switch
        {
            TrafficGrain.Samples => SqlAppSeriesFromSamples,
            TrafficGrain.Hourly  => SqlAppSeriesFromRollup("traffic_hourly"),
            TrafficGrain.Daily   => SqlAppSeriesFromRollup("traffic_daily"),
            _ => throw new InvalidOperationException($"Unsupported grain {grain}"),
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$appId", appId);
        cmd.Parameters.AddWithValue("$from",  window.FromUnixMs);
        cmd.Parameters.AddWithValue("$to",    window.ToUnixMs);
        cmd.Parameters.AddWithValue("$bucketMs", BucketWidthFor(grain));

        var rows = new List<TrafficPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TrafficPoint(
                BucketStartUnixMs: reader.GetInt64(0),
                RemoteClass:       reader.GetString(1),
                BytesUp:           reader.GetInt64(2),
                BytesDown:         reader.GetInt64(3)));
        }
        return rows;
    }

    private const string SqlAppSeriesFromSamples = """
        SELECT s.bucket_start, s.remote_class,
               SUM(s.bytes_up)   AS up,
               SUM(s.bytes_down) AS down
        FROM traffic_samples s
        JOIN process_sessions ps ON ps.session_id = s.session_id
        WHERE ps.app_id = $appId
          AND s.bucket_start < $to
          AND s.bucket_start > $from - $bucketMs
        GROUP BY s.bucket_start, s.remote_class
        ORDER BY s.bucket_start, s.remote_class;
        """;

    private static string SqlAppSeriesFromRollup(string table) => $"""
        SELECT r.bucket_start, r.remote_class,
               SUM(r.bytes_up)   AS up,
               SUM(r.bytes_down) AS down
        FROM {table} r
        WHERE r.app_id = $appId
          AND r.bucket_start < $to
          AND r.bucket_start > $from - $bucketMs
        GROUP BY r.bucket_start, r.remote_class
        ORDER BY r.bucket_start, r.remote_class;
        """;

    private static IReadOnlyList<SessionInfo> LoadRecentSessions(
        SqliteConnection connection, int appId, QueryWindow window)
    {
        // Sessions that overlap the window: start_time < to AND (end_time IS NULL OR end_time >= from).
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, pid, start_time, end_time, hosted_services
            FROM process_sessions
            WHERE app_id = $appId
              AND start_time < $to
              AND (end_time IS NULL OR end_time >= $from)
            ORDER BY start_time DESC
            LIMIT 50;
            """;
        cmd.Parameters.AddWithValue("$appId", appId);
        cmd.Parameters.AddWithValue("$from",  window.FromUnixMs);
        cmd.Parameters.AddWithValue("$to",    window.ToUnixMs);

        var rows = new List<SessionInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SessionInfo(
                SessionId:        reader.GetInt64(0),
                Pid:              reader.GetInt32(1),
                StartTimeUnixMs:  reader.GetInt64(2),
                EndTimeUnixMs:    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                HostedServices:   reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return rows;
    }

    // ---- GetConnections ------------------------------------------------

    /// <summary>
    /// Endpoints an app talked to during the window. Aggregated across all the
    /// app's sessions that overlap the window. NOTE: per Q8 / schema, the
    /// <c>bytes_up</c>/<c>bytes_down</c> on <c>connections</c> are
    /// session-cumulative running totals, not window-filtered. A connection
    /// row visible here represents an endpoint that was active during the
    /// window; the byte totals are over the full life of those sessions.
    /// Window-accurate bytes-per-endpoint would require a future
    /// <c>connection_samples</c> table.
    /// </summary>
    public ConnectionListResult GetConnections(int appId, QueryWindow window)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        // Phase 8 — MAX(c.resolved_host) collapses the app's session rows for
        // a given endpoint into one displayed hostname. Lexicographic pick:
        // for ~95% of endpoints the underlying rows agree (one IP, one
        // hostname); the rare CDN-aliasing case where multiple names map
        // to the same IP yields a deterministic but alphabetical winner.
        // See Phase 8 design decision D4 in docs/zenvizor-sprint-plan.md
        // for the tradeoff and the swap-to-"most-recent-non-null" upgrade
        // path if user testing surfaces confusion.
        cmd.CommandText = """
            SELECT c.protocol, c.remote_addr, c.remote_port, c.remote_class,
                   SUM(c.bytes_up)   AS up,
                   SUM(c.bytes_down) AS down,
                   MIN(c.first_seen) AS first,
                   MAX(c.last_seen)  AS last,
                   MAX(c.resolved_host) AS host
            FROM connections c
            JOIN process_sessions ps ON ps.session_id = c.session_id
            WHERE ps.app_id = $appId
              AND c.last_seen >= $from
              AND c.first_seen < $to
            GROUP BY c.protocol, c.remote_addr, c.remote_port
            ORDER BY (up + down) DESC, c.remote_addr;
            """;
        cmd.Parameters.AddWithValue("$appId", appId);
        cmd.Parameters.AddWithValue("$from",  window.FromUnixMs);
        cmd.Parameters.AddWithValue("$to",    window.ToUnixMs);

        var rows = new List<ConnectionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ConnectionRow(
                Protocol:        reader.GetString(0),
                RemoteAddress:   reader.GetString(1),
                RemotePort:      reader.GetInt32(2),
                RemoteClass:     reader.GetString(3),
                BytesUp:         reader.GetInt64(4),
                BytesDown:       reader.GetInt64(5),
                FirstSeenUnixMs: reader.GetInt64(6),
                LastSeenUnixMs:  reader.GetInt64(7),
                ResolvedHost:    reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return new ConnectionListResult(window, rows);
    }

    // ---- GetTrafficHistory --------------------------------------------

    public TrafficHistoryResult GetTrafficHistory(QueryWindow window, TrafficGrain grain)
    {
        var resolvedGrain = TrafficGrainResolver.Resolve(window, grain);
        using var connection = _connections.Open();

        var sql = resolvedGrain switch
        {
            TrafficGrain.Samples => SqlHistoryFromSamples,
            TrafficGrain.Hourly  => SqlHistoryFromRollup("traffic_hourly"),
            TrafficGrain.Daily   => SqlHistoryFromRollup("traffic_daily"),
            _ => throw new InvalidOperationException($"Unsupported grain {resolvedGrain}"),
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$from", window.FromUnixMs);
        cmd.Parameters.AddWithValue("$to",   window.ToUnixMs);
        cmd.Parameters.AddWithValue("$bucketMs", BucketWidthFor(resolvedGrain));

        var rows = new List<TrafficPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TrafficPoint(
                BucketStartUnixMs: reader.GetInt64(0),
                RemoteClass:       reader.GetString(1),
                BytesUp:           reader.GetInt64(2),
                BytesDown:         reader.GetInt64(3)));
        }
        return new TrafficHistoryResult(window, resolvedGrain, rows);
    }

    private const string SqlHistoryFromSamples = """
        SELECT s.bucket_start, s.remote_class,
               SUM(s.bytes_up)   AS up,
               SUM(s.bytes_down) AS down
        FROM traffic_samples s
        WHERE s.bucket_start < $to
          AND s.bucket_start > $from - $bucketMs
        GROUP BY s.bucket_start, s.remote_class
        ORDER BY s.bucket_start, s.remote_class;
        """;

    private static string SqlHistoryFromRollup(string table) => $"""
        SELECT r.bucket_start, r.remote_class,
               SUM(r.bytes_up)   AS up,
               SUM(r.bytes_down) AS down
        FROM {table} r
        WHERE r.bucket_start < $to
          AND r.bucket_start > $from - $bucketMs
        GROUP BY r.bucket_start, r.remote_class
        ORDER BY r.bucket_start, r.remote_class;
        """;
}
