using Microsoft.Data.Sqlite;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Centralizes the connection string used by all write-path repositories.
/// <c>Open()</c> is virtual so the Phase-3 integration test can subclass it
/// to enforce "no SQLite open during a snapshot call" — production code
/// uses this class directly without subclassing.
/// </summary>
public class ConnectionFactory
{
    /// <summary>
    /// Per-connection SQLite busy timeout. Without this, contention between
    /// the flush sink (every 5 s), the retention sweep, and IPC-side readers
    /// surfaces as immediate SQLITE_BUSY instead of a short wait. Matched
    /// against the flush interval so a stuck lock can't silently swallow more
    /// than one flush tick before failing loudly.
    /// </summary>
    private const int BusyTimeoutMs = 5000;

    private readonly string _connectionString;

    public ConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            // Pooling enabled — write-path opens many short-lived connections
            // on the flush tick (default every 5s).
            Pooling = true,
        }.ToString();
    }

    public virtual SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // busy_timeout is per-connection, not persisted in the DB file, so it
        // must be set on every open. Pooling reuses connections, but the
        // pragma is cheap enough that re-setting it isn't worth gating on.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
        cmd.ExecuteNonQuery();

        return connection;
    }
}
