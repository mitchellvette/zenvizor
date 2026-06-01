using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TitaniRun.Core.Attribution;
using TitaniRun.Storage.Repositories;

namespace TitaniRun.Storage;

/// <summary>
/// One-shot enrichment of existing <c>apps</c> rows whose
/// <c>signature_status</c> is still <c>'Unchecked'</c> from Phase 1. Per
/// Phase 2 Q10, this exists so users who installed Phase 1 first don't have
/// historical rows that stay <c>Unchecked</c> forever.
/// </summary>
/// <remarks>
/// <para>
/// Invoked from <c>TitaniRunHostedService.StartAsync</c> AFTER the migrator
/// runs and BEFORE the capture monitor starts. That ordering eliminates the
/// race where a fresh session insert (with the enriched publisher) and a
/// backfill UPDATE both target the same <c>(image_path, publisher)</c> key.
/// </para>
/// <para>
/// Batched at <see cref="DefaultBatchSize"/> with a
/// <see cref="DefaultInterBatchDelay"/> pause between batches purely to smooth
/// the <c>WinVerifyTrust</c> workload on systems with many apps; the inter-batch
/// sleep is not a concurrency-safety mechanism. Idempotent: re-runs on a clean
/// DB do nothing.
/// </para>
/// </remarks>
public sealed class EnrichmentBackfill
{
    public const int DefaultBatchSize = 10;
    public static readonly TimeSpan DefaultInterBatchDelay = TimeSpan.FromMilliseconds(100);

    private readonly ConnectionFactory _connections;
    private readonly IAppEnricher _enricher;
    private readonly int _batchSize;
    private readonly TimeSpan _interBatchDelay;
    private readonly ILogger _logger;

    public EnrichmentBackfill(
        ConnectionFactory connections,
        IAppEnricher enricher,
        ILogger<EnrichmentBackfill>? logger = null,
        int batchSize = DefaultBatchSize,
        TimeSpan? interBatchDelay = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _enricher = enricher ?? throw new ArgumentNullException(nameof(enricher));
        _batchSize = batchSize <= 0 ? DefaultBatchSize : batchSize;
        _interBatchDelay = interBatchDelay ?? DefaultInterBatchDelay;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public EnrichmentBackfillResult Run()
    {
        var pending = LoadPendingApps();
        if (pending.Count == 0)
        {
            _logger.LogInformation("Enrichment backfill: no Unchecked apps rows.");
            return new EnrichmentBackfillResult(Updated: 0, Skipped: 0);
        }

        _logger.LogInformation(
            "Enrichment backfill starting: {Count} apps with signature_status='Unchecked'.",
            pending.Count);

        var updated = 0;
        var skipped = 0;
        for (var batchStart = 0; batchStart < pending.Count; batchStart += _batchSize)
        {
            var batchEnd = Math.Min(batchStart + _batchSize, pending.Count);
            for (var i = batchStart; i < batchEnd; i++)
            {
                var (appId, imagePath, imageName) = pending[i];
                var image = new ProcessImageInfo(
                    Pid: 0,
                    ImagePath: imagePath,
                    ImageName: imageName,
                    StartTimeUnixMs: 0);
                var enrichment = _enricher.Enrich(image);
                if (enrichment.SignatureStatus == "Unchecked")
                {
                    skipped++;
                    continue;
                }

                try
                {
                    UpdateAppRow(appId, enrichment);
                    updated++;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
                {
                    // Defense in depth: a concurrent session-open inserted
                    // (image_path, publisher=X) before us. Should not happen
                    // because backfill runs before capture starts, but we don't
                    // crash service startup if it does.
                    _logger.LogWarning(ex,
                        "Backfill UPDATE conflicted for app_id={AppId} path={Path}; leaving Unchecked.",
                        appId, imagePath);
                    skipped++;
                }
            }

            if (batchEnd < pending.Count && _interBatchDelay > TimeSpan.Zero)
            {
                Thread.Sleep(_interBatchDelay);
            }
        }

        _logger.LogInformation(
            "Enrichment backfill done. Updated={Updated} Skipped={Skipped}.",
            updated, skipped);
        return new EnrichmentBackfillResult(updated, skipped);
    }

    private List<(int AppId, string ImagePath, string ImageName)> LoadPendingApps()
    {
        var rows = new List<(int, string, string)>();
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT app_id, image_path, image_name
            FROM apps
            WHERE signature_status = 'Unchecked'
            ORDER BY app_id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }

    private void UpdateAppRow(int appId, EnrichmentResult enrichment)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE apps
            SET publisher = $publisher,
                signature_status = $sig,
                is_user_writable_path = $userWritable
            WHERE app_id = $id;
            """;
        cmd.Parameters.AddWithValue("$publisher", (object?)enrichment.Publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", enrichment.SignatureStatus);
        cmd.Parameters.AddWithValue("$userWritable", enrichment.IsUserWritablePath ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", appId);
        cmd.ExecuteNonQuery();
    }
}

public sealed record EnrichmentBackfillResult(int Updated, int Skipped);
