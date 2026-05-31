using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TitaniRun.Ipc.Server;
using TitaniRun.Storage;

namespace TitaniRun.Service;

/// <summary>
/// The Phase-0 service entry point. Responsibilities:
/// 1. Create + ACL the <c>%ProgramData%\TitaniRun\</c> data directory.
/// 2. Run the SQLite migrator against <c>titanirun.db</c>.
/// 3. Start the named-pipe IPC server.
/// 4. Log a one-line startup record (verifiable in Event Viewer + log file).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TitaniRunHostedService : IHostedService
{
    private readonly ILogger<TitaniRunHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private TitaniRunPipeServer? _pipeServer;

    public TitaniRunHostedService(
        ILogger<TitaniRunHostedService> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dataDir = StorageConstants.DefaultDataDirectory;
        var dbPath = StorageConstants.DefaultDatabasePath;

        try
        {
            ProgramDataAcl.EnsureDirectoryWithAcl(dataDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            // ACL changes require Admin/SYSTEM. In dev, the developer's account
            // may lack the rights to re-ACL %ProgramData% — log and continue so
            // the rest of the service can still come up. The installer (Phase 6)
            // handles this authoritatively.
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

        var startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var handler = new TitaniRunIpcHandler(startedAtUnixMs, dbPath);

        _pipeServer = new TitaniRunPipeServer(
            handler,
            _loggerFactory.CreateLogger<TitaniRunPipeServer>());
        _pipeServer.Start();

        // The CLAUDE.md Phase-0 manual QA gate looks for this line.
        _logger.LogInformation(
            "TitaniRun service started. DbPath={DbPath} Pipe=\\\\.\\pipe\\TitaniRun.Ipc.v1",
            dbPath);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TitaniRun service stopping.");
        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync().ConfigureAwait(false);
            _pipeServer = null;
        }
    }
}
