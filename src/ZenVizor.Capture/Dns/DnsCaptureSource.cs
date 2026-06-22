// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Dns;

namespace ZenVizor.Capture.Dns;

/// <summary>
/// Phase 8 — passive DNS observer. Subscribes to the
/// <c>Microsoft-Windows-DNS-Client</c> ETW provider (event 3008,
/// <i>DNS query completed</i>); for each successful query, maps the parsed
/// payload to (IP → QNAME) tuples and writes them to the supplied
/// <see cref="DnsResolutionStore"/>. The aggregator reads from the same
/// store at flush time to stamp <c>connections.resolved_host</c>.
/// <para>
/// Strictly observational. Originates ZERO network traffic of its own —
/// CLAUDE.md invariant #1. The provider feeds us records the host's
/// resolver was going to return regardless of whether we were listening.
/// </para>
/// <para>
/// Owns its own <see cref="TraceEventSession"/> sibling to the kernel
/// network session held by <see cref="EtwCaptureSource"/>. Two sessions
/// is the deliberate choice (see Phase 8 design decision D5 in
/// <c>docs/zenvizor-sprint-plan.md</c>) — a fault in DNS observation does
/// not affect the load-bearing kernel-network capture path.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DnsCaptureSource : IAsyncDisposable
{
    public const string DefaultSessionName = "ZenVizor.Capture.Dns";
    private const string DnsClientProviderName = "Microsoft-Windows-DNS-Client";
    private const int EventIdQueryCompleted = 3008;

    /// <summary>
    /// TTL stamped on every entry derived from event 3008. The provider's
    /// payload does not include the DNS response TTL — only the resolved
    /// IPs — so we substitute a fixed default. 5 minutes is short enough
    /// that stale CDN flips age out within a flush cycle's worth of new
    /// connections, long enough that we don't re-record on every flush.
    /// </summary>
    public const int DefaultTtlSeconds = 300;

    /// <summary>
    /// Cadence at which <see cref="DnsResolutionStore.EvictExpired"/> runs
    /// on the background eviction thread. Independent of the production
    /// flush interval; lookups already skip expired entries, so the store
    /// can carry stale entries past their TTL until this interval reclaims
    /// the slots.
    /// </summary>
    public static readonly TimeSpan EvictTickInterval = TimeSpan.FromSeconds(60);

    private readonly DnsResolutionStore _store;
    private readonly string _sessionName;
    private readonly ILogger _logger;
    private readonly Func<long> _nowProvider;

    private TraceEventSession? _session;
    private Thread? _processThread;
    private CancellationTokenSource? _evictCts;
    private Task? _evictLoop;
    private volatile bool _shutdownRequested;
    private volatile bool _isFaulted;
    private long _eventsObserved;
    private long _eventsIgnored;

    public DnsCaptureSource(
        DnsResolutionStore store,
        string? sessionName = null,
        ILogger<DnsCaptureSource>? logger = null,
        Func<long>? nowProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionName = sessionName ?? DefaultSessionName;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool IsFaulted => _isFaulted;
    public long EventsObserved => Interlocked.Read(ref _eventsObserved);
    public long EventsIgnored  => Interlocked.Read(ref _eventsIgnored);

    public void Start()
    {
        if (_session is not null) return;

        TryStopLeakedSession(_sessionName, _logger);

        _shutdownRequested = false;
        _isFaulted = false;

        _session = new TraceEventSession(_sessionName) { StopOnDispose = true };
        _session.EnableProvider(DnsClientProviderName, TraceEventLevel.Informational);
        _session.Source.Dynamic.All += OnDynamicEvent;

        _processThread = new Thread(ProcessLoop)
        {
            IsBackground = true,
            Name = "ZenVizor.DnsCapture",
        };
        _processThread.Start();

        _evictCts = new CancellationTokenSource();
        _evictLoop = Task.Run(() => EvictLoopAsync(_evictCts.Token));

        _logger.LogInformation(
            "DNS capture session '{Session}' started (provider '{Provider}').",
            _sessionName, DnsClientProviderName);
    }

    private void ProcessLoop()
    {
        try
        {
            _session?.Source.Process();
            if (!_shutdownRequested)
            {
                _isFaulted = true;
                _logger.LogError(
                    "DNS ETW Process loop exited unexpectedly without a shutdown request — DNS capture is now dead.");
            }
        }
        catch (Exception ex)
        {
            _isFaulted = true;
            _logger.LogError(ex, "DNS ETW Process loop terminated unexpectedly.");
        }
    }

    private async Task EvictLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(EvictTickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var dropped = _store.EvictExpired(_nowProvider());
                    if (dropped > 0)
                    {
                        _logger.LogDebug("DNS store evicted {Dropped} expired entries.", dropped);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DNS store eviction tick failed.");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnDynamicEvent(TraceEvent data)
    {
        if (!string.Equals(data.ProviderName, DnsClientProviderName, StringComparison.Ordinal))
        {
            return;
        }
        if ((int)data.ID != EventIdQueryCompleted)
        {
            return;
        }
        Interlocked.Increment(ref _eventsObserved);

        try
        {
            var queryName    = data.PayloadByName("QueryName")    as string ?? string.Empty;
            var queryResults = data.PayloadByName("QueryResults") as string ?? string.Empty;
            var statusBox    = data.PayloadByName("QueryStatus");
            var queryStatus  = statusBox is null ? 0 : Convert.ToInt32(statusBox);

            Ingest(queryName, queryResults, queryStatus, _nowProvider());
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _eventsIgnored);
            _logger.LogDebug(ex, "DNS Client event mapping failed.");
        }
    }

    /// <summary>
    /// Test seam. Production path lands here after the ETW callback has
    /// extracted the named payload fields; unit tests bypass ETW by calling
    /// directly with canned payloads. Increments <see cref="EventsIgnored"/>
    /// for non-success status codes and unparseable payloads so the
    /// diagnostic counters stay accurate either way.
    /// </summary>
    internal void Ingest(string queryName, string queryResults, int queryStatus, long observedAtUnixMs)
    {
        if (queryStatus != 0)
        {
            Interlocked.Increment(ref _eventsIgnored);
            return;
        }
        var answers = DnsClientEventMapper.Map(queryName, queryResults, DefaultTtlSeconds);
        if (answers.Count == 0)
        {
            Interlocked.Increment(ref _eventsIgnored);
            return;
        }
        foreach (var answer in answers)
        {
            _store.Record(answer.Ip, answer.Hostname, answer.TtlSeconds, observedAtUnixMs);
        }
    }

    private static void TryStopLeakedSession(string sessionName, ILogger logger)
    {
        try
        {
            using var leak = TraceEventSession.GetActiveSession(sessionName);
            if (leak is not null)
            {
                logger.LogWarning(
                    "Found pre-existing ETW session '{Session}' from a prior run; stopping it.",
                    sessionName);
                leak.Stop();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Defensive ETW session cleanup failed (non-fatal).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is null) return;
        _shutdownRequested = true;

        try { _session.Dispose(); } catch { /* best-effort */ }

        if (_evictCts is not null)
        {
            _evictCts.Cancel();
            if (_evictLoop is not null)
            {
                try { await _evictLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _evictCts.Dispose();
            _evictCts = null;
            _evictLoop = null;
        }

        if (_processThread is not null)
        {
            try
            {
                await Task.Run(() => _processThread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }

        _session = null;
        _processThread = null;
    }
}
