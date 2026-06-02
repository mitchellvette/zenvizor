using System.Data;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZenVizor.Storage;

/// <summary>
/// Embedded-SQL forward-only migration runner. Each migration file is named
/// <c>NNN_name.sql</c> (zero-padded version). Applied versions are tracked
/// in the <c>schema_migrations</c> table.
/// </summary>
public sealed class Migrator
{
    private const string MigrationResourcePrefix =
        "ZenVizor.Storage.Migrations.";

    private readonly ILogger _logger;

    public Migrator(ILogger<Migrator>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Open (or create) the SQLite database at <paramref name="databasePath"/>
    /// and apply all pending migrations. Idempotent.
    /// </summary>
    /// <returns>The list of migration versions newly applied during this call.</returns>
    public IReadOnlyList<int> Migrate(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        // WAL must be set OUTSIDE any transaction — SQLite rejects the pragma
        // from inside one. Persistent: setting once leaves the file in WAL mode.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        EnsureSchemaMigrationsTable(connection);
        var applied = GetAppliedVersions(connection);
        var pending = LoadEmbeddedMigrations()
            .Where(m => !applied.Contains(m.Version))
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation(
                "Database already at latest schema (path: {DatabasePath}).",
                databasePath);
            return Array.Empty<int>();
        }

        var newlyApplied = new List<int>();
        foreach (var migration in pending)
        {
            ApplyMigration(connection, migration);
            newlyApplied.Add(migration.Version);
            _logger.LogInformation(
                "Applied migration {Version}: {Name}",
                migration.Version,
                migration.Name);
        }

        return newlyApplied;
    }

    private static void EnsureSchemaMigrationsTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                applied_at  INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static HashSet<int> GetAppliedVersions(SqliteConnection connection)
    {
        var versions = new HashSet<int>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_migrations;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static void ApplyMigration(SqliteConnection connection, MigrationScript migration)
    {
        using var transaction = connection.BeginTransaction();

        using (var apply = connection.CreateCommand())
        {
            apply.Transaction = transaction;
            apply.CommandText = migration.Sql;
            apply.ExecuteNonQuery();
        }

        using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations (version, name, applied_at)
                VALUES ($version, $name, $applied_at);
                """;
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$name", migration.Name);
            record.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            record.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static IReadOnlyList<MigrationScript> LoadEmbeddedMigrations()
    {
        var assembly = typeof(Migrator).Assembly;
        var results = new List<MigrationScript>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(MigrationResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            if (!resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = resourceName[MigrationResourcePrefix.Length..];
            var (version, name) = ParseMigrationFileName(fileName);

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Failed to open embedded migration resource '{resourceName}'.");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            results.Add(new MigrationScript(version, name, sql));
        }

        return results;
    }

    private static (int Version, string Name) ParseMigrationFileName(string fileName)
    {
        // Expected: NNN_name.sql  (e.g. "001_initial.sql")
        var separator = fileName.IndexOf('_', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidOperationException(
                $"Migration filename '{fileName}' does not match NNN_name.sql.");
        }

        var versionText = fileName[..separator];
        if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            throw new InvalidOperationException(
                $"Migration filename '{fileName}' has non-numeric version prefix '{versionText}'.");
        }

        var name = Path.GetFileNameWithoutExtension(fileName[(separator + 1)..]);
        return (version, name);
    }

    private sealed record MigrationScript(int Version, string Name, string Sql);
}
