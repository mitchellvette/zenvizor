// SPDX-License-Identifier: GPL-3.0-or-later

using ZenVizor.Core.Attribution;
using ZenVizor.Core.Storage;

namespace ZenVizor.Core.Aggregation;

/// <summary>
/// Pure in-memory state for PID → session attribution. Hot path performs ZERO
/// disk I/O; lifecycle writes (open/close) are deferred to the flush tick where
/// they're batched into the single <see cref="IFlushSink"/> transaction.
/// </summary>
/// <remarks>
/// <para>
/// Sprint Plan Phase 1 responsibilities preserved:
/// <list type="bullet">
///   <item>First observation of a PID: resolve image, queue a pending session open.</item>
///   <item>PID reuse (start-time mismatch): queue close of old, queue open of new.</item>
///   <item>Stale process exit: <see cref="CollectStaleSessionIds"/> drains validated stale sessions.</item>
///   <item>PID 0 skipped; PID 4 (System) accepted via the resolver.</item>
/// </list>
/// </para>
/// <para>
/// Threading: this class owns its own <c>_gate</c> so the caller
/// (<see cref="TrafficAggregator.Observe"/>) does NOT hold its accumulator
/// lock while session-open enrichment (WinVerifyTrust + FileInfo + SCM
/// enumeration) runs. <see cref="TryTrack"/>'s slow path releases the
/// internal lock for the duration of the enrichment work, then re-enters
/// to commit; concurrent <see cref="TryTrack"/> calls for the same PID
/// race-resolve via a double-check on the second acquisition.
/// </para>
/// </remarks>
public sealed class SessionTracker
{
    public const int IdlePid = 0;
    public const long DefaultStaleThresholdMs = 30_000;

    private readonly IProcessImageResolver _imageResolver;
    private readonly IAppEnricher _appEnricher;
    private readonly IServiceHostResolver _serviceHostResolver;

    private readonly Dictionary<int, SessionState> _byPid = new();
    private readonly List<int> _explicitCloses = new();
    private readonly object _gate = new();

    public SessionTracker(
        IProcessImageResolver imageResolver,
        long staleThresholdMs = DefaultStaleThresholdMs)
        : this(imageResolver, NoOpAppEnricher.Instance, NoOpServiceHostResolver.Instance, staleThresholdMs)
    {
    }

    public SessionTracker(
        IProcessImageResolver imageResolver,
        IAppEnricher appEnricher,
        IServiceHostResolver serviceHostResolver,
        long staleThresholdMs = DefaultStaleThresholdMs)
    {
        _imageResolver = imageResolver ?? throw new ArgumentNullException(nameof(imageResolver));
        _appEnricher = appEnricher ?? throw new ArgumentNullException(nameof(appEnricher));
        _serviceHostResolver = serviceHostResolver ?? throw new ArgumentNullException(nameof(serviceHostResolver));
        StaleThresholdMs = staleThresholdMs;
    }

    public long StaleThresholdMs { get; }

    /// <summary>
    /// Hot path. Adopts the PID into the tracker (or refreshes its last-observed
    /// timestamp). Returns <c>false</c> if the PID is untrackable (Idle process,
    /// or the resolver has no image info — phantom PID).
    /// <para>
    /// Fast path (existing PID, same start time): acquires the internal lock
    /// only long enough to bump <c>LastObservedUnixMs</c>. Slow path (new PID
    /// or PID reuse): releases the lock while running session-open enrichment
    /// (<see cref="IAppEnricher.Enrich"/> + <see cref="IServiceHostResolver"/>)
    /// so the per-event hot path doesn't block on file I/O or signature checks.
    /// </para>
    /// </summary>
    public bool TryTrack(int pid, long nowUnixMs)
    {
        if (pid == IdlePid)
        {
            return false;
        }

        ProcessImageInfo? newImage = null;
        bool isPidReuse = false;
        int reusedSessionIdToClose = 0;

        lock (_gate)
        {
            if (_byPid.TryGetValue(pid, out var existing))
            {
                var cached = _imageResolver.Resolve(pid);
                if (cached is null)
                {
                    // Process vanished mid-session; keep state, stale reap
                    // will collect it shortly.
                    existing.LastObservedUnixMs = nowUnixMs;
                    return true;
                }
                if (existing.StartTimeUnixMs == cached.StartTimeUnixMs)
                {
                    // Common fast path — same process, same PID, just bump.
                    existing.LastObservedUnixMs = nowUnixMs;
                    return true;
                }

                // PID reuse — different start time at the same PID. Queue the
                // old session's close (if persisted), drop the entry, and
                // proceed to the slow open path below. The enrichment work
                // for the new image runs OUTSIDE this lock.
                if (existing.IsPersisted)
                {
                    isPidReuse = true;
                    reusedSessionIdToClose = existing.SessionId;
                }
                _byPid.Remove(pid);
                newImage = cached;
            }
        }

        if (newImage is null)
        {
            // No entry yet — resolve the image now. Most lookups are the
            // lifecycle resolver's PID-keyed cache hit (cheap); Win32 fallback
            // can stat the process and is the reason we won't hold the lock.
            newImage = _imageResolver.Resolve(pid);
            if (newImage is null)
            {
                return false;
            }
        }

        // Heavy work runs without the lock. WinVerifyTrust + FileInfo (in
        // AppEnricher) plus the SCM service-host enumeration each take many
        // milliseconds on cold cache and used to block every aggregator
        // update.
        var enrichment = _appEnricher.Enrich(newImage);
        var hostedServices = FormatHostedServices(_serviceHostResolver.ResolveHostedServices(pid));

        lock (_gate)
        {
            if (isPidReuse)
            {
                _explicitCloses.Add(reusedSessionIdToClose);
            }

            if (_byPid.TryGetValue(pid, out var raceWinner))
            {
                // Another thread committed a session for this PID while we
                // were enriching. Adopt its entry — our enrichment work was
                // redundant but harmless.
                raceWinner.LastObservedUnixMs = nowUnixMs;
                return true;
            }

            _byPid[pid] = new SessionState(
                appIdentity: BuildAppIdentity(newImage, enrichment),
                hostedServices: hostedServices,
                startTimeUnixMs: newImage.StartTimeUnixMs,
                lastObservedUnixMs: nowUnixMs);
            return true;
        }
    }

