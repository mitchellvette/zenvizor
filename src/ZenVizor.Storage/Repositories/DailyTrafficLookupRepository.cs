// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Read-only lookup over <c>traffic_daily</c> joined with <c>apps</c> for
/// the Phase 6.7 <c>UnusualDailyVolumeRule</c>. Returns per-app per-day
/// totals across a date range so the rule can compute a 14-day baseline
/// median + delta against yesterday's bytes.
/// </summary>
public sealed class DailyTrafficLookupRepository
{
    private readonly ConnectionFactory _connections;

    public DailyTrafficLookupRepository(ConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Per-app per-day bytes summed across remote_class within the
    /// half-open range <c>[fromUnixMsInclusive, toUnixMsExclusive)</c>.
    /// Joined against <c>apps</c> for the image_name + image_path that
    /// the rule's detail string needs. Apps with zero traffic in the
    /// range simply don't appear — the rule's "fewer than 14 baseline
    /// days" gate handles that case.
    /// </summary>
    public IReadOnlyList<DailyTrafficLookupRow> GetDailyTotals(long fromUnixMsInclusive, long toUnixMsExclusive)
    {
        if (toUnixMsExclusive <= fromUnixMsInclusive)
        {
            return Array.Empty<DailyTrafficLookupRow>();
        }

        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT td.app_id,
                   a.image_name,
                   a.image_path,
                   td.bucket_start,
                   SUM(td.bytes_up)   AS up_bytes,
                   SUM(td.bytes_down) AS down_bytes
            FROM traffic_daily td
            JOIN apps a ON a.app_id = td.app_id
            WHERE td.bucket_start >= $from
              AND td.bucket_start <  $to
            GROUP BY td.app_id, td.bucket_start
            ORDER BY td.app_id, td.bucket_start;
            """;
        cmd.Parameters.AddWithValue("$from", fromUnixMsInclusive);
        cmd.Parameters.AddWithValue("$to",   toUnixMsExclusive);

        var rows = new List<DailyTrafficLookupRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DailyTrafficLookupRow(
                AppId:           reader.GetInt32(0),
                ImageName:       reader.GetString(1),
                ImagePath:       reader.GetString(2),
                BucketStartUnixMs: reader.GetInt64(3),
                BytesUp:         reader.GetInt64(4),
                BytesDown:       reader.GetInt64(5)));
        }
        return rows;
    }
}

/// <summary>One row of the <c>traffic_daily</c> ⋈ <c>apps</c> projection.</summary>
public sealed record DailyTrafficLookupRow(
    int AppId,
    string ImageName,
    string ImagePath,
    long BucketStartUnixMs,
    long BytesUp,
    long BytesDown);
