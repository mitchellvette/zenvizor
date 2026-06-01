using FluentAssertions;
using Microsoft.Data.Sqlite;
using TitaniRun.Storage;

namespace TitaniRun.Storage.Tests;

public sealed class MigratorTests : IDisposable
{
    private readonly string _dbPath;

    public MigratorTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"titanirun-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Migrate_FreshDatabase_AppliesAllMigrations()
    {
        var migrator = new Migrator();

        var applied = migrator.Migrate(_dbPath);

        applied.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void Migrate_FreshDatabase_CreatesAllExpectedTables()
    {
        new Migrator().Migrate(_dbPath);

        var tables = GetTableNames(_dbPath);

        var expected = new[]
        {
            "schema_migrations",
            "apps",
            "process_sessions",
            "traffic_samples",
            "connections",
            "traffic_hourly",
            "traffic_daily",
            "alerts",
            "devices",     // RESERVED — defined, not populated
            "settings",
        };

        tables.Should().Contain(expected);
    }

    [Fact]
    public void Migrate_FreshDatabase_RecordsAppliedVersion()
    {
        new Migrator().Migrate(_dbPath);

        var rows = Query(
            _dbPath,
            "SELECT version, name FROM schema_migrations ORDER BY version;",
            r => (Version: r.GetInt32(0), Name: r.GetString(1)));

        rows.Should().HaveCount(2);
        rows[0].Version.Should().Be(1);
        rows[0].Name.Should().Be("initial");
        rows[1].Version.Should().Be(2);
        rows[1].Name.Should().Be("phase1_settings");
    }

    [Fact]
    public void Migrate_RunTwice_IsIdempotent()
    {
        var migrator = new Migrator();
        migrator.Migrate(_dbPath);

        var secondPass = migrator.Migrate(_dbPath);

        secondPass.Should().BeEmpty();
    }

    [Fact]
    public void Migrate_SeedsRetentionAndOperationalSettings()
    {
        new Migrator().Migrate(_dbPath);

        var settings = Query(
            _dbPath,
            "SELECT key, value FROM settings;",
            r => (Key: r.GetString(0), Value: r.GetString(1)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        settings.Should().Contain(new KeyValuePair<string, string>("retention.traffic_samples_days", "30"));
        settings.Should().Contain(new KeyValuePair<string, string>("retention.connections_days", "30"));
        settings.Should().Contain(new KeyValuePair<string, string>("retention.traffic_hourly_days", "90"));
        settings.Should().Contain(new KeyValuePair<string, string>("retention.traffic_daily_days", "365"));
        settings.Should().Contain(new KeyValuePair<string, string>("retention.alerts_days_after_ack", "90"));
        settings.Should().Contain(new KeyValuePair<string, string>("flush.interval_ms", "5000"));
        settings.Should().Contain(new KeyValuePair<string, string>("flush.bucket_seconds", "60"));
        settings.Should().Contain(new KeyValuePair<string, string>("toast.on_alert", "1"));
        settings.Should().Contain(new KeyValuePair<string, string>("autostart.mirror", "1"));
        settings.Should().Contain(new KeyValuePair<string, string>("pid_table.poll_ms", "1000"));
        settings.Should().Contain(new KeyValuePair<string, string>("session.end_grace_ms", "30000"));
    }

    [Fact]
    public void Migrate_CreatesExpectedHotPathIndexes()
    {
        new Migrator().Migrate(_dbPath);

        var indexes = Query(
            _dbPath,
            "SELECT name FROM sqlite_master WHERE type = 'index';",
            r => r.GetString(0))
            .ToHashSet(StringComparer.Ordinal);

        indexes.Should().Contain("ix_traffic_samples_bucket");
        indexes.Should().Contain("ix_traffic_samples_session_bucket");
        indexes.Should().Contain("ux_connections_endpoint");
        indexes.Should().Contain("ix_traffic_hourly_app_bucket");
        indexes.Should().Contain("ix_traffic_daily_app_bucket");
        indexes.Should().Contain("ux_apps_path_publisher");
    }

    [Fact]
    public void Migrate_ConnectionsTable_HasResolvedHostColumn()
    {
        // resolved_host is RESERVED for the future passive-DNS module (post-MVP).
        // Asserts the column exists but is nullable, so we can leave it null in MVP.
        new Migrator().Migrate(_dbPath);

        var columns = GetColumns(_dbPath, "connections");

        columns.Should().Contain(c => c.Name == "resolved_host" && c.NotNull == 0);
    }

    [Fact]
    public void Migrate_AppsTable_DedupKeyTreatsNullPublisherAsBucket()
    {
        new Migrator().Migrate(_dbPath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Same image_path + null publisher should be a single bucket per the unique index.
        Execute(_dbPath,
            "INSERT INTO apps (image_path, image_name, publisher, first_seen, last_seen) " +
            $"VALUES ('C:\\test\\app.exe', 'app.exe', NULL, {now}, {now});");

        var second = () => Execute(_dbPath,
            "INSERT INTO apps (image_path, image_name, publisher, first_seen, last_seen) " +
            $"VALUES ('C:\\test\\app.exe', 'app.exe', NULL, {now}, {now});");

        second.Should().Throw<SqliteException>("the unique index treats two null-publisher inserts as a conflict");
    }

    // ---------- helpers ----------

    private static IReadOnlyList<string> GetTableNames(string dbPath) =>
        Query(
            dbPath,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';",
            r => r.GetString(0));

    private static IReadOnlyList<(string Name, int NotNull)> GetColumns(string dbPath, string tableName) =>
        Query(
            dbPath,
            $"PRAGMA table_info({tableName});",
            r => (Name: r.GetString(1), NotNull: r.GetInt32(3)));

    private static IReadOnlyList<T> Query<T>(string dbPath, string sql, Func<SqliteDataReader, T> map)
    {
        using var connection = OpenReadOnly(dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }

        return results;
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