    /// <summary>
    /// Returns the persisted <c>session_id</c> for <paramref name="pid"/> if one
    /// has been committed to disk. PIDs whose session is still pending (queued
    /// for the upcoming flush) return false.
    /// </summary>
    public bool TryGetSessionId(int pid, out int sessionId)
    {
        lock (_gate)
        {
            if (_byPid.TryGetValue(pid, out var state) && state.IsPersisted)
            {
                sessionId = state.SessionId;
                return true;
            }
        }

        sessionId = 0;
        return false;
    }

    /// <summary>
    /// Snapshot of sessions awaiting their first INSERT. Does NOT mutate state —
    /// the aggregator drives state-change atomicity via <see cref="OnFlushCommitted"/>.
    /// </summary>
    public IReadOnlyList<NewSessionEntry> CollectPendingOpens()
    {
        lock (_gate)
        {
            var pending = new List<NewSessionEntry>();
            foreach (var (pid, state) in _byPid)
            {
                if (state.IsPersisted)
                {
                    continue;
                }
                pending.Add(new NewSessionEntry(
                    Pid: pid,
                    App: state.AppIdentity,
                    StartTimeUnixMs: state.StartTimeUnixMs,
                    HostedServices: state.HostedServices));
            }
            return pending;
        }
    }

    /// <summary>
    /// Snapshot of <c>pid → (AppIdentity, HostedServices)</c> across ALL tracked
    /// PIDs (pending AND persisted). Used by the per-app activity rollup so a
    /// newly-tracked PID still has its first-window bytes attributed to its app
    /// before its session row hits SQLite.
    /// </summary>
    public IReadOnlyDictionary<int, PidAppInfo> SnapshotPidToApp()
    {
        lock (_gate)
        {
            var snapshot = new Dictionary<int, PidAppInfo>(_byPid.Count);
            foreach (var (pid, state) in _byPid)
            {
                snapshot[pid] = new PidAppInfo(state.AppIdentity, state.HostedServices);
            }
            return snapshot;
        }
    }

    /// <summary>
    /// Snapshot of already-persisted (pid → session_id) for the aggregator to
    /// pass through the FlushBatch — the sink uses it to resolve sample/connection PIDs.
    /// </summary>
    public IReadOnlyDictionary<int, int> SnapshotPersistedSessions()
    {
        lock (_gate)
        {
            var snapshot = new Dictionary<int, int>(_byPid.Count);
            foreach (var (pid, state) in _byPid)
            {
                if (state.IsPersisted)
                {
                    snapshot[pid] = state.SessionId;
                }
            }
            return snapshot;
        }
    }

    /// <summary>
    /// Returns persisted session ids to close. Two sources:
    /// <list type="bullet">
    ///   <item>Sessions marked <c>Closing</c> by PID reuse (already removed from map).</item>
    ///   <item>Sessions whose PID has not been observed for at least <see cref="StaleThresholdMs"/>
    ///         AND that the resolver no longer finds (or finds with a different start time).</item>
    /// </list>
    /// Does NOT mutate state — drainage happens in <see cref="OnFlushCommitted"/>.
    /// </summary>
    public IReadOnlyList<int> CollectStaleSessionIds(long nowUnixMs)
    {
        lock (_gate)
        {
            var stale = new List<int>(_explicitCloses);
            foreach (var (pid, state) in _byPid)
            {
                if (!state.IsPersisted)
                {
                    continue;
                }
                if (nowUnixMs - state.LastObservedUnixMs < StaleThresholdMs)
                {
                    continue;
                }

                var image = _imageResolver.Resolve(pid);
                if (image is not null && image.StartTimeUnixMs == state.StartTimeUnixMs)
                {
                    // Process is still alive, just idle — keep the session.
                    continue;
                }

                stale.Add(state.SessionId);
            }
            return stale;
        }
    }

