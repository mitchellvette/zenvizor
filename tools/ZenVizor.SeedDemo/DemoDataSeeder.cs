// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ZenVizor.SeedDemo;

/// <summary>
/// Populates a freshly-migrated THROWAWAY store with the fixed synthetic
/// dataset used for marketing screenshots. Every value here is invented and
/// privacy-safe: a "demo" identity, RFC 5737 documentation IPs
/// (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24), and one deliberate real
/// IP (162.159.61.3) that matches the website's ECH FAQ. No real usernames,
/// machine names, private, or loopback addresses appear anywhere.
/// <para>
/// The dataset is shaped to drive every read surface the app renders from
/// the DB: the daily-report hero/hourly/top-apps/uncommon-talkers/notable
/// lists, and the alerts feed. Alert copy is produced from the app's real
/// rule templates (ZenVizor.Core.Alerts) so the "why this matters" panels
/// render exactly as production would. Byte shapes are deterministic (fixed
/// RNG seed); the "today" timestamps are relative to when you seed so the
/// demo always looks current.
/// </para>
/// </summary>
internal static class DemoDataSeeder
{
    private const long KiB = 1024L;
    private const long MiB = 1024L * 1024L;
    private const long HourMs = 3_600_000L;
    private const long DayMs = 86_400_000L;
    private const int HistoryDays = 30;

    private const string ChromePath   = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string SvchostPath  = @"C:\Windows\System32\svchost.exe";
    private const string OneDrivePath = @"C:\Program Files\Microsoft OneDrive\OneDrive.exe";
    private const string UnknownPath  = @"C:\Users\demo\Downloads\unknown_setup.exe";
    private const string LegacyPath   = @"C:\Program Files\LegacyTool\LegacyTool.exe";

