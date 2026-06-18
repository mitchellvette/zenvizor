using System.Globalization;
using ZenVizor.Core.Aggregation;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Fires when an app's traffic for the previous UTC day is at least
/// k × the median of the prior 14 days AND the delta over median
/// clears a 50 MB floor. k is user-tunable via
/// <see cref="IAlertSettingsLookup.UnusualDailyVolumeKTimesTen"/>
/// (default 25 = 2.5×). Severity Warning per catalog §1.4. Source
/// <see cref="SourceMonitor.Rollup"/>.
/// <para>
/// Date-roll gated: evaluates AT MOST once per UTC day. The rule tracks
/// <see cref="_lastEvalDayUnixMs"/> internally; <see cref="Evaluate"/>
/// checks <c>AlignToDay(flushTime)</c> against it and only walks the
/// repository when the day has rolled. The producer's standard 24h
/// per-app cooldown handles the dismiss-and-re-arm path.
/// </para>
/// <para>
/// Formula divergence from the catalog brief: the original spec called
/// for <c>bytes ≥ median + k × MAD</c> (robust statistics, variance-
/// aware). Phase 6.7 ships the simpler <c>bytes ≥ k × median</c>
/// because the user-facing slider is "fire at 2.5× typical" and the
/// MAD-based formula's threshold depends on per-app variance in a way
/// that doesn't match that mental model. Revert if low-variance apps
/// generate noise — the median + MAD math is preserved in
/// <see cref="IpcSchemaVersion.Settings"/> remarks for future reference.
/// </para>
/// <para>
/// UTC day alignment: the rule reads <c>traffic_daily.bucket_start</c>
/// which is UTC-midnight aligned. Users in non-UTC timezones get the
/// alert about their "previous UTC day" rather than their "previous
/// local day." Acceptable for v1; refactoring to local-day alignment
/// would require re-aggregating traffic_hourly per local day.
/// </para>
/// </summary>
public sealed class UnusualDailyVolumeRule : IFlushAlertRule, IResettableRollupRule
{
    /// <summary>
    /// Hard-coded MB floor on (yesterday.bytes - baseline.median). A
    /// low-variance app's median of 100 MB at k=2.5 would fire at
    /// 250 MB regardless; this floor adds the additional constraint
    /// that the delta has to be substantial. 50 MB sits at the same
    /// "this matters" line as the LargeDownload default.
    /// </summary>
    public const long FloorBytes = 50L * 1024L * 1024L;

    /// <summary>Days of baseline required before the rule will evaluate an app.</summary>
    public const int BaselineDays = 14;

    /// <summary>24h cooldown per app — catalog warning-tier lock.</summary>
    public long CooldownMs => TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// Repository lookup delegate. Called with a half-open range; returns
    /// per-app per-day totals. Injected so tests can supply a synthetic
    /// data source without a SQLite dependency.
    /// </summary>
    public delegate IReadOnlyList<DailyVolumeRow> DailyVolumeQuery(long fromUnixMsInclusive, long toUnixMsExclusive);

    private readonly IAlertSettingsLookup _settings;
    private readonly DailyVolumeQuery _query;

    // UTC-day-aligned timestamp of the last evaluation. Zero on first
    // run so the rule evaluates immediately. ResetLastEvalDate() forces
    // re-evaluation on the next flush — used by the debug
    // RunRollupRulesNowAsync IPC hook for QA.
    private long _lastEvalDayUnixMs;

    public UnusualDailyVolumeRule(IAlertSettingsLookup settings, DailyVolumeQuery query)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public void ResetLastEvalDate()
    {
        Interlocked.Exchange(ref _lastEvalDayUnixMs, 0L);
    }

    /// <summary>
    /// Same shape as <see cref="ResetLastEvalDate"/>: clearing the date
    /// gate lets the next Evaluate() re-walk the repository. The rule
    /// has no other internal state to reset.
    /// </summary>
    public void ForgetAll() => ResetLastEvalDate();

