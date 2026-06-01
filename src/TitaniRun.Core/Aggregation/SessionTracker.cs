using TitaniRun.Core.Attribution;
using TitaniRun.Core.Storage;

namespace TitaniRun.Core.Aggregation;

/// <summary>
/// Pure in-memory state for PID → session attribution. Hot path performs ZERO
/// disk I/O; lifecycle writes (open/close) are deferred to the flush tick where
/// they're batched into the single <see cref="IFlushSink"/> transaction.
/// </summary>
/// <remarks>
/// Sprint Plan Phase 1 responsibilities preserved:
/// <list type="bullet">
///   <item>First observation of a PID: resolve image, queue a pending session open.</item>
///   <item>PID reuse (start-time mismatch): queue close of old, queue open of new.</item>
///   <item>Stale process exit: <see cref="CollectStaleSessionIds"/> drains validated stale sessions.</item>
///   <item>PID 0 skipped; PID 4 (System) accepted via the resolver.</item>
/// </list>
/// </remarks>
public sealed class SessionTracker
{
    public const int IdlePid = 0;
    public const long DefaultStaleThresholdMs = 30_000;

    private readonly IProcessImageResolver _imageResolver;

    private readonly Dictionary<int, SessionState> _byPid = new();

    public SessionTracker(
        IProcessImageResolver imageResolver,
        long staleThresholdMs = DefaultStaleThresholdMs)
    {
        _imageResolver = imageResolver ?? throw new ArgumentNullException(nameof(imageResolver));
        StaleThresholdMs = staleThresholdMs;
    }

    public long StaleThresholdMs { get; }

    /// <summary>
    /// Hot path. Adopts the PID into the tracker (or refreshes its last-observed
    /// timestamp). Returns <c>false</c> if the PID is untrackable (Idle process,
    /// or the resolver has no image info — phantom PID).
    /// </summary>
    public bool TryTrack(int pid, long nowUnixMs)
    {
        if (pid == IdlePid)
        {
            return false;
        }

        if (_byPid.TryGetValue(pid, out var existing))
        {
            // PID reuse: same PID, different start time → close old, open new.
            var image = _imageResolver.Resolve(pid);
            if (image is null)
            {
                // Process disappeared mid-session; keep state but stop touching it,
                // stale reap will collect it shortly.
                return true;
            }
            if (existing.StartTimeUnixMs != image.StartTimeUnixMs)
            {
                if (existing.IsPersisted)
                {
                    // Queue the old session for close. If it was still pending
                    // (no session_id yet), it just gets dropped — its traffic for
                    // this window was already attributed to the pending session.
                    _explicitCloses.Add(existing.SessionId);
                }
                _byPid[pid] = new SessionState(
                    appIdentity: ToAppIdentity(image),
                    startTimeUnixMs: image.StartTimeUnixMs,
                    lastObservedUnixMs: nowUnixMs);
                return true;
            }

            existing.LastObservedUnixMs = nowUnixMs;
            return true;
        }

        var freshImage = _imageResolver.Resolve(pid);
        if (freshImage is null)
        {
            return false;
        }

        _byPid[pid] = new SessionState(
            appIdentity: ToAppIdentity(freshImage),
            startTimeUnixMs: freshImage.StartTimeUnixMs,
            lastObservedUnixMs: nowUnixMs);
        return true;
    }

    /// <summary>
    /// Returns the persisted <c>session_id</c> for <paramref name="pid"/> if one
    /// has been committed to disk. PIDs whose session is still pending (queued
    /// for the upcoming flush) return false.
    /// </summary>
    public bool TryGetSessionId(int pid, out int sessionId)
    {
        if (_byPid.TryGetValue(pid, out var state) && state.IsPersisted)
        {
            sessionId = state.SessionId;
            return true;
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
                HostedServices: null));
        }
        return pending;
    }

    /// <summary>
    /// Snapshot of already-persisted (pid → session_id) for the aggregator to
    /// pass through the FlushBatch — the sink uses it to resolve sample/connection PIDs.
    /// </summary>
    public IReadOnlyDictionary<int, int> SnapshotPersistedSessions()
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

    /// <summary>Number of currently tracked PIDs (test diagnostic).</summary>
    internal int TrackedCount => _byPid.Count;

    private readonly List<int> _explicitCloses = new();

    private static AppIdentity ToAppIdentity(ProcessImageInfo image) =>
        new(
            ImagePath: image.ImagePath,
            ImageName: image.ImageName,
            Publisher: null,
            SignatureStatus: "Unchecked",
            IsUserWritablePath: false);

    private sealed class SessionState
    {
        public SessionState(AppIdentity appIdentity, long startTimeUnixMs, long lastObservedUnixMs)
        {
            AppIdentity = appIdentity;
            StartTimeUnixMs = startTimeUnixMs;
            LastObservedUnixMs = lastObservedUnixMs;
            IsPersisted = false;
            SessionId = 0;
        }

        public AppIdentity AppIdentity { get; }
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
