using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Phase 5b read-side aggregator that builds the daily-report payload from
/// the rollup tiers (<c>traffic_hourly</c>, <c>traffic_daily</c>) and the
/// <c>apps</c> / <c>process_sessions</c> tables. Replaces the Phase-5a
/// <c>DailyReportStubProvider</c>.
/// </summary>
/// <remarks>
/// All read paths are read-only and use a single connection from the pool
/// for the duration of one report. The aggregator owns the local-time
/// conversion: <c>traffic_hourly</c> rows live in UTC-aligned hour buckets
/// but the daily report reads as "what happened on my machine that local
/// day", so we translate buckets through the supplied
/// <see cref="TimeZoneInfo"/> before grouping into the 0-23 hour series.
///
/// Provisional-field heuristics (mockup page 9 token annotation / brief
/// §Phase 5):
/// <list type="bullet">
///   <item><description><b>NewToday</b> — apps whose publisher's earliest
///   <c>first_seen</c> across the <c>apps</c> table falls within today's
///   local window AND the app had traffic today. Excludes NULL-publisher
///   apps — those route to RiskyPaths instead.</description></item>
///   <item><description><b>UnusualVolume</b> — today's bytes exceed 3× the
///   7-day rolling median for that app, in whichever direction (Up or Down)
///   is the larger anomaly. Requires ≥4 baseline days with non-zero bytes
///   to avoid false fires on fresh installs.</description></item>
///   <item><description><b>RiskyPaths</b> — apps with
///   <c>signature_status IN ('Unsigned','Invalid')</c> AND
///   <c>is_user_writable_path=1</c> AND traffic today.</description></item>
///   <item><description><b>Notable (UnsignedFromUserPath)</b> — same predicate
///   as RiskyPaths but specifically against <c>traffic_samples</c> WAN-class
///   buckets so we only flag apps that actually made outbound network
///   contact (not just observed file access). MVP only rule.</description></item>
/// </list>
/// </remarks>
public sealed class DailyReportRepository
{
    private const int TopAppsLimit            = 10;
    private const double UnusualVolumeFactor  = 3.0;
    private const int    UnusualVolumeMinBaselineDays = 4;

    private readonly ConnectionFactory _connections;

