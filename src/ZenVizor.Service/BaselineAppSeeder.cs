// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZenVizor.Attribution;
using ZenVizor.Core.Attribution;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Service;

/// <summary>
/// Epic B (1.2.0) — one-shot running-process baseline seed. Runs on
/// first service start after install, before capture begins. Enumerates
/// currently-running processes, enriches each distinct image path
/// through the same <see cref="AppEnricher"/> the live capture pipeline
/// uses, and inserts an <c>apps</c> row with <c>first_seen = install
/// epoch</c> for every image not already in the table.
/// <para>
/// This is what makes the baseline gate in
/// <see cref="ZenVizor.Core.Alerts.FirstRunWanTalkerRule"/> effective on
/// day one. Without the seed, an app is only recorded in <c>apps</c>
/// the moment ZenVizor first observes a WAN connection from it — so
/// every long-running pre-existing app would get a fresh <c>first_seen</c>
/// stamp right around the time it opens its first connection, land
/// squarely inside the 60 s first-run window, and fire a false-positive
/// FirstRunWanTalker alert (even with the install-epoch gate, because
/// its own <c>first_seen</c> would be well past <c>install_epoch + 48 h</c>
/// on any user's second-day session).
/// </para>
/// <para>
/// <b>Invariant #1.</b> Strictly local — <see cref="Process.GetProcesses"/>
/// only. No sockets, no probes, no reverse DNS. Verified under the
/// self-monitoring gate.
/// </para>
/// <para>
/// <b>Idempotence.</b> Guarded by the <c>baseline.setup_scan_done</c>
/// settings key. A second call after the flag is set is a no-op.
/// Even without the guard, <c>INSERT OR IGNORE</c> against the
/// <c>(image_path, IFNULL(publisher, ''))</c> unique index would keep
/// re-runs safe — the flag just avoids re-doing the enumeration +
/// enrichment work.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BaselineAppSeeder
{
    private readonly ConnectionFactory _connections;
    private readonly AppEnricher _enricher;
    private readonly ILogger _logger;

    public BaselineAppSeeder(
        ConnectionFactory connections,
        AppEnricher enricher,
        ILogger<BaselineAppSeeder> logger)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _enricher = enricher ?? throw new ArgumentNullException(nameof(enricher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs the seed if it hasn't run before on this install. The
    /// guard key (<see cref="SettingsRepository.Keys.BaselineSetupScanDone"/>)
    /// is checked + set on the seeder's own connection to avoid
    /// deadlocking against the write transaction the seed opens
    /// internally (an early cut called <see cref="SettingsRepository.Set"/>
    /// mid-transaction and hung on SQLite's writer serialization).
    /// Returns the number of rows inserted (0 if the guard was
    /// already set or every running image was already in <c>apps</c>).
    /// </summary>
    public int SeedIfNeeded(long installEpochUnixMs)
    {
        if (installEpochUnixMs <= 0)
        {
            _logger.LogWarning(
                "BaselineAppSeeder skipped — install epoch is not set (received {Epoch}).",
                installEpochUnixMs);
            return 0;
        }

        // Presence-check on the guard key uses a bare connection with
        // no transaction so it can't self-deadlock. The write below runs
        // inside the same transaction that inserts the seeded rows so
        // "seed complete" is atomic with the row set.
        if (ReadSettingsKey(SettingsRepository.Keys.BaselineSetupScanDone) == "1")
        {
            return 0;
        }

        var distinctImages = EnumerateDistinctImages();
        _logger.LogInformation(
            "BaselineAppSeeder: enumerated {Count} distinct running images.",
            distinctImages.Count);

        // Enrichment runs BEFORE opening a DB connection. WinVerifyTrust
        // over 100+ binaries can take tens of seconds on a cold cache; a
        // write transaction held across that window would block every
        // other writer (settings updates, alert inserts, retention
        // purge) for the full duration. Enrich into an in-memory list
        // first, then hold the DB transaction only for the batch
        // insert.
        var enrichedRows = new List<(string ImagePath, EnrichmentResult Result)>(distinctImages.Count);
        foreach (var imagePath in distinctImages)
        {
            var probe = new ProcessImageInfo(
                Pid: 0,
                ImagePath: imagePath,
                ImageName: Path.GetFileName(imagePath),
                StartTimeUnixMs: 0);
            try
            {
                enrichedRows.Add((imagePath, _enricher.Enrich(probe)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "BaselineAppSeeder: enrichment failed for {Path}; skipping.",
                    imagePath);
            }
        }

        int inserted = 0;
        using (var connection = _connections.Open())
        using (var tx = connection.BeginTransaction())
        {
            foreach (var (imagePath, enriched) in enrichedRows)
            {
                if (TryInsertBaselineRow(connection, tx, imagePath, enriched, installEpochUnixMs))
                {
                    inserted++;
                }
            }

            // Write the guard flag inside the same transaction so the
            // "seed complete" signal and the seeded rows commit
            // atomically. SettingsRepository.Set() opens its own
            // connection — using it here would race the transaction
            // held on `connection` and deadlock on SQLite's writer
            // serialization (this is exactly the bug that made the
            // 1.2.0 first cut fail after ~37s of enrichment).
            UpsertSettingsKeyInTx(
                connection, tx,
                SettingsRepository.Keys.BaselineSetupScanDone, "1");

            tx.Commit();
        }

        _logger.LogInformation(
            "BaselineAppSeeder: inserted {Inserted} baseline apps rows (install epoch {Epoch}).",
            inserted, installEpochUnixMs);
        return inserted;
    }

    private string? ReadSettingsKey(string key)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void UpsertSettingsKeyInTx(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private List<string> EnumerateDistinctImages()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BaselineAppSeeder: Process.GetProcesses failed; nothing to seed.");
            return new List<string>();
        }

        foreach (var proc in all)
        {
            try
            {
                string? path = null;
                try
                {
                    path = proc.MainModule?.FileName;
                }
                catch
                {
                    // Protected processes deny MainModule access; a
                    // basename-only path won't map to a stable identity in
                    // apps (no directory, so signature verification is
                    // meaningless), so skip rather than seeding a partial
                    // row that would conflict with a properly-attributed
                    // row later.
                }

                if (!string.IsNullOrEmpty(path))
                {
                    seen.Add(path);
                }
            }
            finally
            {
                proc.Dispose();
            }
        }

        return seen.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryInsertBaselineRow(
        SqliteConnection connection,
        SqliteTransaction tx,
        string imagePath,
        EnrichmentResult enriched,
        long installEpochUnixMs)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        // INSERT OR IGNORE against ux_apps_path_publisher (see 001_initial.sql):
        // if a row for this (image_path, publisher) tuple already exists —
        // e.g. we're re-running the seeder after the guard flag was
        // cleared by hand — the insert is a no-op and no exception is
        // raised. On a fresh install this is always an insert.
        cmd.CommandText = """
            INSERT OR IGNORE INTO apps
                (image_path, image_name, publisher, signature_status, is_user_writable_path,
                 path_class, first_seen, last_seen)
            VALUES
                ($path, $name, $publisher, $sig, $userWritable, $pathClass, $epoch, $epoch);
            """;
        cmd.Parameters.AddWithValue("$path", imagePath);
        cmd.Parameters.AddWithValue("$name", Path.GetFileName(imagePath));
        cmd.Parameters.AddWithValue("$publisher", (object?)enriched.Publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", enriched.SignatureStatus);
        cmd.Parameters.AddWithValue("$userWritable", enriched.IsUserWritablePath ? 1 : 0);
        cmd.Parameters.AddWithValue("$pathClass", enriched.PathClass.ToStorageString());
        cmd.Parameters.AddWithValue("$epoch", installEpochUnixMs);
        return cmd.ExecuteNonQuery() == 1;
    }
}
