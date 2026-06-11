using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage;

/// <summary>
/// One-shot enrichment of existing <c>apps</c> rows whose
/// <c>signature_status</c> is still <c>'Unchecked'</c> from Phase 1. Per
/// Phase 2 Q10, this exists so users who installed Phase 1 first don't have
/// historical rows that stay <c>Unchecked</c> forever.
/// </summary>
/// <remarks>
/// <para>
/// Invoked from <c>ZenVizorHostedService.StartAsync</c> as a background task
/// AFTER the capture monitor starts, so a large backlog of Unchecked rows
/// can't delay capture startup. The race with new-session inserts that this
/// implies is bounded: backfill never inserts apps rows — only UPDATEs
/// existing ones — and any constraint conflict from a concurrent session
/// insert hitting the same <c>(image_path, publisher)</c> key is caught and
/// the row is skipped (logged at warning).
/// </para>
/// <para>
/// Batched at <see cref="DefaultBatchSize"/> with a
/// <see cref="DefaultInterBatchDelay"/> pause between batches purely to smooth
/// the <c>WinVerifyTrust</c> workload on systems with many apps; the inter-batch
/// sleep is not a concurrency-safety mechanism. Idempotent: re-runs on a clean
/// DB do nothing.
/// </para>
/// <para>
/// Capped at <see cref="DefaultMaxRowsPerRun"/> rows per service start so a
/// pathological backlog can't pin a worker thread indefinitely; remaining
/// rows are picked up on subsequent restarts. The SELECT itself uses LIMIT
/// to avoid materializing the whole pending set into memory.
/// </para>
/// </remarks>
public sealed class EnrichmentBackfill
{
    public const int DefaultBatchSize = 10;
    public const int DefaultMaxRowsPerRun = 10_000;
    public static readonly TimeSpan DefaultInterBatchDelay = TimeSpan.FromMilliseconds(100);

    private readonly ConnectionFactory _connections;
    private readonly IAppEnricher _enricher;
    private readonly int _batchSize;
    private readonly int _maxRowsPerRun;
    private readonly TimeSpan _interBatchDelay;
    private readonly ILogger _logger;

    public EnrichmentBackfill(
        ConnectionFactory connections,
        IAppEnricher enricher,
        ILogger<EnrichmentBackfill>? logger = null,
        int batchSize = DefaultBatchSize,
        TimeSpan? interBatchDelay = null,
        int maxRowsPerRun = DefaultMaxRowsPerRun)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _enricher = enricher ?? throw new ArgumentNullException(nameof(enricher));
        _batchSize = batchSize <= 0 ? DefaultBatchSize : batchSize;
        _maxRowsPerRun = maxRowsPerRun <= 0 ? DefaultMaxRowsPerRun : maxRowsPerRun;
        _interBatchDelay = interBatchDelay ?? DefaultInterBatchDelay;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public EnrichmentBackfillResult Run(CancellationToken cancellationToken = default)
    {
        var updated = 0;
        var skipped = 0;
        var processed = 0;
        var lastSeenAppId = 0;

        while (processed < _maxRowsPerRun)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Page through pending rows by app_id so we never materialize the
            // full backlog at once. We also use the order-by cursor to skip
            // rows that an earlier batch failed to update (constraint
            // conflicts), so they don't re-appear in the next SELECT and
            // create an infinite loop.
            var remainingRoom = _maxRowsPerRun - processed;
            var pageSize = Math.Min(_batchSize, remainingRoom);
            var page = LoadPendingApps(lastSeenAppId, pageSize);
            if (page.Count == 0) break;

            lastSeenAppId = page[^1].AppId;

            // Enrich (off-DB) before opening the connection so we don't hold
            // a write connection while WinVerifyTrust runs.
            var batch = new List<(int AppId, EnrichmentResult Result)>(page.Count);
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = new ProcessImageInfo(
                    Pid: 0,
                    ImagePath: row.ImagePath,
                    ImageName: row.ImageName,
                    StartTimeUnixMs: 0);
                var enrichment = _enricher.Enrich(image);
                processed++;
                if (enrichment.SignatureStatus == "Unchecked")
                {
                    skipped++;
                    continue;
                }
                batch.Add((row.AppId, enrichment));
            }