    public DailyReportRepository(ConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public DailyReportResult GetDailyReport(
        DateOnly date,
        AnchorMode anchor,
        DateOnly? anchorSpecificDate,
        TimeZoneInfo localTz)
    {
        var (dayStart, dayEnd) = LocalDayWindowUtcMs(date, localTz);

        using var connection = _connections.Open();

        var (todayUp, todayDown, wan, local) = LoadHero(connection, dayStart, dayEnd);
        var (anchorAvgUp, anchorAvgDown) =
            LoadAnchorBaseline(connection, date, anchor, anchorSpecificDate, localTz);
        var baselineDaysAvailable =
            LoadBaselineDaysAvailable(connection, date, AnchorDays(anchor), localTz);

        var hero = new DailyReportHero(
            TotalUpBytes:           todayUp,
            TotalDownBytes:         todayDown,
            WanRatio:               Ratio(wan,   wan + local),
            LocalRatio:             Ratio(local, wan + local),
            TotalDeltaPct:          PercentDelta(todayUp + todayDown, anchorAvgUp + anchorAvgDown),
            UpDeltaPct:             PercentDelta(todayUp,             anchorAvgUp),
            DownDeltaPct:           PercentDelta(todayDown,           anchorAvgDown),
            BaselineDaysAvailable:  baselineDaysAvailable);

        var hourly = LoadHourlySeries(connection, dayStart, dayEnd, localTz);
        var topApps = LoadTopApps(connection, dayStart, dayEnd);
        EnrichSvchostBrackets(connection, topApps, dayStart, dayEnd);

        var newToday      = LoadNewToday(connection, dayStart, dayEnd, localTz);
        var unusualVolume = LoadUnusualVolume(connection, date, dayStart, dayEnd, localTz);
        var riskyPaths    = LoadRiskyPaths(connection, dayStart, dayEnd);
        var notable       = LoadNotable(connection, dayStart, dayEnd);

        var talkers = new List<DailyReportTalker>(newToday.Count + unusualVolume.Count + riskyPaths.Count);
        talkers.AddRange(newToday);
        talkers.AddRange(unusualVolume);
        talkers.AddRange(riskyPaths);

        // Server computes HasOverlap so the UI doesn't have to. An app
        // surfaced in Top Apps AND in any UncommonTalker/Notable list gets
        // its dot.
        ApplyOverlapFlags(topApps, talkers, notable);

        return new DailyReportResult(
            Date:               date,
            Anchor:             anchor,
            AnchorSpecificDate: anchorSpecificDate,
            Hero:               hero,
            HourlyTraffic:      hourly,
            TopApps:            topApps,
            UncommonTalkers:    talkers,
            Notable:            notable);
    }

    // ─── Local-day window math ─────────────────────────────────────────────

    // A local calendar day expressed in UTC unix ms. DST days produce 23 or
    // 25-hour windows; the bucket-overlap predicate handles both, and the
    // hourly loader buckets back into the 0-23 local-hour slots — DST repeats
    // sum into the same slot (acceptable for visual cue trend).
    private static (long start, long end) LocalDayWindowUtcMs(DateOnly date, TimeZoneInfo tz)
    {
        var localMidnight     = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var localMidnightNext = localMidnight.AddDays(1);
        var startUtc          = TimeZoneInfo.ConvertTimeToUtc(localMidnight,     tz);
        var endUtc            = TimeZoneInfo.ConvertTimeToUtc(localMidnightNext, tz);
        return (new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                new DateTimeOffset(endUtc,   TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    // ─── Hero totals ───────────────────────────────────────────────────────

    private const string SqlHeroTotals = """
        SELECT
            COALESCE(SUM(bytes_up),   0)                                                       AS up,
            COALESCE(SUM(bytes_down), 0)                                                       AS down,
            COALESCE(SUM(CASE WHEN remote_class = 'Wan'   THEN bytes_up + bytes_down END), 0) AS wan,
            COALESCE(SUM(CASE WHEN remote_class = 'Local' THEN bytes_up + bytes_down END), 0) AS local
        FROM traffic_hourly
        WHERE bucket_start >= $start AND bucket_start < $end;
        """;

    private static (long up, long down, long wan, long local) LoadHero(
        SqliteConnection connection, long dayStart, long dayEnd)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlHeroTotals;
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0, 0);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    // ─── Anchor baseline ───────────────────────────────────────────────────

    // Returns the *average per day* of Up and Down bytes in the anchor window.
    // For AnchorMode.SpecificDate this is the bare day's totals (window size 1).
    // For Avg7d/30d/90d this is the previous N days' totals divided by N.
    private (long up, long down) LoadAnchorBaseline(
        SqliteConnection connection,
        DateOnly reportDate,
        AnchorMode anchor,
        DateOnly? specificDate,
        TimeZoneInfo localTz)
    {
        int days = AnchorDays(anchor);

        DateOnly anchorEnd; // exclusive
        DateOnly anchorStart;
        if (anchor == AnchorMode.SpecificDate)
        {
            var target = specificDate ?? reportDate.AddDays(-1);
            anchorStart = target;
            anchorEnd   = target.AddDays(1);
        }
        else
        {
            // Window ENDS at the day before the report day (today's data is
            // not yet in the baseline — keeps the comparison stable across
            // partial-day refreshes). Window LENGTH is N days.
            anchorEnd   = reportDate;
            anchorStart = reportDate.AddDays(-days);
        }

        var (startMs, endMs) = (
            LocalDayWindowUtcMs(anchorStart, localTz).start,
            LocalDayWindowUtcMs(anchorEnd.AddDays(-1), localTz).end);

        const string sql = """
            SELECT COALESCE(SUM(bytes_up), 0), COALESCE(SUM(bytes_down), 0)
            FROM traffic_daily
            WHERE bucket_start >= $start AND bucket_start < $end;
            """;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$start", startMs);
        cmd.Parameters.AddWithValue("$end",   endMs);
        using var reader = cmd.ExecuteReader();
        long totalUp = 0, totalDown = 0;
        if (reader.Read())
        {
            totalUp   = reader.GetInt64(0);
            totalDown = reader.GetInt64(1);
        }
        return (totalUp / Math.Max(1, days), totalDown / Math.Max(1, days));
    }

    private static double PercentDelta(long today, long baseline)
    {
        if (baseline <= 0) return 0.0;
        return ((double)today - baseline) / baseline * 100.0;
    }

    private static double Ratio(long part, long whole) =>
        whole <= 0 ? 0.0 : (double)part / whole;

    // ─── Baseline sufficiency ──────────────────────────────────────────────

    // Anchor window's nominal size in days. SpecificDate compares against a
    // single day, so it's effectively 1 — but the UI treatment skips the
    // sufficiency guard for SpecificDate (it's a UI-only placeholder for the
    // MVP) so the value is mostly a defensive fallback.
    private static int AnchorDays(AnchorMode anchor) => anchor switch
    {
        AnchorMode.Avg7d  => 7,
        AnchorMode.Avg30d => 30,
        AnchorMode.Avg90d => 90,
        _ => 1,
    };

    // Days of pre-report history available for the chosen anchor, capped at
    // the anchor's nominal size. Sourced from MIN(bucket_start) on
    // traffic_daily — the tier LoadAnchorBaseline actually reads from. An
    // empty traffic_daily (truly fresh install or post-wipe state) returns
    // 0, which surfaces treatment (a) suppression in the UI.
    //
    // Why traffic_daily and not apps.first_seen: RetentionRepository.WipeHistory
    // deliberately preserves the apps registry so the per-app vocabulary
    // (image_name, publisher, signature_status, app_id continuity across
    // tiers) survives "Reset history". Sourcing from apps would over-report
    // baseline days after a wipe — the user's mental model says comparisons
    // should reset alongside the data, and the data tier is where the
    // baseline math grounds out.
    //
    // Distinct from UnusualVolumeMinBaselineDays (which counts per-app
    // non-zero baseline days from traffic_daily for a different decision):
    // the hero-deltas guard cares "is there enough rolled-up history at
    // all?"; the unusual-volume guard cares "is there enough for THIS app?".
    private static int LoadBaselineDaysAvailable(
        SqliteConnection connection,
        DateOnly reportDate,
        int anchorDays,
        TimeZoneInfo localTz)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MIN(bucket_start) FROM traffic_daily;";
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return 0;

        var earliestMs    = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        var earliestUtc   = DateTimeOffset.FromUnixTimeMilliseconds(earliestMs).UtcDateTime;
        var earliestLocal = TimeZoneInfo.ConvertTimeFromUtc(earliestUtc, localTz);
        var earliestDate  = DateOnly.FromDateTime(earliestLocal);

        var daysSpan = reportDate.DayNumber - earliestDate.DayNumber;
        return Math.Clamp(daysSpan, 0, anchorDays);
    }

    // ─── Hourly sparkline series ───────────────────────────────────────────

    private const string SqlHourlySeries = """
        SELECT bucket_start,
               COALESCE(SUM(bytes_up),   0) AS up,
               COALESCE(SUM(bytes_down), 0) AS down
        FROM traffic_hourly
        WHERE bucket_start >= $start AND bucket_start < $end
        GROUP BY bucket_start
        ORDER BY bucket_start;
        """;

    private static IReadOnlyList<DailyReportHourPoint> LoadHourlySeries(
        SqliteConnection connection, long dayStart, long dayEnd, TimeZoneInfo localTz)
    {
        var localHourSums = new (long up, long down)[24];

        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlHourlySeries;
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var utcMs   = reader.GetInt64(0);
            var utc     = DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime;
            var local   = TimeZoneInfo.ConvertTimeFromUtc(utc, localTz);
            var hour    = local.Hour;
            if (hour < 0 || hour > 23) continue; // defensive; can't actually happen
            localHourSums[hour] = (
                localHourSums[hour].up   + reader.GetInt64(1),
                localHourSums[hour].down + reader.GetInt64(2));
        }

        var points = new DailyReportHourPoint[24];
        for (var h = 0; h < 24; h++)
            points[h] = new DailyReportHourPoint(h, localHourSums[h].up, localHourSums[h].down);
        return points;
    }

    // ─── Top apps ──────────────────────────────────────────────────────────

    private const string SqlTopApps = """
        SELECT a.app_id, a.image_name, a.image_path, a.publisher, a.signature_status,
               a.is_user_writable_path,
               COALESCE(SUM(h.bytes_up),   0) AS up,
               COALESCE(SUM(h.bytes_down), 0) AS down
        FROM apps a
        JOIN traffic_hourly h ON h.app_id = a.app_id
        WHERE h.bucket_start >= $start AND h.bucket_start < $end
        GROUP BY a.app_id
        HAVING up + down > 0
        ORDER BY (up + down) DESC, a.image_name
        LIMIT $limit;
        """;

    private static List<DailyReportAppRow> LoadTopApps(
        SqliteConnection connection, long dayStart, long dayEnd)
    {
        var rows = new List<DailyReportAppRow>(TopAppsLimit);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlTopApps;
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        cmd.Parameters.AddWithValue("$limit", TopAppsLimit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DailyReportAppRow(
                AppId:              reader.GetInt32(0),
                ImageName:          reader.GetString(1),
                ImagePath:          reader.GetString(2),
                Publisher:          reader.IsDBNull(3) ? null : reader.GetString(3),
                SignatureStatus:    reader.GetString(4),
                IsUserWritablePath: reader.GetInt32(5) != 0,
                BytesUp:            reader.GetInt64(6),
                BytesDown:          reader.GetInt64(7),
                HasOverlap:         false));
        }
        return rows;
    }

