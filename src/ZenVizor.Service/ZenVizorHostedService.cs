using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZenVizor.Attribution;
using ZenVizor.Attribution.Authenticode;
using ZenVizor.Attribution.IpHelper;
using ZenVizor.Attribution.Paths;
using ZenVizor.Attribution.Services;
using ZenVizor.Capture;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Attribution;
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
    private const long PidTablePollMs = 1000;

    private readonly ILogger<ZenVizorHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private ZenVizorPipeServer? _pipeServer;
    private CaptureMonitor? _captureMonitor;
    private EtwCaptureSource? _captureSource;

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

        var imageResolver = new RealProcessImageResolver(
            _loggerFactory.CreateLogger<RealProcessImageResolver>());
        var pidTableSource = new IpHelperPidTableSource(
            pollIntervalMs: PidTablePollMs,
            logger: _loggerFactory.CreateLogger<IpHelperPidTableSource>());

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

        // One-shot enrichment of any pre-Phase-2 'Unchecked' apps rows. Runs
        // BEFORE the capture monitor starts so it cannot race with new-session
        // inserts. Idempotent: re-runs are no-ops once all rows are enriched.
        var backfill = new EnrichmentBackfill(
            connections,
            appEnricher,
            _loggerFactory.CreateLogger<EnrichmentBackfill>());
        backfill.Run();

        var sessionTracker = new SessionTracker(imageResolver, appEnricher, serviceHostResolver);
        var aggregator = new TrafficAggregator(
            sessionTracker,
            new PidCorrector(),
            pidTableSource,
            flushSink,
            logger: _loggerFactory.CreateLogger<TrafficAggregator>());

        _captureSource = new EtwCaptureSource(
            logger: _loggerFactory.CreateLogger<EtwCaptureSource>());
        _captureMonitor = new CaptureMonitor(
            _captureSource,
            aggregator,
            FlushInterval,
            _loggerFactory.CreateLogger<CaptureMonitor>());

        await _captureMonitor.StartAsync(cancellationToken).ConfigureAwait(false);

        // ---- IPC ----
        var startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var handler = new ZenVizorIpcHandler(
            startedAtUnixMs,
            dbPath,
            () => _captureMonitor?.IsRunning ?? false);

        _pipeServer = new ZenVizorPipeServer(
            handler,
            _loggerFactory.CreateLogger<ZenVizorPipeServer>());
        _pipeServer.Start();

        _logger.LogInformation(
            "ZenVizor service started. DbPath={DbPath} Pipe=\\\\.\\pipe\\ZenVizor.Ipc.v1 CaptureActive={Active}",
            dbPath, _captureMonitor.IsRunning);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ZenVizor service stopping.");

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

        _captureSource = null;
    }
}