            if (batch.Count == 0)
            {
                if (page.Count < pageSize) break;
                continue;
            }

            var (batchUpdated, batchSkipped) = ApplyBatch(batch);
            updated += batchUpdated;
            skipped += batchSkipped;

            if (page.Count < pageSize) break;

            if (_interBatchDelay > TimeSpan.Zero
                && cancellationToken.WaitHandle.WaitOne(_interBatchDelay))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (updated == 0 && skipped == 0)
        {
            _logger.LogInformation("Enrichment backfill: no Unchecked apps rows.");
        }
        else
        {
            _logger.LogInformation(
                "Enrichment backfill done. Updated={Updated} Skipped={Skipped} Cap={Cap}.",
                updated, skipped, _maxRowsPerRun);
        }
        return new EnrichmentBackfillResult(updated, skipped);
    }

    /// <summary>
    /// Apply a batch of enrichment updates inside ONE connection + ONE
    /// transaction. Fast path: all rows commit together. Slow path: if a
    /// concurrent session insert won the race on <c>(image_path, publisher)</c>
    /// and surfaced SQLITE_CONSTRAINT, the whole transaction aborts and we
    /// re-apply the batch row-by-row, skipping just the conflicting rows.
    /// </summary>
    private (int Updated, int Skipped) ApplyBatch(IReadOnlyList<(int AppId, EnrichmentResult Result)> batch)
    {
        using var conn = _connections.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            var written = 0;
            foreach (var (appId, result) in batch)
            {
                WriteUpdate(conn, transaction, appId, result);
                written++;
            }
            transaction.Commit();
            return (written, 0);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            transaction.Rollback();
            return ApplyBatchPerRow(conn, batch);
        }
    }

    private (int Updated, int Skipped) ApplyBatchPerRow(
        SqliteConnection conn,
        IReadOnlyList<(int AppId, EnrichmentResult Result)> batch)
    {
        var updated = 0;
        var skipped = 0;
        foreach (var (appId, result) in batch)
        {
            try
            {
                WriteUpdate(conn, transaction: null, appId, result);
                updated++;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
            {
                _logger.LogWarning(ex,
                    "Backfill UPDATE conflicted for app_id={AppId}; leaving Unchecked.",
                    appId);
                skipped++;
            }
        }
        return (updated, skipped);
    }

    private static void WriteUpdate(
        SqliteConnection conn,
        SqliteTransaction? transaction,
        int appId,
        EnrichmentResult enrichment)
    {
        using var cmd = conn.CreateCommand();
        if (transaction is not null)
        {
            cmd.Transaction = transaction;
        }
        cmd.CommandText = """
            UPDATE apps
            SET publisher = $publisher,
                signature_status = $sig,
                is_user_writable_path = $userWritable,
                path_class = $pathClass
            WHERE app_id = $id;
            """;
        cmd.Parameters.AddWithValue("$publisher", (object?)enrichment.Publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", enrichment.SignatureStatus);
        cmd.Parameters.AddWithValue("$userWritable", enrichment.IsUserWritablePath ? 1 : 0);
        cmd.Parameters.AddWithValue("$pathClass", enrichment.PathClass.ToStorageString());
        cmd.Parameters.AddWithValue("$id", appId);
        cmd.ExecuteNonQuery();
    }

    private List<(int AppId, string ImagePath, string ImageName)> LoadPendingApps(int afterAppId, int limit)
    {
        var rows = new List<(int, string, string)>(limit);
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT app_id, image_path, image_name
            FROM apps
            WHERE signature_status = 'Unchecked'
              AND app_id > $after
            ORDER BY app_id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$after", afterAppId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }
}

public sealed record EnrichmentBackfillResult(int Updated, int Skipped);