    public IEnumerable<(RaiseRequest Request, string Detail)> Evaluate(FlushAlertEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var todayUtcMs = BucketAligner.AlignToDay(evt.FlushTimeUnixMs);
        if (todayUtcMs == _lastEvalDayUnixMs)
        {
            yield break;
        }
        _lastEvalDayUnixMs = todayUtcMs;

        var yesterdayMs = todayUtcMs - BucketAligner.DayMs;
        var baselineStartMs = yesterdayMs - (BaselineDays * BucketAligner.DayMs);
        var rows = _query(baselineStartMs, todayUtcMs);
        if (rows.Count == 0) yield break;

        // Group by app. Within each group: separate yesterday's total
        // from the prior-14 baseline. Skip when baseline has < 14 days
        // of data (warmup or app that didn't talk every day).
        var byApp = new Dictionary<int, List<DailyVolumeRow>>();
        foreach (var row in rows)
        {
            if (!byApp.TryGetValue(row.AppId, out var list))
            {
                list = new List<DailyVolumeRow>();
                byApp[row.AppId] = list;
            }
            list.Add(row);
        }

        var k = _settings.UnusualDailyVolumeKTimesTen / 10.0;
        if (k <= 0) yield break;

        foreach (var (appId, daily) in byApp)
        {
            DailyVolumeRow? yesterday = null;
            var baselineBytes = new List<long>();
            foreach (var d in daily)
            {
                if (d.DayUnixMs == yesterdayMs)
                {
                    yesterday = d;
                }
                else if (d.DayUnixMs < yesterdayMs)
                {
                    baselineBytes.Add(d.BytesUp + d.BytesDown);
                }
            }
            if (yesterday is null) continue;
            if (baselineBytes.Count < BaselineDays) continue;

            var median = Median(baselineBytes);
            var yesterdayTotal = yesterday.BytesUp + yesterday.BytesDown;
            var threshold = (long)(k * median);

            // Predicate clears: yesterday's total meets the multiplier
            // bar AND the absolute delta over median is substantial.
            if (yesterdayTotal < threshold) continue;
            if ((yesterdayTotal - median) < FloorBytes) continue;

            var req = new RaiseRequest(
                Type:          AlertType.UnusualDailyVolume,
                Severity:      NotableSeverity.Warning,
                SourceMonitor: SourceMonitor.Rollup,
                EntityKind:    AlertEntityKind.App,
                EntityRef:     appId.ToString(CultureInfo.InvariantCulture),
                AppId:         appId,
                Title:         $"Unusual daily traffic: {yesterday.ImageName}");

            yield return (req, RenderDetail(yesterday, median, yesterdayTotal, k));
        }
    }

    private static long Median(List<long> values)
    {
        values.Sort();
        var count = values.Count;
        if (count == 0) return 0;
        if ((count & 1) == 1)
        {
            return values[count / 2];
        }
        var a = values[(count / 2) - 1];
        var b = values[count / 2];
        return (a + b) / 2;
    }

    private static string RenderDetail(DailyVolumeRow yesterday, long median, long yesterdayTotal, double k)
    {
        var ratio = median == 0 ? double.PositiveInfinity : (double)yesterdayTotal / median;
        var ratioPhrase = double.IsInfinity(ratio)
            ? "vs ~0 bytes typical (baseline median was near zero)"
            : $"vs {FormatBytes(median)} typical (over {ratio:0.0}x baseline; threshold is {k:0.0}x)";
        var dayLocal = DateTimeOffset.FromUnixTimeMilliseconds(yesterday.DayUnixMs)
                                     .LocalDateTime
                                     .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return
            $"{yesterday.ImageName} used {FormatBytes(yesterdayTotal)} on {dayLocal}, " +
            $"{ratioPhrase}. " +
            $"Image path: {yesterday.ImagePath}. " +
            $"Baseline derived from the prior {BaselineDays} days of traffic.";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
    }
}

/// <summary>
/// Sub-interface for per-flush rules that participate in the
/// "RunRollupRulesNow" QA path. The debug IPC handler calls
/// <see cref="AlertProducer.EvaluateRollupRulesNow"/>, which iterates
/// per-flush rules and calls <see cref="ResetLastEvalDate"/> on any
/// that implement this. The producer then synthesizes a flush event
/// for the rule to re-evaluate against.
/// </summary>
public interface IResettableRollupRule
{
    /// <summary>
    /// Reset the rule's date-roll gate so the next <c>Evaluate</c> call
    /// runs the predicate again.
    /// </summary>
    void ResetLastEvalDate();
}

/// <summary>
/// One app's bytes for one UTC-aligned day, as returned by the
/// repository query injected into <see cref="UnusualDailyVolumeRule"/>.
/// </summary>
public sealed record DailyVolumeRow(
    int AppId,
    string ImageName,
    string ImagePath,
    long DayUnixMs,
    long BytesUp,
    long BytesDown);