    public static string Seed(string dbPath)
    {
        var tz = TimeZoneInfo.Local;
        var nowLocal = DateTime.Now;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var today = DateOnly.FromDateTime(nowLocal);
        var currentHour = nowLocal.Hour;
        var todayStart = LocalMidnightMs(today, tz);
        var rng = new Random(20260601);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        // ─── timeline anchors ──────────────────────────────────────────────
        var thirtyDaysAgo = nowMs - (HistoryDays * DayMs);
        var unknownFirstSeen = RoundToMinute(Math.Max(todayStart + (5 * 60_000L), nowMs - (3 * HourMs)));
        var unknownFirstConn = unknownFirstSeen + 8_000L;
        var legacyFirstSeen = RoundToMinute(Math.Max(todayStart + (10 * 60_000L), nowMs - (2 * HourMs)));
        var legacyFirstConn = legacyFirstSeen + 5_000L;

        // ─── apps ──────────────────────────────────────────────────────────
        var chromeId   = InsertApp(conn, tx, "chrome.exe", ChromePath, "Google LLC", "Signed", 0, "System", thirtyDaysAgo, nowMs);
        var svchostId  = InsertApp(conn, tx, "svchost.exe", SvchostPath, "Microsoft Corporation", "Signed", 0, "System", thirtyDaysAgo, nowMs);
        var oneDriveId = InsertApp(conn, tx, "OneDrive.exe", OneDrivePath, "Microsoft Corporation", "Signed", 0, "System", thirtyDaysAgo, nowMs);
        var unknownId  = InsertApp(conn, tx, "unknown_setup.exe", UnknownPath, null, "Unsigned", 1, "UserWritable", unknownFirstSeen, nowMs);
        var legacyId   = InsertApp(conn, tx, "LegacyTool.exe", LegacyPath, "Example Corp", "Invalid", 0, "System", legacyFirstSeen, nowMs);

        // ─── sessions (all still running: end_time NULL) ───────────────────
        var chromeSess   = InsertSession(conn, tx, chromeId, 4820, nowMs - (8 * HourMs), null);
        var svchostSess  = InsertSession(conn, tx, svchostId, 1364, nowMs - (8 * HourMs), "Dnscache,BITS,wuauserv");
        var oneDriveSess = InsertSession(conn, tx, oneDriveId, 7220, nowMs - (8 * HourMs), null);
        var unknownSess  = InsertSession(conn, tx, unknownId, 9088, unknownFirstSeen, null);
        var legacySess   = InsertSession(conn, tx, legacyId, 6540, legacyFirstSeen, null);

        // ─── per-app daily byte plan (MiB) ─────────────────────────────────
        var chromeUp = new long[HistoryDays];
        var chromeDown = new long[HistoryDays];
        chromeUp[0] = 22 * MiB; chromeDown[0] = 440 * MiB;   // today
        chromeUp[1] = 38 * MiB; chromeDown[1] = 592 * MiB;   // yesterday's spike (stored alert)
        for (var d = 2; d < HistoryDays; d++)
        {
            long totalMiB = 185 + rng.Next(-12, 13);
            long upMiB = (long)Math.Round(totalMiB * 0.06);
            chromeUp[d] = upMiB * MiB;
            chromeDown[d] = (totalMiB - upMiB) * MiB;
        }

        var svUp = new long[HistoryDays];
        var svDown = new long[HistoryDays];
        var svLocalUp = new long[HistoryDays];
        var svLocalDown = new long[HistoryDays];
        svUp[0] = 6 * MiB; svDown[0] = 94 * MiB; svLocalUp[0] = 2 * MiB; svLocalDown[0] = 40 * MiB;
        for (var d = 1; d < HistoryDays; d++)
        {
            svUp[d] = 3 * MiB; svDown[d] = (26 + rng.Next(0, 7)) * MiB;
            svLocalUp[d] = 1 * MiB; svLocalDown[d] = (4 + rng.Next(0, 3)) * MiB;
        }

        var odUp = new long[HistoryDays];
        var odDown = new long[HistoryDays];
        odUp[0] = 95 * MiB; odDown[0] = 8 * MiB;
        for (var d = 1; d < HistoryDays; d++)
        {
            odUp[d] = (12 + rng.Next(0, 11)) * MiB;
            odDown[d] = (6 + rng.Next(0, 7)) * MiB;
        }

        // ─── emit helpers ──────────────────────────────────────────────────
        var dailyCount = 0;
        var hourlyCount = 0;
        var sampleCount = 0;

        int[] SpreadHours(int dayOffset)
        {
            if (dayOffset != 0) return Enumerable.Range(8, 15).ToArray(); // 08:00–22:00
            var daytime = Enumerable.Range(8, 15).Where(h => h <= currentHour).ToArray();
            if (daytime.Length > 0) return daytime;
            var lo = Math.Max(0, currentHour - 6);
            return Enumerable.Range(lo, currentHour - lo + 1).ToArray();
        }

        int[] ConcentratedTodayHours()
        {
            var lo = Math.Max(0, currentHour - 2);
            return Enumerable.Range(lo, currentHour - lo + 1).ToArray();
        }

        void EmitDaily(long appId, int dayOffset, long up, long down, string cls)
        {
            if (up == 0 && down == 0) return;
            var bucket = LocalMidnightMs(today.AddDays(-dayOffset), tz);
            InsertDaily(conn, tx, appId, bucket, up, down, cls);
            dailyCount++;
        }

        void EmitHourly(long appId, int dayOffset, int[] hours, long totalUp, long totalDown, string cls)
        {
            if (hours.Length == 0) return;
            var baseMs = LocalMidnightMs(today.AddDays(-dayOffset), tz);
            var ups = Allocate(totalUp, hours.Length, rng);
            var downs = Allocate(totalDown, hours.Length, rng);
            for (var i = 0; i < hours.Length; i++)
            {
                if (ups[i] == 0 && downs[i] == 0) continue;
                InsertHourly(conn, tx, appId, baseMs + (hours[i] * HourMs), ups[i], downs[i], cls);
                hourlyCount++;
            }
        }

        // chrome — WAN only
        for (var d = 0; d < HistoryDays; d++) EmitDaily(chromeId, d, chromeUp[d], chromeDown[d], "Wan");
        for (var d = 0; d <= 2; d++) EmitHourly(chromeId, d, SpreadHours(d), chromeUp[d], chromeDown[d], "Wan");

        // svchost — WAN + Local
        for (var d = 0; d < HistoryDays; d++)
        {
            EmitDaily(svchostId, d, svUp[d], svDown[d], "Wan");
            EmitDaily(svchostId, d, svLocalUp[d], svLocalDown[d], "Local");
        }
        for (var d = 0; d <= 2; d++)
        {
            EmitHourly(svchostId, d, SpreadHours(d), svUp[d], svDown[d], "Wan");
            EmitHourly(svchostId, d, SpreadHours(d), svLocalUp[d], svLocalDown[d], "Local");
        }

        // OneDrive — WAN only
        for (var d = 0; d < HistoryDays; d++) EmitDaily(oneDriveId, d, odUp[d], odDown[d], "Wan");
        for (var d = 0; d <= 2; d++) EmitHourly(oneDriveId, d, SpreadHours(d), odUp[d], odDown[d], "Wan");

        // unknown_setup + LegacyTool — first seen today, small WAN footprint
        EmitDaily(unknownId, 0, 1 * MiB, 3 * MiB, "Wan");
        EmitHourly(unknownId, 0, ConcentratedTodayHours(), 1 * MiB, 3 * MiB, "Wan");
        EmitDaily(legacyId, 0, 2 * MiB, 5 * MiB, "Wan");
        EmitHourly(legacyId, 0, ConcentratedTodayHours(), 2 * MiB, 5 * MiB, "Wan");

        // ─── traffic_samples (high-res tier, last ~6h) ─────────────────────
        void EmitRecentSamples(long sessionId, string cls, long baseUp, long baseDown)
        {
            var anchor = RoundToMinute(nowMs);
            for (var k = 0; k < 72; k++)
            {
                var bucket = anchor - (k * 5 * 60_000L);
                var up = baseUp + rng.Next(0, (int)(baseUp / 2) + 1);
                var down = baseDown + rng.Next(0, (int)(baseDown / 2) + 1);
                InsertSample(conn, tx, sessionId, bucket, up, down, cls);
                sampleCount++;
            }
        }

        EmitRecentSamples(chromeSess, "Wan", 96 * KiB, 900 * KiB);
        EmitRecentSamples(svchostSess, "Wan", 24 * KiB, 160 * KiB);
        EmitRecentSamples(svchostSess, "Local", 8 * KiB, 48 * KiB);
        EmitRecentSamples(oneDriveSess, "Wan", 200 * KiB, 32 * KiB);

        // unknown_setup WAN cluster around its first connection — this is what
        // the report's Notable list keys off (unsigned, user-writable, WAN
        // sample within today's window).
        var unknownAnchor = RoundToMinute(unknownFirstConn);
        for (var i = 0; i < 4; i++)
        {
            InsertSample(conn, tx, unknownSess, unknownAnchor + (i * 60_000L), 64 * KiB, 220 * KiB, "Wan");
            sampleCount++;
        }

        var legacyAnchor = RoundToMinute(legacyFirstConn);
        for (var i = 0; i < 3; i++)
        {
            InsertSample(conn, tx, legacySess, legacyAnchor + (i * 60_000L), 96 * KiB, 260 * KiB, "Wan");
            sampleCount++;
        }

        // ─── connections (WAN only, documentation IPs + one ECH real IP) ───
        var connCount = 0;
        void EmitConn(long sessionId, string protocol, string addr, int port, string? host, long up, long down, long firstSeen)
        {
            InsertConnection(conn, tx, sessionId, protocol, addr, port, "Wan", host, up, down, firstSeen, nowMs);
            connCount++;
        }

        EmitConn(chromeSess, "TCP", "203.0.113.50", 443, "youtube.com", 3 * MiB, 210 * MiB, nowMs - (6 * HourMs));
        EmitConn(chromeSess, "TCP", "198.51.100.20", 443, "www.google.com", 2 * MiB, 8 * MiB, nowMs - (6 * HourMs));
        EmitConn(chromeSess, "TCP", "203.0.113.60", 443, "rr3---sn-4g5ednsz.googlevideo.com", 4 * MiB, 220 * MiB, nowMs - (5 * HourMs));
        EmitConn(chromeSess, "TCP", "162.159.61.3", 443, null, 1 * MiB, 12 * MiB, nowMs - (4 * HourMs)); // ECH — unresolved host
        EmitConn(svchostSess, "TCP", "203.0.113.100", 443, null, 3 * MiB, 60 * MiB, nowMs - (5 * HourMs)); // BITS
        EmitConn(svchostSess, "UDP", "203.0.113.53", 53, null, 200 * KiB, 400 * KiB, nowMs - (5 * HourMs)); // DNS
        EmitConn(oneDriveSess, "TCP", "203.0.113.120", 443, null, 95 * MiB, 8 * MiB, nowMs - (3 * HourMs));
        EmitConn(unknownSess, "TCP", "203.0.113.10", 443, null, 1 * MiB, 3 * MiB, unknownFirstConn);
        EmitConn(legacySess, "TCP", "198.51.100.77", 443, null, 2 * MiB, 5 * MiB, legacyFirstConn);

        // ─── alerts (rendered from the app's real rule templates) ──────────
        var alertCount = 0;
        void EmitAlert(string type, string severity, long createdAt, string source, long appId, string title, string detail)
        {
            InsertAlert(conn, tx, type, severity, createdAt, source, "App",
                appId.ToString(CultureInfo.InvariantCulture), title, detail);
            alertCount++;
        }

        // 1. UnsignedFromUserPath (Critical, Capture) — unknown_setup.
        EmitAlert("UnsignedFromUserPath", "Critical", unknownFirstConn, "Capture", unknownId,
            "Unsigned program talking to the network: unknown_setup.exe",
            $"unknown_setup.exe is running from a user-writable folder and started making " +
            $"network connections. Image path: {UnknownPath}. " +
            $"Signer: none (unsigned). " +
            $"First connection: {FormatMinute(unknownFirstConn)}. " +
            $"Connections so far: 3.");

        // 2. InvalidSignature (Critical, Capture) — LegacyTool.
        EmitAlert("InvalidSignature", "Critical", legacyFirstConn, "Capture", legacyId,
            "Program with invalid signature talking to the network: LegacyTool.exe",
            $"LegacyTool.exe is signed but the signature does not verify (tampered, expired, or revoked). " +
            $"Image path: {LegacyPath}. " +
            $"Signer: Example Corp. " +
            $"First connection: {FormatMinute(legacyFirstConn)}. " +
            $"Connections so far: 2.");

        // 3. OutboundHeavy (Warning, Capture) — OneDrive. 78.5 MB up vs 9.8 MB down, ratio 8.0x.
        const long obUp = 82_313_216L;
        const long obDown = 10_289_152L;
        EmitAlert("OutboundHeavy", "Warning", nowMs - (20 * 60_000L), "Capture", oneDriveId,
            "Outbound-heavy app: OneDrive.exe",
            $"OneDrive.exe uploaded {FormatBytesBinary(obUp)} in the last 15 minutes, " +
            $"vs {FormatBytesBinary(obDown)} downloaded (ratio {(double)obUp / obDown:0.0}x). " +
            $"Image path: {OneDrivePath}. " +
            $"Observed across PID 7220.");

        // 4. UnusualDailyVolume (Warning, Rollup) — chrome, referencing yesterday's spike.
        const long yesterdayTotal = 630 * MiB;
        const long baselineMedian = 185 * MiB;
        const double k = 2.5;
        var yesterdayMs = LocalMidnightMs(today.AddDays(-1), tz);
        EmitAlert("UnusualDailyVolume", "Warning", todayStart + (6 * 60_000L), "Rollup", chromeId,
            "Unusual daily traffic: chrome.exe",
            $"chrome.exe used {FormatBytesBinary(yesterdayTotal)} on {FormatDay(yesterdayMs)}, " +
            $"vs {FormatBytesBinary(baselineMedian)} typical " +
            $"(over {(double)yesterdayTotal / baselineMedian:0.0}x baseline; threshold is {k:0.0}x). " +
            $"Image path: {ChromePath}. " +
            $"Baseline derived from the prior 14 days of traffic.");

        // 5. FirstRunWanTalker (Info, Capture) — unknown_setup.
        var ageSeconds = (unknownFirstConn - unknownFirstSeen) / 1000;
        EmitAlert("FirstRunWanTalker", "Info", unknownFirstConn + 2_000L, "Capture", unknownId,
            "Newly-installed program reached the network: unknown_setup.exe",
            $"unknown_setup.exe was first observed at {FormatSecond(unknownFirstSeen)} and opened its " +
            $"first network connection at {FormatSecond(unknownFirstConn)} ({ageSeconds} s after first observed). " +
            $"Image path: {UnknownPath}. " +
            $"Connections so far: 3.");

        // 6. LargeDownload (Info, Capture) — chrome pulled 148 MB from the CDN endpoint.
        const long ldBytes = 148 * MiB;
        EmitAlert("LargeDownload", "Info", nowMs - (40 * 60_000L), "Capture", chromeId,
            "Large download by chrome.exe",
            $"chrome.exe pulled {FormatBytesKilo(ldBytes)} from 203.0.113.60:443 in under 60 seconds. " +
            $"Image path: {ChromePath}. " +
            $"Total qualifying downloads: 2 (PID 4820).");

        tx.Commit();

        return
            $"  Seeded 5 apps, 5 sessions, {dailyCount} daily + {hourlyCount} hourly rollup rows,\n" +
            $"  {sampleCount} traffic samples, {connCount} connections, and {alertCount} alerts.";
    }