    /// <summary>
    /// Apply the result of a successful flush:
    /// <list type="number">
    ///   <item>Mark each PID in <paramref name="pidToNewSessionId"/> as persisted at the given id.</item>
    ///   <item>Remove every PID whose session id appears in <paramref name="closedSessionIds"/>.</item>
    ///   <item>Clear the explicit-close queue.</item>
    /// </list>
    /// MUST be called only after the sink commits, so a failed flush leaves the
    /// tracker re-runnable on the next tick.
    /// </summary>
    public void OnFlushCommitted(
        IReadOnlyDictionary<int, int> pidToNewSessionId,
        IReadOnlyList<int> closedSessionIds)
    {
        lock (_gate)
        {
            foreach (var (pid, sessionId) in pidToNewSessionId)
            {
                if (_byPid.TryGetValue(pid, out var state))
                {
                    state.MarkPersisted(sessionId);
                }
            }

            var closedSet = new HashSet<int>(closedSessionIds);
            var pidsToDrop = new List<int>();
            foreach (var (pid, state) in _byPid)
            {
                if (state.IsPersisted && closedSet.Contains(state.SessionId))
                {
                    pidsToDrop.Add(pid);
                }
            }
            foreach (var pid in pidsToDrop)
            {
                _byPid.Remove(pid);
            }

            _explicitCloses.Clear();
        }
    }

    /// <summary>Number of currently tracked PIDs (test diagnostic).</summary>
    internal int TrackedCount
    {
        get { lock (_gate) return _byPid.Count; }
    }

    /// <summary>
    /// Drops every tracked PID and any pending explicit-close request.
    /// Called by the Settings "Reset history" flow on the service side
    /// (alongside <see cref="TrafficAggregator.ResetInMemoryState"/> and
    /// <see cref="Alerts.AlertProducer.ForgetAll"/>) so the next qualifying
    /// observation is treated as a brand-new session rather than a hit
    /// against the pre-wipe in-memory map (whose <c>SessionId</c> values
    /// point at <c>process_sessions</c> rows the wipe just deleted).
    /// Currently-running real processes (browser, terminal, etc.) will
    /// re-register the next time ETW or the IP-Helper poll observes them
    /// — same behaviour as a service restart but without the ETW-resubscribe
    /// cost.
    /// </summary>
    /// <returns>The number of tracked PIDs dropped; logged by the caller.</returns>
    public int ResetTrackerState()
    {
        lock (_gate)
        {
            var dropped = _byPid.Count;
            _byPid.Clear();
            _explicitCloses.Clear();
            return dropped;
        }
    }

    private static AppIdentity BuildAppIdentity(ProcessImageInfo image, EnrichmentResult enrichment) =>
        new(
            ImagePath: image.ImagePath,
            ImageName: image.ImageName,
            Publisher: enrichment.Publisher,
            SignatureStatus: enrichment.SignatureStatus,
            IsUserWritablePath: enrichment.IsUserWritablePath)
        {
            PathClass = enrichment.PathClass,
        };

    private static string? FormatHostedServices(IReadOnlyList<string>? services)
    {
        if (services is null || services.Count == 0)
        {
            return null;
        }
        return string.Join(',', services);
    }

    /// <summary>App-level projection of a tracked PID for the activity rollup.</summary>
    public readonly record struct PidAppInfo(AppIdentity AppIdentity, string? HostedServices);

    private sealed class SessionState
    {
        public SessionState(
            AppIdentity appIdentity,
            string? hostedServices,
            long startTimeUnixMs,
            long lastObservedUnixMs)
        {
            AppIdentity = appIdentity;
            HostedServices = hostedServices;
            StartTimeUnixMs = startTimeUnixMs;
            LastObservedUnixMs = lastObservedUnixMs;
            IsPersisted = false;
            SessionId = 0;
        }

        public AppIdentity AppIdentity { get; }
        public string? HostedServices { get; }
        public long StartTimeUnixMs { get; }
        public long LastObservedUnixMs { get; set; }
        public bool IsPersisted { get; private set; }
        public int SessionId { get; private set; }

        public void MarkPersisted(int sessionId)
        {
            IsPersisted = true;
            SessionId = sessionId;
        }
    }
}
