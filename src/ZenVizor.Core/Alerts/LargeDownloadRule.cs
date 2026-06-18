using System.Globalization;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Fires when a single connection accumulates a large download
/// (<see cref="IAlertSettingsLookup.LargeDownloadMb"/> MB by default 50)
/// within a short window
/// (<see cref="WindowMs"/> default 60 s of the connection's first-seen).
/// Severity Info per catalog §1.4 — informational ("notable download
/// happened"), not alarming. Source <see cref="SourceMonitor.Capture"/>.
/// <para>
/// Entity = App. Cooldown 24 h. A heavy-download app (cloud sync,
/// Steam, OS update) gets one alert per day even when it pulls dozens
/// of qualifying downloads. The detail enumerates contributing PIDs +
/// remote addresses observed during the alert's lifetime.
/// </para>
/// <para>
/// Stateful: tracks lifetime bytes_down per (session, remote, port)
/// in <see cref="_perConnection"/>. The connection's first_seen is
/// the gate timestamp; once a connection has been alerted-on, it's
/// recorded in <see cref="_alertedConnections"/> so the count doesn't
/// double-fire on subsequent flushes for the same download.
/// </para>
/// </summary>
public sealed class LargeDownloadRule : IFlushAlertRule
{
    /// <summary>Sliding window — connection must hit the threshold within this many ms of first_seen.</summary>
    public static readonly long WindowMs = (long)TimeSpan.FromSeconds(60).TotalMilliseconds;

    /// <summary>24h cooldown — same calendar day suppresses re-fire per app.</summary>
    public long CooldownMs => TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerMillisecond;

    private readonly IAlertSettingsLookup _settings;

    // Per-connection cumulative bytes_down + first_seen + observed PIDs/remotes
    // for an alert's detail string. Keyed by (sessionId, remoteAddress, remotePort)
    // so a single browser tab streaming from one CDN endpoint doesn't merge
    // with a separate connection to the same host.
    private readonly Dictionary<ConnectionKey, ConnectionState> _perConnection = new();
    private readonly HashSet<ConnectionKey> _alertedConnections = new();

    // Per-app alert detail aggregation. When the rule has already raised
    // for app_id N in this process, additional qualifying connections
    // for the same app get folded into the detail string here.
    private readonly Dictionary<int, AppAlertState> _appState = new();

    public LargeDownloadRule(IAlertSettingsLookup settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IEnumerable<(RaiseRequest Request, string Detail)> Evaluate(FlushAlertEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var thresholdBytes = (long)_settings.LargeDownloadMb * 1024L * 1024L;
        if (thresholdBytes <= 0) yield break;

        foreach (var conn in evt.Connections)
        {
            var key = new ConnectionKey(conn.SessionId, conn.RemoteAddress, conn.RemotePort);

            // Accumulate per-flush delta into per-connection lifetime.
            if (!_perConnection.TryGetValue(key, out var state))
            {
                state = new ConnectionState
                {
                    FirstSeenUnixMs = conn.FirstSeenUnixMs,
                    AppId = conn.AppId,
                    ImageName = conn.App.ImageName,
                    Pid = conn.Pid,
                };
                _perConnection[key] = state;
            }
            state.BytesDown += conn.BytesDownDelta;
            state.LastSeenUnixMs = conn.LastSeenUnixMs;

            // Already alerted for this exact connection — skip the
            // qualifying check; the alert's detail will roll forward on
            // the next flush via the per-app state.
            if (_alertedConnections.Contains(key)) continue;

            // Gate: cumulative bytes_down ≥ threshold AND landed within
            // the rule's window from first-seen.
            var ageMs = conn.LastSeenUnixMs - state.FirstSeenUnixMs;
            if (state.BytesDown < thresholdBytes) continue;
            if (ageMs > WindowMs) continue;

            _alertedConnections.Add(key);

            // Resolve / create the app-level alert state.
            if (!_appState.TryGetValue(conn.AppId, out var appState))
            {
                appState = new AppAlertState
                {
                    ImageName = conn.App.ImageName,
                    ImagePath = conn.App.ImagePath,
                };
                _appState[conn.AppId] = appState;
            }
            appState.QualifyingConnections.Add((conn.RemoteAddress, conn.RemotePort, state.BytesDown, conn.Pid));

            var req = new RaiseRequest(
                Type:          AlertType.LargeDownload,
                Severity:      NotableSeverity.Info,
                SourceMonitor: SourceMonitor.Capture,
                EntityKind:    AlertEntityKind.App,
                EntityRef:     conn.AppId.ToString(CultureInfo.InvariantCulture),
                AppId:         conn.AppId,
                Title:         $"Large download by {conn.App.ImageName}");

            yield return (req, RenderDetail(appState));
        }

        // TTL-prune stale connection rows. Anything not touched in 5
        // minutes is dropped — a download is long over by then and we
        // don't want unbounded memory growth on long-running services.
        var cutoff = evt.FlushTimeUnixMs - (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        var stale = new List<ConnectionKey>();
        foreach (var (k, s) in _perConnection)
        {
            if (s.LastSeenUnixMs < cutoff) stale.Add(k);
        }
        foreach (var k in stale)
        {
            _perConnection.Remove(k);
            _alertedConnections.Remove(k);
        }
    }

    private static string RenderDetail(AppAlertState appState)
    {
        // Bytes phrase: pick the largest qualifying connection for the
        // headline; enumerate the rest in the trailing summary.
        long maxBytes = 0;
        string maxRemote = "";
        foreach (var (remote, port, bytes, _) in appState.QualifyingConnections)
        {
            if (bytes > maxBytes)
            {
                maxBytes = bytes;
                maxRemote = $"{remote}:{port}";
            }
        }

        var distinctPids = new HashSet<int>();
        foreach (var (_, _, _, pid) in appState.QualifyingConnections)
        {
            distinctPids.Add(pid);
        }

        var pidPhrase = distinctPids.Count == 1
            ? $"PID {distinctPids.First()}"
            : "PIDs " + string.Join(", ", distinctPids.OrderBy(p => p));

        return
            $"{appState.ImageName} pulled {FormatBytes(maxBytes)} from {maxRemote} in under 60 seconds. " +
            $"Image path: {appState.ImagePath}. " +
            $"Total qualifying downloads: {appState.QualifyingConnections.Count} ({pidPhrase}).";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
    }

    private readonly record struct ConnectionKey(int SessionId, string RemoteAddress, int RemotePort);

    private sealed class ConnectionState
    {
        public int AppId;
        public string ImageName = "";
        public int Pid;
        public long BytesDown;
        public long FirstSeenUnixMs;
        public long LastSeenUnixMs;
    }

    private sealed class AppAlertState
    {
        public string ImageName = "";
        public string ImagePath = "";
        public List<(string Remote, int Port, long Bytes, int Pid)> QualifyingConnections { get; } = new();
    }
}
