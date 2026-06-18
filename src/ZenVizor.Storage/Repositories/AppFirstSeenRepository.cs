using Microsoft.Data.Sqlite;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Read-only point lookup for <c>apps.first_seen</c>. Used by the alert
/// producer's first-seen lookup cache (Phase 6.7) so
/// <see cref="ZenVizor.Core.Alerts.FirstRunWanTalkerRule"/> can gate on
/// "this app was created within the last N seconds." The producer
/// caches the result in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// — <c>first_seen</c> is write-once per row in the apps table, so a
/// once-per-app round-trip is the worst case.
/// </summary>
public sealed class AppFirstSeenRepository
{
    private readonly ConnectionFactory _connections;

    public AppFirstSeenRepository(ConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Returns <c>apps.first_seen</c> for the row at <paramref name="appId"/>
    /// or zero if no such row exists (race: producer evaluating against a
    /// session whose app_id just landed in the same flush; the lookup
    /// missed the INSERT). The producer treats zero as "no first-seen
    /// known" and the rule predicate correctly rejects.
    /// </summary>
    public long GetFirstSeenUnixMs(int appId)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT first_seen FROM apps WHERE app_id = $appId;";
        cmd.Parameters.AddWithValue("$appId", appId);
        var result = cmd.ExecuteScalar();
        return result is long v ? v : 0L;
    }
}
