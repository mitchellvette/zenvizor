// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZenVizor.Attribution;
using ZenVizor.Attribution.Authenticode;
using ZenVizor.Attribution.IpHelper;
using ZenVizor.Attribution.Paths;
using ZenVizor.Attribution.Services;
using ZenVizor.Capture;
using ZenVizor.Capture.Dns;
using ZenVizor.Capture.Sni;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Dns;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ipc.Server;
using ZenVizor.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Service;

/// <summary>
/// The service entry point. Owns the full capture pipeline and the IPC server.
/// On start:
/// 1. Create + ACL the <c>%ProgramData%\ZenVizor\</c> data directory.
/// 2. Run the SQLite migrator against <c>zenvizor.db</c>.
/// 3. Build the capture pipeline: ETW + IP Helper + ProcessImageResolver + aggregator + sink.
/// 4. Start the capture monitor (ETW session + reader/flush loops).
/// 5. Start the named-pipe IPC server with a handler that knows about capture state.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ZenVizorHostedService : IHostedService
{
    // Defaults; later phases will source these from the settings table.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(5000);
    private static readonly TimeSpan RetentionPurgeInterval = TimeSpan.FromHours(24);
    private const long PidTablePollMs = 1000;

    // Phase 8.6 — passive SNI/QUIC/Host recovery. Feature toggle until a
    // settings-table row replaces it; default-on. When the PktMon control
    // surface is unavailable, capture transparently falls back to the
    // receive-only SIO_RCVALL raw-socket substrate (IPv4 only).
    private const bool SniCaptureEnabled = true;

    // Dev/QA only — when ZENVIZOR_DISABLE_CAPTURE is set to a truthy value the
    // service constructs the full pipeline (so IPC still serves the DB) but
    // starts NO capture: no ETW monitor, no DNS/SNI observers, no retention
    // purge, no enrichment backfill. Lets the marketing screenshot seed be
    // served verbatim without live capture polluting the deterministic byte
    // totals or the enrichment backfill rewriting the hand-seeded signer/path
    // attribution. Inert unless the env var is set; production never sets it.
    private const string DisableCaptureEnvVar = "ZENVIZOR_DISABLE_CAPTURE";
    private static readonly bool CaptureDisabled = IsEnvFlagSet(DisableCaptureEnvVar);

    private static bool IsEnvFlagSet(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value)
            && !value.Equals("0", StringComparison.Ordinal)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private readonly ILogger<ZenVizorHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private ZenVizorPipeServer? _pipeServer;
    private AlertBroadcaster? _alertBroadcaster;
    private CaptureMonitor? _captureMonitor;
    private EtwCaptureSource? _captureSource;
    private DnsCaptureSource? _dnsSource;
    private SniCaptureSource? _sniSource;
    private DnsResolutionStore? _dnsStore;
    private CancellationTokenSource? _retentionCts;
    private Task? _retentionLoop;
    private CancellationTokenSource? _backfillCts;
    private Task? _backfillTask;

    public ZenVizorHostedService(
        ILogger<ZenVizorHostedService> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dataDir = StorageConstants.DefaultDataDirectory;
        var dbPath = StorageConstants.DefaultDatabasePath;

        try
        {
            ProgramDataAcl.EnsureDirectoryWithAcl(dataDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex,
                "Could not fully ACL data directory {DataDir} — continuing with inherited perms.",
                dataDir);
        }

        var migrator = new Migrator(_loggerFactory.CreateLogger<Migrator>());
        var applied = migrator.Migrate(dbPath);
        if (applied.Count > 0)
        {
            _logger.LogInformation(
                "Applied {Count} migration(s) to {DbPath}: {Versions}",
                applied.Count, dbPath, string.Join(",", applied));
        }

        // ---- Capture pipeline ----
        var connections = new ConnectionFactory(dbPath);
        var flushSink = new SqliteFlushSink(connections);

        // Phase 6.7 — settings cache constructed UP FRONT so the alert
        // producer's Phase 6.7 per-flush rules (LargeDownload,
        // OutboundHeavy) can read user-tunable thresholds at evaluation
        // time. UpdateSettingsAsync on the IPC side calls
        // alertSettingsLookup.Refresh() so a UI write takes effect on
        // the next flush without a restart.
        var settingsRepoForAlerts = new SettingsRepository(connections);
        var alertSettingsLookup = new CachedAlertSettingsLookup(settingsRepoForAlerts);

        // Epic B (1.2.0) — install-epoch anchor for the first-run
        // baseline gate. Written once, on the first service start
        // after install, and never overwritten thereafter. Guard uses
        // GetString==null so upgrades from 1.1.x land the epoch at
        // upgrade-time (there was no key before this release); fresh
        // installs land it here too. Either way, the epoch is the
        // moment ZenVizor first ran on this box — the semantic anchor
        // for "the settling window starts now."
        long installEpochUnixMs;
        var epochRaw = settingsRepoForAlerts.GetString(SettingsRepository.Keys.BaselineInstallEpochMs);
        if (epochRaw is null)
        {
            installEpochUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 64-bit epoch persists as a string (settings.value is TEXT);
            // SettingsRepository.SetInt would truncate to Int32.
            settingsRepoForAlerts.Set(
                SettingsRepository.Keys.BaselineInstallEpochMs,
                installEpochUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _logger.LogInformation(
                "Baseline install epoch initialized: {Epoch} ms",
                installEpochUnixMs);
        }
        else if (!long.TryParse(epochRaw, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out installEpochUnixMs))
        {
            _logger.LogWarning(
                "Baseline install epoch key present but unparseable ({Raw}); " +
                "disabling baseline gate for this process.", epochRaw);
            installEpochUnixMs = 0;
        }

        // Epic B (1.2.0) — per-severity toast preference seeding.
        // Runs at every service start; skips work when the three keys
        // are already present. On a truly-fresh install (no keys and no
        // legacy master), lands the gating-phase default:
        // Critical=1, Warning=0, Info=0. On an upgrade from 1.1.x, honours
        // the legacy toast.on_alert value — a user with '1' had opted
        // into every-severity toasts and keeps them (all three seeded to
        // 1); a user with '0' had opted out and stays opted out (all
        // three seeded to 0). Legacy toast.on_alert stays in the table
        // so older UIs still read a coherent value.
        MigrateToastPreferencesIfNeeded(settingsRepoForAlerts, _logger);

        // ---- Phase 6 alert pipeline (must construct BEFORE the aggregator
        //      so we can hand the producer in as its IAlertEventSink). The
        //      sink → producer dependency chain stays inside the service:
        //      AlertsRepository (Storage) → AlertsRepositorySink (Service
        //      adapter implementing Core's IAlertSink) → AlertProducer
        //      (Core, rules-only logic). The producer also exposes the
        //      AlertRaised event that the broadcaster wires below. ----
        var alertsRepo  = new AlertsRepository(connections);
        var alertSink   = new AlertsRepositorySink(alertsRepo);

        // Phase 6.7 P4 — first-seen lookup cache. apps.first_seen is
        // write-once per row, so a per-app ConcurrentDictionary is
        // sufficient — no expiry, no refresh. Misses fall through to a
        // single SELECT against the repo; the result is cached for the
        // lifetime of the service process. Consumers: FirstRunWanTalkerRule.
        var appFirstSeenRepo = new AppFirstSeenRepository(connections);
        var firstSeenCache = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
        long FirstSeenLookup(int appId) =>
            firstSeenCache.GetOrAdd(appId, id => appFirstSeenRepo.GetFirstSeenUnixMs(id));

        // Phase 6.7 P4 — register Rules 1 + 2 (per-WAN-event) and
        // Rules 3 + 4 + 5 (per-flush) from the alert catalog. All six
        // producers wired with this slice.
        var alertRules  = new IAlertRule[]
        {
            new UnsignedFromUserPathRule(),
            new InvalidSignatureRule(),
            new FirstRunWanTalkerRule(installEpochUnixMs),
        };
        var dailyTrafficLookup = new DailyTrafficLookupRepository(connections);
        var flushAlertRules = new IFlushAlertRule[]
        {
            new LargeDownloadRule(alertSettingsLookup),
            new OutboundHeavyRule(alertSettingsLookup),
            new UnusualDailyVolumeRule(
                alertSettingsLookup,
                (from, to) => MapDailyTotals(dailyTrafficLookup.GetDailyTotals(from, to))),
        };
        var alertProducer = new AlertProducer(
            alertRules,
            alertSink,
            appFirstSeenLookup: FirstSeenLookup,
            flushRules:         flushAlertRules,
            logger:             _loggerFactory.CreateLogger<AlertProducer>());

        // ProcessLifecycleResolver is the Phase-3 fix for short-lived process
        // attribution: an ETW-fed image cache keyed by PID, populated at
        // process-start time and held past process-exit for a grace window so
        // trailing network events still resolve. Without this, sub-second
        // processes (fast curl, single-shot CLI tools) silently lost ALL
        // attribution because their image couldn't be looked up via Win32
        // after they exited.
        var imageResolver = new ProcessLifecycleResolver(
            logger: _loggerFactory.CreateLogger<ProcessLifecycleResolver>());
        imageResolver.PrimeFromRunningProcesses();

        // The IpHelper polling source covers two cases the ETW lifecycle
        // resolver does not: (a) connections that existed before ZenVizor
        // started — we never see their connect event — and (b) UDP, which
        // has no connect event. The ConnectionLifecycleResolver layers an
        // eager ETW-fed cache on top with grace-period retention so trailing
        // receive events for short-lived TCP connections still resolve.
        var ipHelperSource = new IpHelperPidTableSource(
            pollIntervalMs: PidTablePollMs,
            logger: _loggerFactory.CreateLogger<IpHelperPidTableSource>());
        var pidTableSource = new ConnectionLifecycleResolver(
            ipHelperSource,
            logger: _loggerFactory.CreateLogger<ConnectionLifecycleResolver>());

        // ---- Phase 2 enrichment ----
        var signatureVerifier = new WinVerifyTrustSignatureVerifier(
            _loggerFactory.CreateLogger<WinVerifyTrustSignatureVerifier>());
        var pathClassifier = new UserWritablePathClassifier();
        var appEnricher = new AppEnricher(
            signatureVerifier,
            pathClassifier,
            _loggerFactory.CreateLogger<AppEnricher>());
        var serviceHostResolver = new ScmServiceHostResolver(
            _loggerFactory.CreateLogger<ScmServiceHostResolver>());

        // One-shot enrichment of any pre-Phase-2 'Unchecked' apps rows.
        // Previously synchronous in front of capture startup; a large backlog
        // (a user upgrading from Phase 1 with many months of history) would
        // delay capture by tens of seconds. Now dispatched as a background
        // task AFTER capture starts. The race with new-session inserts is
        // bounded — backfill never INSERTs apps rows, only UPDATEs existing
        // ones, and SQLITE_CONSTRAINT on the (image_path, publisher) unique
        // index is caught per row.
        var backfill = new EnrichmentBackfill(
            connections,
            appEnricher,
            _loggerFactory.CreateLogger<EnrichmentBackfill>());

        var sessionTracker = new SessionTracker(imageResolver, appEnricher, serviceHostResolver);

        // Epic B (1.2.0) — running-process setup-scan seed. Runs
        // synchronously here, BEFORE capture starts, so that any
        // pre-existing app that opens a WAN connection in the first 60 s
        // of capture finds its apps row already stamped with
        // first_seen = install_epoch and the FirstRunWanTalker baseline
        // gate correctly rejects the false-positive raise.
        //
        // Idempotence: guarded by baseline.setup_scan_done. A second
        // service start is a no-op (< 1 ms).
        //
        // Also runs AFTER the install-epoch key is written above, so
        // installEpochUnixMs is guaranteed populated on a fresh install.
        // A parse failure that left installEpochUnixMs = 0 causes the
        // seeder to skip with a warning (see BaselineAppSeeder logic).
        var baselineSeeder = new BaselineAppSeeder(
            connections,
            appEnricher,
            _loggerFactory.CreateLogger<BaselineAppSeeder>());
        try
        {
            baselineSeeder.SeedIfNeeded(installEpochUnixMs);
        }
        catch (Exception ex)
        {
            // Seeder failure is NOT fatal. Worst case: the day-one
            // FirstRunWanTalker flood recurs for apps we couldn't seed
            // — a UX regression, not a data-integrity or safety
            // problem. Log loud and continue.
            _logger.LogWarning(ex,
                "BaselineAppSeeder threw ({Kind}: {Message}); day-one first-run gating may be less effective on this run.",
                ex.GetType().Name, ex.Message);
        }

        // Phase 8 — the DNS resolution store is constructed BEFORE the
        // aggregator so the aggregator can read from it at flush time. The
        // DnsCaptureSource (started further below, after CaptureMonitor) is
        // the producer. If DnsCaptureSource.Start() throws, the store
        // simply stays empty and every aggregator lookup misses — the rest
        // of the pipeline carries on with resolved_host = null.
        _dnsStore = new DnsResolutionStore();

        var aggregator = new TrafficAggregator(
            sessionTracker,
            new PidCorrector(),
            pidTableSource,
            flushSink,
            logger: _loggerFactory.CreateLogger<TrafficAggregator>(),
            alertEventSink: alertProducer,
            dnsStore: _dnsStore);

        _captureSource = new EtwCaptureSource(
            logger: _loggerFactory.CreateLogger<EtwCaptureSource>(),
            processSink: imageResolver,
            connectionSink: pidTableSource);
        _captureMonitor = new CaptureMonitor(
            _captureSource,
            aggregator,
            FlushInterval,
            _loggerFactory.CreateLogger<CaptureMonitor>());

        if (CaptureDisabled)
        {
            _logger.LogWarning(
                "{EnvVar} is set — running in dev capture-disabled mode. No ETW/DNS/SNI " +
                "capture, no retention purge, no enrichment backfill; the service serves the " +
                "existing database over IPC unchanged. NEVER use this against the production " +
                "data directory.",
                DisableCaptureEnvVar);
        }
        else
        {
            await _captureMonitor.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        // ---- Phase 8 — passive DNS observer (sibling capture path) ----
        // Sibling TraceEventSession listening to Microsoft-Windows-DNS-Client
        // event 3008; populates _dnsStore (constructed above and already
        // wired into the aggregator) so the aggregator's flush-time lookup
        // can stamp connections.resolved_host. Strictly observational —
        // invariant #1 (zero own traffic) still holds; the new provider
        // does not originate traffic, it surfaces what the host resolver
        // already does.
        //
        // DNS observation is an enrichment, not load-bearing. A failed start
        // logs loud and continues — capture still works, hostnames stay null
        // until the next service restart.
        _dnsSource = new DnsCaptureSource(
            _dnsStore,
            logger: _loggerFactory.CreateLogger<DnsCaptureSource>());
        if (!CaptureDisabled)
        {
            try
            {
                _dnsSource.Start();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DNS capture source failed to start; resolved_host will be null until next service start.");
            }
        }

        // ---- Phase 8.6 — passive SNI / QUIC-SNI / HTTP-Host observer ----
        // Sibling source feeding the SAME _dnsStore the DNS observer feeds, so
        // the aggregator's existing flush-time lookup stamps
        // connections.resolved_host with no IPC/UI/schema change. Closes the
        // Phase 8 DoH gap (Chrome and other in-app resolvers bypass the Windows
        // DNS resolver, so the DNS observer sees nothing for them) by recovering
        // the hostname from the wire instead. Strictly observational —
        // invariant #1 holds; both substrates are receive-only.
        //
        // Like DNS, this is enrichment, not load-bearing: a failed start logs
        // and continues. ECH (TLS 1.3 Encrypted ClientHello) keeps its SNI
        // encrypted and stays unrecoverable — the documented residual gap.
        if (SniCaptureEnabled && !CaptureDisabled)
        {
            _sniSource = TryStartSniCapture(_dnsStore);
        }

        // ---- IPC ----
        var queryRepo       = new AppHistoryQueryRepository(connections);
        var dailyReportRepo = new DailyReportRepository(connections);
        // Re-use the settings cache constructed above for the alert
        // pipeline so the IPC handler and the alert producer share one
        // refresh-on-update surface.
        var settingsRepo = settingsRepoForAlerts;
        var retentionForWipe = new RetentionRepository(
            connections, _loggerFactory.CreateLogger<RetentionRepository>());
        var startModeManager = new ServiceStartModeManager(
            ServiceConstants.ServiceName,
            _loggerFactory.CreateLogger<ServiceStartModeManager>());
        var startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var handler = new ZenVizorIpcHandler(
            startedAtUnixMs,
            dbPath,
            isCaptureActive: () => _captureMonitor?.IsRunning ?? false,
            snapshotProvider: () => aggregator.TakeActivitySnapshot(),
            statsProvider: () =>
            {
                var (seen, unattributed) = aggregator.SnapshotObservationCounters();
                return new ZenVizor.Ipc.Contracts.Dto.CaptureStats(
                    CapturedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ObservationsSeen: seen,
                    ObservationsUnattributed: unattributed);
            },
            appListProvider:     w        => queryRepo.GetAppList(w),
            appDetailProvider:   (id,w,g) => queryRepo.GetAppDetail(id, w, g),
            connectionsProvider: (id,w)   => queryRepo.GetConnections(id, w),
            historyProvider:     (w,g)    => queryRepo.GetTrafficHistory(w, g),
            dailyReportProvider: (d,a,sd) => dailyReportRepo.GetDailyReport(d, a, sd, TimeZoneInfo.Local),
            alertsProvider:      filter   => BuildAlertsResult(alertsRepo, filter),
            alertDismisser:      id       => alertsRepo.Dismiss(
                id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            settingsProvider:    ()       => BuildSettingsSnapshot(settingsRepo, startModeManager),
            settingsApplier:     update   => ApplySettingsUpdate(settingsRepo, startModeManager, update, alertSettingsLookup),
            rollupRulesNowRunner: ()      => alertProducer.EvaluateRollupRulesNow(),
            historyWiper:        ()       => WipeAndLog(retentionForWipe, alertProducer, aggregator, sessionTracker));

        // The alert broadcaster is the fan-out point for server-pushed
        // AlertRaised notifications. The Phase 6 alert producer calls
        // BroadcastAlertRaisedAsync via the event-forwarding hook below
        // when a rule fires; every accepted IPC connection auto-subscribes
        // via the pipe server constructor.
        _alertBroadcaster = new AlertBroadcaster(
            _loggerFactory.CreateLogger<AlertBroadcaster>());

        // Producer → broadcaster bridge. Fire-and-forget so the alert
        // pipeline's hot path never blocks on per-subscriber send latency
        // (the broadcaster's internal try/catch handles slow/broken pipes
        // without stalling other clients). The bridge lives at the
        // composition root rather than inside the producer so the
        // producer (Core) stays ignorant of the IPC transport — same
        // separation Core/Storage already maintains.
        alertProducer.AlertRaised += dto =>
        {
            _ = _alertBroadcaster.BroadcastAlertRaisedAsync(dto);
        };

        _pipeServer = new ZenVizorPipeServer(
            handler,
            _loggerFactory.CreateLogger<ZenVizorPipeServer>(),
            alertBroadcaster: _alertBroadcaster);
        _pipeServer.Start();

        // ---- Retention purge + enrichment backfill. Both mutate the DB, so
        //      they stay off in capture-disabled mode to keep a seeded store
        //      byte-for-byte deterministic: retention would purge anything past
        //      its window and the backfill would re-verify signatures and
        //      overwrite the hand-seeded signer/path attribution. ----
        if (!CaptureDisabled)
        {
            // Retention purge: one immediate run + once per 24 h thereafter.
            var retention = new RetentionRepository(
                connections, _loggerFactory.CreateLogger<RetentionRepository>());
            _retentionCts = new CancellationTokenSource();
            _retentionLoop = Task.Run(() => RunRetentionLoopAsync(retention, _retentionCts.Token));

            // Enrichment backfill: fire-and-forget after capture is up.
            _backfillCts = new CancellationTokenSource();
            _backfillTask = Task.Run(() => RunBackfillSafelyAsync(backfill, _backfillCts.Token));
        }

        _logger.LogInformation(
            "ZenVizor service started. DbPath={DbPath} Pipe=\\\\.\\pipe\\ZenVizor.Ipc.v1 CaptureActive={Active}",
            dbPath, _captureMonitor.IsRunning);
    }

    /// <summary>
    /// Translates <see cref="AlertsRepository.Query"/> rows into the wire
    /// <see cref="AlertsResult"/>. Owns the string→enum conversion + HasMore
    /// truncation. <see cref="AlertsRepository.Query"/> internally fetches
    /// MaxRows+1 so the truncation check is a length comparison, no extra
    /// COUNT round-trip.
    /// <para>
    /// AppId derivation: for App-scoped alerts the EntityRef IS the app id,
    /// so the wire DTO populates AppId by parsing EntityRef. For
    /// Session-scoped (and future Device/File) alerts, AppId would need to
    /// come from a producer-populated column — out of scope for Phase 6.1
    /// since the only shipped producer is App-scoped.
    /// </para>
    /// </summary>
    private static AlertsResult BuildAlertsResult(AlertsRepository repo, AlertsFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var rows = repo.Query(filter.State, filter.MaxRows);
        var hasMore = rows.Count > filter.MaxRows;
        var visible = hasMore ? rows.Take(filter.MaxRows) : rows;

        var dtos = new List<AlertDto>(filter.MaxRows);
        foreach (var r in visible)
        {
            var entityKind = ParseEnum(r.EntityKind, AlertEntityKind.App);
            int? appId = entityKind == AlertEntityKind.App && int.TryParse(r.EntityRef, out var parsed)
                ? parsed
                : null;

            dtos.Add(new AlertDto(
                AlertId:              r.AlertId,
                Type:                 ParseEnum(r.Type,     AlertType.UnsignedFromUserPath),
                Severity:             ParseEnum(r.Severity, NotableSeverity.Info),
                CreatedAtUnixMs:      r.CreatedAtUnixMs,
                Source:               ParseEnum(r.SourceMonitor, SourceMonitor.Capture),
                EntityKind:           entityKind,
                EntityRef:            r.EntityRef,
                Title:                r.Title,
                Detail:               r.Detail,
                AcknowledgedAtUnixMs: r.AcknowledgedAtUnixMs,
                AppId:                appId));
        }

        return new AlertsResult(filter, dtos, hasMore);
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Epic B (1.2.0) — one-shot per-severity toast key seeding. Fast
    /// path: if all three per-severity keys already exist, returns
    /// immediately. Otherwise seeds any missing keys — the value comes
    /// from the legacy <c>toast.on_alert</c> row when it's present
    /// (upgrade from 1.1.x — honour the user's prior all-or-nothing
    /// intent) and defaults to <c>(Critical=1, Warning=0, Info=0)</c>
    /// on a truly fresh install (gating-phase default). Individual keys
    /// are only written when absent, so a partial state (user manually
    /// set one of the three via zvctl before the others were seeded)
    /// isn't overwritten.
    /// </summary>
    internal static void MigrateToastPreferencesIfNeeded(
        SettingsRepository settings,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var hasCritical = settings.GetString(SettingsRepository.Keys.ToastOnCritical) is not null;
        var hasWarning  = settings.GetString(SettingsRepository.Keys.ToastOnWarning)  is not null;
        var hasInfo     = settings.GetString(SettingsRepository.Keys.ToastOnInfo)     is not null;
        if (hasCritical && hasWarning && hasInfo) return;

        var legacyRaw = settings.GetString(SettingsRepository.Keys.ToastOnAlert);
        bool defaultCritical, defaultWarning, defaultInfo;
        string reason;

        if (legacyRaw == "1")
        {
            // 1.1.x upgrade with master ON. Honour prior intent.
            defaultCritical = defaultWarning = defaultInfo = true;
            reason = "legacy toast.on_alert=1 → all severities on (upgrade honour)";
        }
        else if (legacyRaw == "0")
        {
            // 1.1.x upgrade with master OFF. Honour prior intent.
            defaultCritical = defaultWarning = defaultInfo = false;
            reason = "legacy toast.on_alert=0 → all severities off (upgrade honour)";
        }
        else
        {
            // Fresh install (no legacy row) OR unparseable legacy value.
            // Land the gating-phase default.
            defaultCritical = true;
            defaultWarning  = false;
            defaultInfo     = false;
            reason = "fresh-install gating default (Critical only)";
        }

        if (!hasCritical) settings.SetBool(SettingsRepository.Keys.ToastOnCritical, defaultCritical);
        if (!hasWarning)  settings.SetBool(SettingsRepository.Keys.ToastOnWarning,  defaultWarning);
        if (!hasInfo)     settings.SetBool(SettingsRepository.Keys.ToastOnInfo,     defaultInfo);

        logger.LogInformation(
            "Per-severity toast preferences seeded: Critical={C} Warning={W} Info={I} — {Reason}.",
            defaultCritical, defaultWarning, defaultInfo, reason);
    }

    /// <summary>
    /// Builds the Settings snapshot returned by
    /// <c>GetSettingsAsync</c>. SCM start-mode is queried live so the UI sees
    /// reality, not the cached <c>autostart.mode</c> row (which may have
    /// drifted if someone ran <c>sc.exe config</c> out-of-band). The cached
    /// row is updated as a side effect so subsequent reads stay fast.
    /// </summary>
    private static SettingsSnapshot BuildSettingsSnapshot(
        SettingsRepository settings,
        ServiceStartModeManager startModeManager)
    {
        var liveMode = startModeManager.Get();
        // Update the cached mirror row so it stays consistent for diagnostics.
        settings.Set(SettingsRepository.Keys.AutostartMode, liveMode.ToString());

        return new SettingsSnapshot(
            AutostartMode:               liveMode,
            // ToastOnAlert is the computed OR of the three per-severity
            // fields for back-compat with 1.1.x UIs. Snapshots always
            // build the master from the source-of-truth per-severity
            // keys; the legacy toast.on_alert row is written on updates
            // for the same reason (see ApplySettingsUpdate).
            ToastOnAlert:                settings.GetBool(SettingsRepository.Keys.ToastOnCritical, true)
                                       || settings.GetBool(SettingsRepository.Keys.ToastOnWarning,  false)
                                       || settings.GetBool(SettingsRepository.Keys.ToastOnInfo,     false),
            Theme:                       ParseEnum(
                                            settings.GetString(SettingsRepository.Keys.AppearanceTheme) ?? "System",
                                            AppTheme.System),
            FlushIntervalMs:             settings.GetInt(SettingsRepository.Keys.FlushIntervalMs,  5000),
            FlushBucketSeconds:          settings.GetInt(SettingsRepository.Keys.FlushBucketSecs,  60),
            RetentionSamplesDays:        settings.GetInt(SettingsRepository.Keys.SamplesDays,         30),
            RetentionConnectionsDays:    settings.GetInt(SettingsRepository.Keys.ConnectionsDays,     30),
            RetentionHourlyDays:         settings.GetInt(SettingsRepository.Keys.HourlyDays,          90),
            RetentionDailyDays:          settings.GetInt(SettingsRepository.Keys.DailyDays,           365),
            RetentionAlertsDaysAfterAck: settings.GetInt(SettingsRepository.Keys.AlertsDaysAfterAck,  90),
            StartMinimized:              settings.GetBool(SettingsRepository.Keys.StartMinimized,    false),
            AlertLargeDownloadMb:        settings.GetInt(SettingsRepository.Keys.AlertLargeDownloadMb,            50),
            AlertOutboundHeavyFloorMb:   settings.GetInt(SettingsRepository.Keys.AlertOutboundHeavyFloorMb,       10),
            AlertUnusualDailyVolumeKTimesTen: settings.GetInt(SettingsRepository.Keys.AlertUnusualDailyVolumeKTimesTen, 25),
            SmoothChartAnimations:       settings.GetBool(SettingsRepository.Keys.SmoothChartAnimations,         false),
            ToastOnCritical:             settings.GetBool(SettingsRepository.Keys.ToastOnCritical, true),
            ToastOnWarning:              settings.GetBool(SettingsRepository.Keys.ToastOnWarning,  false),
            ToastOnInfo:                 settings.GetBool(SettingsRepository.Keys.ToastOnInfo,     false));
    }

    /// <summary>
    /// Applies each non-null field on the update. Autostart-mode changes
    /// hit SCM via <c>ChangeServiceConfig</c>; failure there throws and
    /// the rest of the update is skipped — partial-success is not exposed
    /// to the wire so the UI never reads a half-applied state.
    /// </summary>
    private static void ApplySettingsUpdate(
        SettingsRepository settings,
        ServiceStartModeManager startModeManager,
        SettingsUpdate update,
        CachedAlertSettingsLookup alertSettingsLookup)
    {
        if (update.AutostartMode is { } mode)
        {
            startModeManager.Set(mode);
            settings.Set(SettingsRepository.Keys.AutostartMode, mode.ToString());
        }
        // Apply the legacy master FIRST so any per-severity fields on
        // the same update take precedence. Writing the master mass-sets
        // the three per-severity keys — this is the 1.1.x back-compat
        // path (an older UI that only knows the master still gets a
        // coherent effect). The legacy toast.on_alert row is also
        // maintained so older UIs continue to read a meaningful value
        // on their own snapshots.
        if (update.ToastOnAlert is { } toast)
        {
            settings.SetBool(SettingsRepository.Keys.ToastOnAlert,    toast);
            settings.SetBool(SettingsRepository.Keys.ToastOnCritical, toast);
            settings.SetBool(SettingsRepository.Keys.ToastOnWarning,  toast);
            settings.SetBool(SettingsRepository.Keys.ToastOnInfo,     toast);
        }
        if (update.ToastOnCritical is { } tc)
        {
            settings.SetBool(SettingsRepository.Keys.ToastOnCritical, tc);
        }
        if (update.ToastOnWarning is { } tw)
        {
            settings.SetBool(SettingsRepository.Keys.ToastOnWarning, tw);
        }
        if (update.ToastOnInfo is { } ti)
        {
            settings.SetBool(SettingsRepository.Keys.ToastOnInfo, ti);
        }
        // Recompute the legacy master row after per-severity edits so
        // an older UI reading toast.on_alert directly sees a consistent
        // "any severity on" answer.
        if (update.ToastOnCritical is not null ||
            update.ToastOnWarning  is not null ||
            update.ToastOnInfo     is not null)
        {
            var anyOn = settings.GetBool(SettingsRepository.Keys.ToastOnCritical, true)
                     || settings.GetBool(SettingsRepository.Keys.ToastOnWarning,  false)
                     || settings.GetBool(SettingsRepository.Keys.ToastOnInfo,     false);
            settings.SetBool(SettingsRepository.Keys.ToastOnAlert, anyOn);
        }
        if (update.Theme is { } theme)
        {
            settings.Set(SettingsRepository.Keys.AppearanceTheme, theme.ToString());
        }
        if (update.RetentionSamplesDays         is { } a) settings.SetInt(SettingsRepository.Keys.SamplesDays,        a);
        if (update.RetentionConnectionsDays     is { } b) settings.SetInt(SettingsRepository.Keys.ConnectionsDays,    b);
        if (update.RetentionHourlyDays          is { } c) settings.SetInt(SettingsRepository.Keys.HourlyDays,         c);
        if (update.RetentionDailyDays           is { } d) settings.SetInt(SettingsRepository.Keys.DailyDays,          d);
        if (update.RetentionAlertsDaysAfterAck  is { } e) settings.SetInt(SettingsRepository.Keys.AlertsDaysAfterAck, e);
        if (update.StartMinimized               is { } m) settings.SetBool(SettingsRepository.Keys.StartMinimized,    m);

        // Phase 6.7 — alert producer thresholds. Range validation (1-1024 MB
        // for byte thresholds, 10-100 for k×10) is light here; the IPC
        // handler validation block above already gates the wire-level shape
        // before we land in this routine. Producer reads via
        // IAlertSettingsLookup on every flush so updates take effect on the
        // next flush tick without a service restart.
        var alertWritten = false;
        if (update.AlertLargeDownloadMb            is { } ldmb) { settings.SetInt(SettingsRepository.Keys.AlertLargeDownloadMb,            ldmb); alertWritten = true; }
        if (update.AlertOutboundHeavyFloorMb       is { } ohmb) { settings.SetInt(SettingsRepository.Keys.AlertOutboundHeavyFloorMb,       ohmb); alertWritten = true; }
        if (update.AlertUnusualDailyVolumeKTimesTen is { } uvk) { settings.SetInt(SettingsRepository.Keys.AlertUnusualDailyVolumeKTimesTen, uvk); alertWritten = true; }

        // Phase 9.a — Dashboard smooth-chart-animations toggle. No producer
        // cache to refresh: DashboardPage re-reads this on every page load,
        // so a toggle here takes effect on the next nav to Dashboard.
        if (update.SmoothChartAnimations is { } sca) settings.SetBool(SettingsRepository.Keys.SmoothChartAnimations, sca);
        if (alertWritten)
        {
            // Producer rules read via alertSettingsLookup on every flush;
            // re-cache the new values atomically so the next flush picks
            // them up without a service restart.
            alertSettingsLookup.Refresh();
        }
    }

    /// <summary>
    /// Wraps <see cref="RetentionRepository.WipeHistory"/> into the wire
    /// shape. After the DB wipe, also clears the alert producer's in-memory
    /// dedup cache via <see cref="AlertProducer.ForgetAll"/> so the next
    /// qualifying observation re-raises a fresh alert rather than being
    /// silently absorbed as a "still active" hit against the now-deleted
    /// row.
    /// </summary>
    private static WipeHistoryResult Wipe(RetentionRepository retention, AlertProducer alertProducer)
    {
        var r = retention.WipeHistory();
        alertProducer.ForgetAll();
        return new WipeHistoryResult(
            SamplesDeleted:     r.SamplesDeleted,
            ConnectionsDeleted: r.ConnectionsDeleted,
            HourlyDeleted:      r.HourlyDeleted,
            DailyDeleted:       r.DailyDeleted,
            AlertsDeleted:      r.AlertsDeleted,
            SessionsDeleted:    r.SessionsDeleted);
    }

    /// <summary>
    /// Instance-method shim around <see cref="Wipe"/> that, after the DB
    /// wipe + alert producer cache clear, also nukes the in-memory state
    /// held by <see cref="TrafficAggregator"/> (pid → app_id cache,
    /// per-flush sample / connection accumulators, rolling activity
    /// window) and <see cref="SessionTracker"/> (tracked PIDs, pending
    /// explicit closes). Mirrors what a process restart accomplishes for
    /// these layers without paying the ETW resubscribe cost.
    /// <para>
    /// Logs each phase so the service log makes it visible from a single
    /// "History wipe complete: ..." line that all three resets executed.
    /// Empirical Phase 6.3 finding (2026-06-17): the alert producer cache
    /// clear alone wasn't enough to re-arm re-firing — a service restart
    /// was; this extends the reset to layers that were carrying
    /// equivalent stale state.
    /// </para>
    /// </summary>
    private WipeHistoryResult WipeAndLog(
        RetentionRepository retention,
        AlertProducer alertProducer,
        TrafficAggregator aggregator,
        SessionTracker sessionTracker)
    {
        var result = Wipe(retention, alertProducer);
        var aggCounts = aggregator.ResetInMemoryState();
        var sessionsDropped = sessionTracker.ResetTrackerState();
        _logger.LogInformation(
            "History wipe complete: samples={S} connections={C} hourly={H} daily={D} alerts={A} sessions={Ss}; " +
            "producer cache cleared; aggregator reset (pidToAppId={Pid} samples={Sam} connections={Conn}); " +
            "sessionTracker reset (tracked={Tracked}).",
            result.SamplesDeleted, result.ConnectionsDeleted, result.HourlyDeleted,
            result.DailyDeleted, result.AlertsDeleted, result.SessionsDeleted,
            aggCounts.PidToAppIdEntries, aggCounts.SamplesEntries, aggCounts.ConnectionsEntries,
            sessionsDropped);
        return result;
    }

    private async Task RunRetentionLoopAsync(RetentionRepository retention, CancellationToken cancellationToken)
    {
        // Immediate purge on startup so a freshly-installed service that
        // hasn't run for a long time doesn't carry a backlog into capture.
        TryRunPurge(retention);

        try
        {
            using var timer = new PeriodicTimer(RetentionPurgeInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                TryRunPurge(retention);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void TryRunPurge(RetentionRepository retention)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            retention.PurgeOlderThan(now);
        }
        catch (Exception ex)
        {
            // Purge failures are non-fatal — the next tick retries.
            _logger.LogWarning(ex, "Retention purge failed; will retry on next tick.");
        }
    }

    /// <summary>
    /// Phase 6.7 — map storage-side <see cref="DailyTrafficLookupRow"/>
    /// to the core-side <see cref="DailyVolumeRow"/> the
    /// <see cref="UnusualDailyVolumeRule"/> consumes. The shapes are
    /// nearly identical but kept on opposite sides of the layering
    /// boundary so Core stays free of Storage references.
    /// </summary>
    private static IReadOnlyList<DailyVolumeRow> MapDailyTotals(IReadOnlyList<DailyTrafficLookupRow> rows)
    {
        if (rows.Count == 0) return Array.Empty<DailyVolumeRow>();
        var mapped = new List<DailyVolumeRow>(rows.Count);
        foreach (var r in rows)
        {
            mapped.Add(new DailyVolumeRow(
                AppId:      r.AppId,
                ImageName:  r.ImageName,
                ImagePath:  r.ImagePath,
                DayUnixMs:  r.BucketStartUnixMs,
                BytesUp:    r.BytesUp,
                BytesDown:  r.BytesDown));
        }
        return mapped;
    }

    private Task RunBackfillSafelyAsync(EnrichmentBackfill backfill, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                backfill.Run(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Non-fatal: capture continues. Remaining Unchecked rows get
                // another chance on the next service start.
                _logger.LogWarning(ex, "Enrichment backfill failed; will retry on next service start.");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Start the SNI source on the primary PktMon substrate; on a hard start
    /// failure (the PktMon control surface is unavailable — Phase 8.5 §7) fall
    /// back to the receive-only raw-socket substrate. Returns null if neither
    /// substrate could be started; SNI enrichment is then simply absent and the
    /// rest of the pipeline carries on with resolved_host left to the DNS
    /// observer. Disposes a partially-started source before falling back so we
    /// don't leak its ETW session / PktMon filters.
    /// </summary>
    private SniCaptureSource? TryStartSniCapture(DnsResolutionStore store)
    {
        foreach (var substrate in new[] { SniSubstrate.PktMon, SniSubstrate.RawSocket })
        {
            var source = new SniCaptureSource(
                store,
                logger: _loggerFactory.CreateLogger<SniCaptureSource>(),
                substrate: substrate);
            try
            {
                source.Start();
                _logger.LogInformation("SNI capture started on the {Substrate} substrate.", substrate);
                return source;
            }
            catch (Exception ex)
            {
                _ = source.DisposeAsync();
                _logger.LogWarning(ex,
                    "SNI capture failed to start on the {Substrate} substrate.", substrate);
            }
        }

        _logger.LogWarning(
            "SNI capture could not start on any substrate; resolved_host falls back to DNS observation only.");
        return null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ZenVizor service stopping.");

        if (_backfillCts is not null)
        {
            _backfillCts.Cancel();
            if (_backfillTask is not null)
            {
                try { await _backfillTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _backfillCts.Dispose();
            _backfillCts = null;
            _backfillTask = null;
        }

        if (_retentionCts is not null)
        {
            _retentionCts.Cancel();
            if (_retentionLoop is not null)
            {
                try { await _retentionLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _retentionCts.Dispose();
            _retentionCts = null;
            _retentionLoop = null;
        }

        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync().ConfigureAwait(false);
            _pipeServer = null;
        }

        if (_captureMonitor is not null)
        {
            await _captureMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            _captureMonitor = null;
        }

        if (_sniSource is not null)
        {
            await _sniSource.DisposeAsync().ConfigureAwait(false);
            _sniSource = null;
        }

        if (_dnsSource is not null)
        {
            await _dnsSource.DisposeAsync().ConfigureAwait(false);
            _dnsSource = null;
        }
        _dnsStore = null;

        _captureSource = null;
    }
}
