// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Fires when an app's outbound bytes dominate inbound by at least
/// <see cref="Ratio"/>:1 over a <see cref="WindowMs"/> rolling window
/// AND total outbound clears the
/// <see cref="IAlertSettingsLookup.OutboundHeavyFloorMb"/> floor.
/// Severity Warning per catalog §1.4. Source <see cref="SourceMonitor.Capture"/>.
/// <para>
/// Entity = App. Cooldown 24h. The user-facing signal is "this app
/// uploaded a lot more than it downloaded recently" — useful for
/// catching backup tools dumping large datasets, exfil-like patterns,
/// or chatty telemetry clients. The catalog locks the ratio at 3:1
/// and the window at 15 min; the floor is user-tunable so a noisy
/// cloud sync (10 MB threshold default) can be quieted to 100 MB.
/// </para>
/// <para>
/// Stateful: maintains a per-app sliding 15-min window of (up, down)
/// byte totals built from <see cref="FlushAlertEvent"/> deltas. Each
/// flush appends a new bucket and evicts buckets older than the
/// window. Multi-PID enrichment in the detail string: when several
/// PIDs share the app name, the rendered string enumerates per-PID
/// contributions so a multi-process app can't hide behind one badge.
/// </para>
/// </summary>
public sealed class OutboundHeavyRule : IFlushAlertRule
{
    /// <summary>Outbound/inbound ratio threshold — locked at 3:1.</summary>
    public const double Ratio = 3.0;

    /// <summary>Rolling window — 15 min.</summary>
    public static readonly long WindowMs = (long)TimeSpan.FromMinutes(15).TotalMilliseconds;

    /// <summary>24h cooldown.</summary>
    public long CooldownMs => TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerMillisecond;

    private readonly IAlertSettingsLookup _settings;

    // Per-app rolling-window state. Bucket entries hold flush-time +
    // up/down deltas; each Evaluate() trims buckets older than WindowMs
    // before evaluating the predicate.
    private readonly Dictionary<int, AppWindow> _windows = new();

    // Apps already raised within this process so the same Evaluate()
    // cycle can't yield two requests for the same app (producer would
    // dedupe anyway, but the rule shouldn't bother).
    private readonly HashSet<int> _alertedApps = new();

    public OutboundHeavyRule(IAlertSettingsLookup settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Resets all rule-internal state when alerts history is wiped. Without
    /// this, a previously-raised app stays in <see cref="_alertedApps"/>
    /// and the rule silently no-ops on subsequent qualifying flushes for
    /// the same app — Reset history would quietly disable the rule for the
    /// rest of the process lifetime.
    /// </summary>
    public void ForgetAll()
    {
        _windows.Clear();
        _alertedApps.Clear();
    }

    public IEnumerable<(RaiseRequest Request, string Detail)> Evaluate(FlushAlertEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var floorBytes = (long)_settings.OutboundHeavyFloorMb * 1024L * 1024L;
        if (floorBytes <= 0) yield break;

        // Append this flush's deltas to each contributing app's window.
        foreach (var conn in evt.Connections)
        {
            if (conn.BytesUpDelta == 0 && conn.BytesDownDelta == 0) continue;
            if (!_windows.TryGetValue(conn.AppId, out var win))
            {
                win = new AppWindow
                {
                    ImageName = conn.App.ImageName,
                    ImagePath = conn.App.ImagePath,
                };
                _windows[conn.AppId] = win;
            }
            win.ImageName = conn.App.ImageName;
            win.ImagePath = conn.App.ImagePath;
            win.Buckets.Add(new Bucket(
                FlushTimeUnixMs: evt.FlushTimeUnixMs,
                Pid:             conn.Pid,
                BytesUp:         conn.BytesUpDelta,
                BytesDown:       conn.BytesDownDelta));
        }

        // Trim, evaluate, yield. Walk a snapshot of keys so we can
        // safely mutate _windows entries during iteration.
        var cutoff = evt.FlushTimeUnixMs - WindowMs;
        foreach (var (appId, win) in _windows.ToList())
        {
            win.Buckets.RemoveAll(b => b.FlushTimeUnixMs < cutoff);

            // Prune empty windows so the dictionary doesn't grow.
            if (win.Buckets.Count == 0)
            {
                _windows.Remove(appId);
                continue;
            }

            if (_alertedApps.Contains(appId)) continue;

            long totalUp = 0, totalDown = 0;
            foreach (var b in win.Buckets)
            {
                totalUp += b.BytesUp;
                totalDown += b.BytesDown;
            }

            if (totalUp < floorBytes) continue;

            // Avoid divide-by-zero: when downloads are zero, the ratio
            // is effectively infinite and the predicate clears as long
            // as the floor does.
            var clears = totalDown == 0
                ? true
                : ((double)totalUp / totalDown) >= Ratio;
            if (!clears) continue;

            _alertedApps.Add(appId);

            var perPid = AggregatePerPid(win.Buckets);
            var req = new RaiseRequest(
                Type:          AlertType.OutboundHeavy,
                Severity:      NotableSeverity.Warning,
                SourceMonitor: SourceMonitor.Capture,
                EntityKind:    AlertEntityKind.App,
                EntityRef:     appId.ToString(CultureInfo.InvariantCulture),
                AppId:         appId,
                Title:         $"Outbound-heavy app: {win.ImageName}");

            yield return (req, RenderDetail(win.ImageName, win.ImagePath, totalUp, totalDown, perPid));
        }
    }

    private static List<(int Pid, long BytesUp)> AggregatePerPid(List<Bucket> buckets)
    {
        var byPid = new Dictionary<int, long>();
        foreach (var b in buckets)
        {
            if (b.BytesUp == 0) continue;
            byPid.TryGetValue(b.Pid, out var existing);
            byPid[b.Pid] = existing + b.BytesUp;
        }
        return byPid.Select(p => (Pid: p.Key, BytesUp: p.Value))
                    .OrderByDescending(p => p.BytesUp)
                    .ToList();
    }

    private static string RenderDetail(
        string imageName, string imagePath,
        long totalUp, long totalDown,
        List<(int Pid, long BytesUp)> perPid)
    {
        var ratioPhrase = totalDown == 0
            ? "vs 0 B downloaded (no inbound traffic in window)"
            : $"vs {FormatBytes(totalDown)} downloaded (ratio {(double)totalUp / totalDown:0.0}x)";

        string pidPhrase;
        if (perPid.Count == 1)
        {
            pidPhrase = $"PID {perPid[0].Pid}";
        }
        else
        {
            pidPhrase = "PIDs " + string.Join(", ",
                perPid.Select(p => $"{p.Pid} ({FormatBytes(p.BytesUp)})"));
        }

        return
            $"{imageName} uploaded {FormatBytes(totalUp)} in the last 15 minutes, " +
            $"{ratioPhrase}. " +
            $"Image path: {imagePath}. " +
            $"Observed across {pidPhrase}.";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
    }

    private sealed class AppWindow
    {
        public string ImageName = "";
        public string ImagePath = "";
        public List<Bucket> Buckets { get; } = new();
    }

    private readonly record struct Bucket(long FlushTimeUnixMs, int Pid, long BytesUp, long BytesDown);
}
