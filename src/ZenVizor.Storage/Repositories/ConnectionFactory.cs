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
        return connection;
    }
}