    // ─── local-time helpers ────────────────────────────────────────────────

    private static long LocalMidnightMs(DateOnly date, TimeZoneInfo tz)
    {
        var localMidnight = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    private static long RoundToMinute(long unixMs) => unixMs - (unixMs % 60_000L);

    private static string FormatMinute(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string FormatSecond(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatDay(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Mirror OutboundHeavyRule / UnusualDailyVolumeRule FormatBytes exactly so
    // the seeded detail strings are byte-for-byte what the live rules render.
    private static string FormatBytesBinary(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
    }

    // Mirror LargeDownloadRule.FormatBytes (KB floor, no sub-KB branch).
    private static string FormatBytesKilo(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
    }

    // Largest-remainder allocation of an integer total across n buckets with
    // randomized weights. Preserves the exact total so hourly rows sum to the
    // canonical daily figure.
    private static long[] Allocate(long total, int n, Random rng)
    {
        var result = new long[n];
        if (n <= 0 || total <= 0) return result;

        var weights = new double[n];
        double weightSum = 0;
        for (var i = 0; i < n; i++)
        {
            weights[i] = 0.5 + rng.NextDouble();
            weightSum += weights[i];
        }

        long allocated = 0;
        var fractions = new (int Index, double Fraction)[n];
        for (var i = 0; i < n; i++)
        {
            var exact = total * weights[i] / weightSum;
            var floor = (long)Math.Floor(exact);
            result[i] = floor;
            allocated += floor;
            fractions[i] = (i, exact - floor);
        }

        Array.Sort(fractions, (a, b) => b.Fraction.CompareTo(a.Fraction));
        var remainder = total - allocated;
        for (long i = 0; i < remainder && i < n; i++)
        {
            result[fractions[(int)i].Index] += 1;
        }
        return result;
    }

    // ─── parameterized inserts (const SQL literals → CA2100-safe) ───────────

    private const string InsertAppSql = """
        INSERT INTO apps (image_path, image_name, publisher, signature_status,
                          is_user_writable_path, first_seen, last_seen, path_class)
        VALUES ($path, $name, $publisher, $sig, $uwp, $first, $last, $pathClass);
        """;

    private static long InsertApp(
        SqliteConnection conn, SqliteTransaction tx,
        string imageName, string imagePath, string? publisher, string signatureStatus,
        int isUserWritable, string pathClass, long firstSeen, long lastSeen)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertAppSql;
        cmd.Parameters.AddWithValue("$path", imagePath);
        cmd.Parameters.AddWithValue("$name", imageName);
        cmd.Parameters.AddWithValue("$publisher", (object?)publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", signatureStatus);
        cmd.Parameters.AddWithValue("$uwp", isUserWritable);
        cmd.Parameters.AddWithValue("$first", firstSeen);
        cmd.Parameters.AddWithValue("$last", lastSeen);
        cmd.Parameters.AddWithValue("$pathClass", pathClass);
        cmd.ExecuteNonQuery();
        return LastId(conn, tx);
    }

    private const string InsertSessionSql = """
        INSERT INTO process_sessions (app_id, pid, start_time, end_time, hosted_services)
        VALUES ($app, $pid, $start, NULL, $svc);
        """;

    private static long InsertSession(
        SqliteConnection conn, SqliteTransaction tx,
        long appId, int pid, long startTime, string? hostedServices)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertSessionSql;
        cmd.Parameters.AddWithValue("$app", appId);
        cmd.Parameters.AddWithValue("$pid", pid);
        cmd.Parameters.AddWithValue("$start", startTime);
        cmd.Parameters.AddWithValue("$svc", (object?)hostedServices ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return LastId(conn, tx);
    }

    private const string InsertDailySql = """
        INSERT INTO traffic_daily (app_id, bucket_start, bytes_up, bytes_down, remote_class)
        VALUES ($app, $bucket, $up, $down, $class);
        """;

    private static void InsertDaily(
        SqliteConnection conn, SqliteTransaction tx,
        long appId, long bucketStart, long bytesUp, long bytesDown, string remoteClass)
        => InsertRollup(conn, tx, InsertDailySql, appId, bucketStart, bytesUp, bytesDown, remoteClass);

    private const string InsertHourlySql = """
        INSERT INTO traffic_hourly (app_id, bucket_start, bytes_up, bytes_down, remote_class)
        VALUES ($app, $bucket, $up, $down, $class);
        """;

    private static void InsertHourly(
        SqliteConnection conn, SqliteTransaction tx,
        long appId, long bucketStart, long bytesUp, long bytesDown, string remoteClass)
        => InsertRollup(conn, tx, InsertHourlySql, appId, bucketStart, bytesUp, bytesDown, remoteClass);

    private static void InsertRollup(
        SqliteConnection conn, SqliteTransaction tx, string sql,
        long appId, long bucketStart, long bytesUp, long bytesDown, string remoteClass)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$app", appId);
        cmd.Parameters.AddWithValue("$bucket", bucketStart);
        cmd.Parameters.AddWithValue("$up", bytesUp);
        cmd.Parameters.AddWithValue("$down", bytesDown);
        cmd.Parameters.AddWithValue("$class", remoteClass);
        cmd.ExecuteNonQuery();
    }

    private const string InsertSampleSql = """
        INSERT INTO traffic_samples (session_id, bucket_start, bytes_up, bytes_down, remote_class)
        VALUES ($session, $bucket, $up, $down, $class);
        """;

    private static void InsertSample(
        SqliteConnection conn, SqliteTransaction tx,
        long sessionId, long bucketStart, long bytesUp, long bytesDown, string remoteClass)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertSampleSql;
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$bucket", bucketStart);
        cmd.Parameters.AddWithValue("$up", bytesUp);
        cmd.Parameters.AddWithValue("$down", bytesDown);
        cmd.Parameters.AddWithValue("$class", remoteClass);
        cmd.ExecuteNonQuery();
    }

    private const string InsertConnectionSql = """
        INSERT INTO connections (session_id, protocol, remote_addr, remote_port, remote_class,
                                 resolved_host, bytes_up, bytes_down, first_seen, last_seen)
        VALUES ($session, $protocol, $addr, $port, $class, $host, $up, $down, $first, $last);
        """;

    private static void InsertConnection(
        SqliteConnection conn, SqliteTransaction tx,
        long sessionId, string protocol, string remoteAddr, int remotePort, string remoteClass,
        string? resolvedHost, long bytesUp, long bytesDown, long firstSeen, long lastSeen)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertConnectionSql;
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$protocol", protocol);
        cmd.Parameters.AddWithValue("$addr", remoteAddr);
        cmd.Parameters.AddWithValue("$port", remotePort);
        cmd.Parameters.AddWithValue("$class", remoteClass);
        cmd.Parameters.AddWithValue("$host", (object?)resolvedHost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$up", bytesUp);
        cmd.Parameters.AddWithValue("$down", bytesDown);
        cmd.Parameters.AddWithValue("$first", firstSeen);
        cmd.Parameters.AddWithValue("$last", lastSeen);
        cmd.ExecuteNonQuery();
    }

    private const string InsertAlertSql = """
        INSERT INTO alerts (type, severity, created_at, source_monitor, entity_kind,
                            entity_ref, title, detail, acknowledged_at)
        VALUES ($type, $sev, $created, $source, $kind, $ref, $title, $detail, NULL);
        """;

    private static void InsertAlert(
        SqliteConnection conn, SqliteTransaction tx,
        string type, string severity, long createdAt, string sourceMonitor,
        string entityKind, string entityRef, string title, string detail)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertAlertSql;
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$sev", severity);
        cmd.Parameters.AddWithValue("$created", createdAt);
        cmd.Parameters.AddWithValue("$source", sourceMonitor);
        cmd.Parameters.AddWithValue("$kind", entityKind);
        cmd.Parameters.AddWithValue("$ref", entityRef);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.ExecuteNonQuery();
    }

    private const string LastIdSql = "SELECT last_insert_rowid();";

    private static long LastId(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = LastIdSql;
        return (long)cmd.ExecuteScalar()!;
    }
}