    // ─── svchost bracket suffix ────────────────────────────────────────────

    // svchost.exe rows get the bracketed service list appended to their
    // ImageName ("svchost.exe [Dnscache, NlaSvc]") so the user can tell which
    // service was talking, per mockup page 8 Q4 audit roster.
    private const string SqlHostedServices = """
        SELECT hosted_services
        FROM process_sessions
        WHERE app_id = $appId
          AND start_time < $end
          AND (end_time IS NULL OR end_time > $start)
          AND hosted_services IS NOT NULL
        ORDER BY LENGTH(hosted_services) DESC
        LIMIT 1;
        """;

    private static void EnrichSvchostBrackets(
        SqliteConnection connection, List<DailyReportAppRow> rows, long dayStart, long dayEnd)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!IsSvchost(r.ImageName)) continue;
            var services = LoadHostedServices(connection, r.AppId, dayStart, dayEnd);
            if (string.IsNullOrEmpty(services)) continue;
            rows[i] = r with { ImageName = $"{r.ImageName} [{services}]" };
        }
    }

    private static bool IsSvchost(string imageName) =>
        imageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);

    private static string? LoadHostedServices(
        SqliteConnection connection, int appId, long dayStart, long dayEnd)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlHostedServices;
        cmd.Parameters.AddWithValue("$appId", appId);
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        using var reader = cmd.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : null;
    }

    // ─── NewToday ──────────────────────────────────────────────────────────

    // An app counts as NewToday when (a) its publisher is non-null, (b) the
    // publisher has no earlier first_seen across the apps table (today is the
    // publisher's debut), and (c) the app had traffic on this day. Apps with
    // NULL publisher route to RiskyPaths only — "first publisher seen on this
    // machine" doesn't read sensibly without a publisher.
    private const string SqlNewToday = """
        SELECT a.app_id, a.image_name, a.publisher, a.signature_status, a.first_seen
        FROM apps a
        WHERE a.publisher IS NOT NULL
          AND a.first_seen >= $start AND a.first_seen < $end
          AND NOT EXISTS (
                SELECT 1 FROM apps a2
                WHERE a2.publisher = a.publisher
                  AND a2.first_seen < $start)
          AND EXISTS (
                SELECT 1 FROM traffic_hourly h
                WHERE h.app_id = a.app_id
                  AND h.bucket_start >= $start AND h.bucket_start < $end);
        """;

    private static List<DailyReportTalker> LoadNewToday(
        SqliteConnection connection, long dayStart, long dayEnd, TimeZoneInfo localTz)
    {
        var rows = new List<DailyReportTalker>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlNewToday;
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var firstSeenUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).UtcDateTime;
            var firstSeenLocal = TimeZoneInfo.ConvertTimeFromUtc(firstSeenUtc, localTz);
            var reason = $"First publisher seen on this machine. First WAN connection at {firstSeenLocal:HH:mm}.";
            rows.Add(new DailyReportTalker(
                Category:        UncommonCategory.NewToday,
                AppId:           reader.GetInt32(0),
                ImageName:       reader.GetString(1),
                Publisher:       reader.GetString(2),
                SignatureStatus: reader.GetString(3),
                Reason:          reason,
                HasOverlap:      false));
        }
        return rows;
    }

    // ─── UnusualVolume ─────────────────────────────────────────────────────

    // Two queries: (1) today's per-app Up/Down totals, (2) prior 7 days of
    // per-app per-day totals. C# computes the per-app median across the
    // baseline and flags apps where today's volume in the dominant direction
    // exceeds UnusualVolumeFactor × median, gated on
    // UnusualVolumeMinBaselineDays days of non-zero history.
    private const string SqlTodayPerApp = """
        SELECT h.app_id,
               COALESCE(SUM(h.bytes_up),   0) AS up,
               COALESCE(SUM(h.bytes_down), 0) AS down,
               a.image_name, a.publisher, a.signature_status
        FROM traffic_hourly h
        JOIN apps a ON a.app_id = h.app_id
        WHERE h.bucket_start >= $start AND h.bucket_start < $end
        GROUP BY h.app_id
        HAVING up + down > 0;
        """;

    private const string SqlBaselineDaily = """
        SELECT app_id, bucket_start,
               COALESCE(SUM(bytes_up),   0) AS up,
               COALESCE(SUM(bytes_down), 0) AS down
        FROM traffic_daily
        WHERE bucket_start >= $start AND bucket_start < $end
        GROUP BY app_id, bucket_start;
        """;

    private List<DailyReportTalker> LoadUnusualVolume(
        SqliteConnection connection, DateOnly reportDate,
        long dayStart, long dayEnd, TimeZoneInfo localTz)
    {
        // Today's per-app totals.
        var today = new Dictionary<int, (long up, long down, string name, string? publisher, string sig)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SqlTodayPerApp;
            cmd.Parameters.AddWithValue("$start", dayStart);
            cmd.Parameters.AddWithValue("$end",   dayEnd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                today[reader.GetInt32(0)] = (
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5));
            }
        }

        if (today.Count == 0) return new List<DailyReportTalker>();

        // Baseline window — 7 days preceding report day, daily granularity.
        var baselineStart = LocalDayWindowUtcMs(reportDate.AddDays(-7), localTz).start;
        var baselineEnd   = dayStart;
        var perAppDays = new Dictionary<int, List<(long up, long down)>>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SqlBaselineDaily;
            cmd.Parameters.AddWithValue("$start", baselineStart);
            cmd.Parameters.AddWithValue("$end",   baselineEnd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var appId = reader.GetInt32(0);
                if (!perAppDays.TryGetValue(appId, out var list))
                    perAppDays[appId] = list = new List<(long, long)>();
                list.Add((reader.GetInt64(2), reader.GetInt64(3)));
            }
        }

        var rows = new List<DailyReportTalker>();
        foreach (var (appId, t) in today)
        {
            if (!perAppDays.TryGetValue(appId, out var baseline)) continue;
            // Only count non-zero baseline days; a fresh install with 1 day
            // of history would otherwise false-fire on the second day.
            var nonZero = baseline.Where(d => d.up + d.down > 0).ToList();
            if (nonZero.Count < UnusualVolumeMinBaselineDays) continue;

            var medianUp   = Median(nonZero.Select(d => d.up).ToArray());
            var medianDown = Median(nonZero.Select(d => d.down).ToArray());

            // Pick the direction with the larger multiple.
            var upMult   = medianUp   > 0 ? t.up   / (double)medianUp   : 0;
            var downMult = medianDown > 0 ? t.down / (double)medianDown : 0;
            double mult;
            bool upDirection;
            long bytes;
            if (upMult >= downMult)   { mult = upMult;   upDirection = true;  bytes = t.up;   }
            else                       { mult = downMult; upDirection = false; bytes = t.down; }

            if (mult < UnusualVolumeFactor) continue;

            var direction = upDirection ? "Uploaded" : "Downloaded";
            var reason = $"{direction} {FormatBytes(bytes)}; {mult:0.0}× the 7-day median.";
            rows.Add(new DailyReportTalker(
                Category:        UncommonCategory.UnusualVolume,
                AppId:           appId,
                ImageName:       t.name,
                Publisher:       t.publisher,
                SignatureStatus: t.sig,
                Reason:          reason,
                HasOverlap:      false));
        }
        return rows;
    }

    private static long Median(long[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        var mid = values.Length / 2;
        return values.Length % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2;
    }

    // ─── RiskyPaths ────────────────────────────────────────────────────────

    private const string SqlRiskyPaths = """
        SELECT a.app_id, a.image_name, a.image_path, a.publisher, a.signature_status
        FROM apps a
        WHERE a.is_user_writable_path = 1
          AND a.signature_status IN ('Unsigned', 'Invalid')
          AND EXISTS (
                SELECT 1 FROM traffic_hourly h
                WHERE h.app_id = a.app_id
                  AND h.bucket_start >= $start AND h.bucket_start < $end);
        """;

    private static List<DailyReportTalker> LoadRiskyPaths(
        SqliteConnection connection, long dayStart, long dayEnd)
    {
        var rows = new List<DailyReportTalker>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlRiskyPaths;
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var appId     = reader.GetInt32(0);
            var name      = reader.GetString(1);
            var path      = reader.GetString(2);
            var publisher = reader.IsDBNull(3) ? null : reader.GetString(3);
            var sig       = reader.GetString(4);
            var wanCount  = CountWanEndpoints(connection, appId, dayStart, dayEnd);
            var endpointPhrase = wanCount == 1
                ? "Contacted 1 WAN endpoint."
                : $"Contacted {wanCount} WAN endpoints.";
            var reason    = $"{sig}, running from {AbbreviateUserWritablePath(path)}. {endpointPhrase}";
            rows.Add(new DailyReportTalker(
                Category:        UncommonCategory.RiskyPaths,
                AppId:           appId,
                ImageName:       name,
                Publisher:       publisher,
                SignatureStatus: sig,
                Reason:          reason,
                HasOverlap:      false));
        }
        return rows;
    }

    private const string SqlCountWanEndpoints = """
        SELECT COUNT(DISTINCT c.remote_addr || ':' || c.remote_port)
        FROM connections c
        JOIN process_sessions ps ON ps.session_id = c.session_id
        WHERE ps.app_id = $appId
          AND c.remote_class = 'Wan'
          AND c.first_seen < $end
          AND c.last_seen  >= $start;
        """;

    private static int CountWanEndpoints(
        SqliteConnection connection, int appId, long dayStart, long dayEnd)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlCountWanEndpoints;
        cmd.Parameters.AddWithValue("$appId", appId);
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    // ─── Notable: UnsignedFromUserPath (MVP only rule) ─────────────────────

    // Apps that match RiskyPaths AND made actual outbound WAN flows during
    // the day (not just observed file activity). The pid+time come from the
    // earliest WAN sample in the day so the entity-ref row reads truthfully.
    // LEFT JOIN to alerts on (type, entity_kind, entity_ref) so each
    // Notable row carries the matching alerts.alert_id when one exists.
    // The inner subquery picks the earliest alert per app within the day
    // window (MIN(alert_id) on an AUTOINCREMENT column = earliest by
    // created_at), keeping the link consistent with the day the report
    // covers. COALESCE(..., 0) preserves the Phase 5b sentinel so the
    // Reports UI's chip stays visible-but-inert for unmatched rows
    // (producer hadn't run yet, alerts row was purged by retention, etc.).
    private const string SqlNotableUnsigned = """
        SELECT a.app_id, a.image_name, a.image_path, ps.pid,
               MIN(s.bucket_start) AS first_wan_ms,
               COALESCE(al.alert_id, 0) AS alert_id
        FROM apps a
        JOIN process_sessions ps ON ps.app_id = a.app_id
        JOIN traffic_samples  s  ON s.session_id = ps.session_id
        LEFT JOIN (
            SELECT entity_ref, MIN(alert_id) AS alert_id
            FROM alerts
            WHERE type = 'UnsignedFromUserPath'
              AND entity_kind = 'App'
              AND created_at >= $start AND created_at < $end
            GROUP BY entity_ref
        ) al ON al.entity_ref = CAST(a.app_id AS TEXT)
        WHERE a.is_user_writable_path = 1
          AND a.signature_status IN ('Unsigned', 'Invalid')
          AND s.remote_class = 'Wan'
          AND s.bucket_start >= $start AND s.bucket_start < $end
        GROUP BY a.app_id, ps.pid, al.alert_id
        ORDER BY a.app_id, first_wan_ms;
        """;

    private List<DailyReportNotable> LoadNotable(
        SqliteConnection connection, long dayStart, long dayEnd)
    {
        var rows = new List<DailyReportNotable>();
        // Dedupe by app — multiple sessions of the same app collapse to the
        // first matching session's pid+time.
        var seenApps = new HashSet<int>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlNotableUnsigned;
        cmd.Parameters.AddWithValue("$start", dayStart);
        cmd.Parameters.AddWithValue("$end",   dayEnd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var appId = reader.GetInt32(0);
            if (!seenApps.Add(appId)) continue;
            var name      = reader.GetString(1);
            var path      = reader.GetString(2);
            var pid       = reader.GetInt32(3);
            var firstWanMs = reader.GetInt64(4);
            // Phase 6.4 — real alerts.alert_id projected via the LEFT JOIN.
            // COALESCE-defaulted to 0 in SQL when no alerts row matches the
            // (type, entity_kind, entity_ref) key. The Reports UI keeps the
            // chip visible-but-inert at 0.
            var alertId   = reader.GetInt32(5);
            var wanCount  = CountWanEndpoints(connection, appId, dayStart, dayEnd);
            var endpointPhrase = wanCount == 1
                ? "It contacted 1 WAN endpoint during the day."
                : $"It contacted {wanCount} WAN endpoints during the day.";
            rows.Add(new DailyReportNotable(
                Severity:        NotableSeverity.Critical,
                Title:           "Unsigned binary from a user-writable path",
                Detail:          $"{name} is unsigned and runs from {AbbreviateUserWritablePath(path)}. {endpointPhrase}",
                AppId:           appId,
                ImageName:       name,
                Pid:             pid,
                EventTimeUnixMs: firstWanMs,
                AlertId:         alertId));
        }
        return rows;
    }

    // ─── Path abbreviation ─────────────────────────────────────────────────

    // Server-side path abbreviation. We can't use Environment.GetEnvironmentVariable
    // because the service runs as LocalSystem (its %TEMP% is C:\Windows\Temp,
    // not the user's). Match the well-known user-profile patterns by regex.
    // Order matters: longer / more-specific patterns first.
    private static readonly (Regex pattern, string token)[] PathAbbreviations =
    {
        (new Regex(@"^C:\\Users\\[^\\]+\\AppData\\Local\\Temp(?=\\|$)",      RegexOptions.IgnoreCase | RegexOptions.Compiled), "%TEMP%"),
        (new Regex(@"^C:\\Users\\[^\\]+\\AppData\\Local(?=\\|$)",            RegexOptions.IgnoreCase | RegexOptions.Compiled), "%LOCALAPPDATA%"),
        (new Regex(@"^C:\\Users\\[^\\]+\\AppData\\Roaming(?=\\|$)",          RegexOptions.IgnoreCase | RegexOptions.Compiled), "%APPDATA%"),
        (new Regex(@"^C:\\Users\\[^\\]+(?=\\|$)",                            RegexOptions.IgnoreCase | RegexOptions.Compiled), "%USERPROFILE%"),
    };

    internal static string AbbreviateUserWritablePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        foreach (var (pattern, token) in PathAbbreviations)
        {
            var m = pattern.Match(path);
            if (m.Success) return token + path[m.Length..];
        }
        return path;
    }

    // ─── HasOverlap flag ───────────────────────────────────────────────────

    // An app has overlap when it appears in Top Apps AND in either the
    // talker or notable lists (mockup page 8 Q4 audit). We set the flag on
    // both ends so the dot paints consistently across surfaces.
    private static void ApplyOverlapFlags(
        List<DailyReportAppRow> topApps,
        List<DailyReportTalker> talkers,
        IReadOnlyList<DailyReportNotable> notable)
    {
        var topIds      = new HashSet<int>(topApps.Select(r => r.AppId));
        var talkerIds   = new HashSet<int>(talkers.Select(t => t.AppId));
        var notableIds  = new HashSet<int>(notable.Select(n => n.AppId));
        var otherIds    = new HashSet<int>(talkerIds);
        otherIds.UnionWith(notableIds);

        for (var i = 0; i < topApps.Count; i++)
        {
            if (otherIds.Contains(topApps[i].AppId))
                topApps[i] = topApps[i] with { HasOverlap = true };
        }
        for (var i = 0; i < talkers.Count; i++)
        {
            if (topIds.Contains(talkers[i].AppId))
                talkers[i] = talkers[i] with { HasOverlap = true };
        }
    }

    // ─── Byte formatting ───────────────────────────────────────────────────

    // Server-side byte formatter for reason text. Mirrors the UI's
    // TopAppRow.FormatBytes shape so prose reads consistently with the
    // numeric columns the user already sees.
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
