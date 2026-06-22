// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Data.Sqlite;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Storage.Repositories;

/// <summary>
/// Read/write repository for the <c>alerts</c> table (PRD §7.6, schema in
/// <c>Migrations/001_initial.sql</c>). Owns the producer-side dedupe / cooldown
/// SQL gate and the dismiss flow.
/// <para>
/// All columns are string-typed in the DB even though the wire DTO uses
/// enums — the schema deliberately uses TEXT for forward-compat with future
/// entity kinds and severity tiers that haven't been enumerated yet.
/// </para>
/// </summary>
public sealed class AlertsRepository
{
    private readonly ConnectionFactory _connections;

    public AlertsRepository(ConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Dedupe-cooldown gated insert. Single round-trip — the dedupe predicate
    /// lives in a <c>WHERE NOT EXISTS</c> subquery on the same statement so
    /// there's no read-then-write race against a concurrent producer or a
    /// concurrent dismiss. Returns the new <c>alert_id</c>, or 0 when an
    /// active row OR a row dismissed within the cooldown window for the same
    /// <c>(type, entity_kind, entity_ref)</c> already exists.
    /// </summary>
    public long TryInsert(NewAlert alert, long nowUnixMs, long cooldownMs)
    {
        ArgumentNullException.ThrowIfNull(alert);

        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alerts (type, severity, created_at, source_monitor,
                                entity_kind, entity_ref, title, detail, acknowledged_at)
            SELECT $type, $sev, $now, $source, $kind, $ref, $title, $detail, NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM alerts
                 WHERE type        = $type
                   AND entity_kind = $kind
                   AND entity_ref  = $ref
                   AND (acknowledged_at IS NULL
                        OR acknowledged_at >= $now - $cooldown)
            );
            SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() ELSE 0 END;
            """;
        cmd.Parameters.AddWithValue("$type",     alert.Type);
        cmd.Parameters.AddWithValue("$sev",      alert.Severity);
        cmd.Parameters.AddWithValue("$now",      nowUnixMs);
        cmd.Parameters.AddWithValue("$source",   alert.SourceMonitor);
        cmd.Parameters.AddWithValue("$kind",     alert.EntityKind);
        cmd.Parameters.AddWithValue("$ref",      alert.EntityRef);
        cmd.Parameters.AddWithValue("$title",    alert.Title);
        cmd.Parameters.AddWithValue("$detail",   alert.Detail);
        cmd.Parameters.AddWithValue("$cooldown", cooldownMs);
        var result = cmd.ExecuteScalar();
        return result switch
        {
            long l => l,
            int i  => i,
            _      => 0,
        };
    }

    /// <summary>
    /// In-place detail update for an active alert. Returns the number of rows
    /// updated (0 or 1). Used by the producer to refresh the "Connections so
    /// far: N" phrase as the same app generates more qualifying connections —
    /// the alert row stays the same, only the body text mutates. Matches an
    /// active row only (acknowledged_at IS NULL); dismissed rows are
    /// immutable from the producer's perspective.
    /// </summary>
    public int UpdateDetail(string type, string entityKind, string entityRef, string detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentException.ThrowIfNullOrEmpty(entityKind);
        ArgumentException.ThrowIfNullOrEmpty(entityRef);
        ArgumentNullException.ThrowIfNull(detail);

        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE alerts
               SET detail = $detail
             WHERE type        = $type
               AND entity_kind = $kind
               AND entity_ref  = $ref
               AND acknowledged_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.Parameters.AddWithValue("$type",   type);
        cmd.Parameters.AddWithValue("$kind",   entityKind);
        cmd.Parameters.AddWithValue("$ref",    entityRef);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reverse-chronological query, server-applied State filter per brief
    /// §14. Caller asks for at most <paramref name="maxRows"/> rows; this
    /// method fetches <paramref name="maxRows"/>+1 internally so the wrapper
    /// in <c>ZenVizorIpcHandler</c> can detect <c>HasMore</c> without a
    /// separate COUNT round-trip.
    /// </summary>
    public IReadOnlyList<AlertRow> Query(AlertState state, int maxRows)
    {
        if (maxRows <= 0) return Array.Empty<AlertRow>();

        var stateClause = state switch
        {
            AlertState.Active    => "WHERE acknowledged_at IS NULL",
            AlertState.Dismissed => "WHERE acknowledged_at IS NOT NULL",
            _                    => string.Empty,
        };

        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT alert_id, type, severity, created_at, source_monitor,
                   entity_kind, entity_ref, title, detail, acknowledged_at
              FROM alerts
              {stateClause}
             ORDER BY created_at DESC
             LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", maxRows + 1);

        var rows = new List<AlertRow>(Math.Min(maxRows, 64));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AlertRow(
                AlertId:             reader.GetInt64(0),
                Type:                reader.GetString(1),
                Severity:            reader.GetString(2),
                CreatedAtUnixMs:     reader.GetInt64(3),
                SourceMonitor:       reader.GetString(4),
                EntityKind:          reader.GetString(5),
                EntityRef:           reader.GetString(6),
                Title:               reader.GetString(7),
                Detail:              reader.GetString(8),
                AcknowledgedAtUnixMs: reader.IsDBNull(9) ? null : reader.GetInt64(9)));
        }
        return rows;
    }

    /// <summary>
    /// Idempotent dismiss. Returns true if a previously-active row was flipped
    /// to dismissed, false if the row was already dismissed or doesn't exist.
    /// The boolean is for diagnostics only — the IPC contract treats
    /// double-dismiss as silent success per brief §3.5 ("one click, no
    /// confirm").
    /// </summary>
    public bool Dismiss(long alertId, long nowUnixMs)
    {
        if (alertId <= 0) return false;

        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE alerts
               SET acknowledged_at = $now
             WHERE alert_id = $id
               AND acknowledged_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$now", nowUnixMs);
        cmd.Parameters.AddWithValue("$id",  alertId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Diagnostic / test helper. Mirrors the gate <see cref="TryInsert"/>
    /// applies inside its INSERT statement — returns true when an existing
    /// row matches AND is either still active or was dismissed within the
    /// cooldown window. Production code generally doesn't need this because
    /// <see cref="TryInsert"/> bakes the check into the write; exposed for
    /// rule and integration tests that want to assert dedupe behavior
    /// without performing the insert.
    /// </summary>
    public bool IsActiveOrCoolingDown(
        string type, string entityKind, string entityRef,
        long nowUnixMs, long cooldownMs)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM alerts
             WHERE type        = $type
               AND entity_kind = $kind
               AND entity_ref  = $ref
               AND (acknowledged_at IS NULL
                    OR acknowledged_at >= $now - $cooldown)
             LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$type",     type);
        cmd.Parameters.AddWithValue("$kind",     entityKind);
        cmd.Parameters.AddWithValue("$ref",      entityRef);
        cmd.Parameters.AddWithValue("$now",      nowUnixMs);
        cmd.Parameters.AddWithValue("$cooldown", cooldownMs);
        return cmd.ExecuteScalar() is not null;
    }
}

/// <summary>
/// Producer-side payload for <see cref="AlertsRepository.TryInsert"/>. All
/// fields are the storage-string forms of the contract enums; the conversion
/// happens at the producer/repository boundary so the repo stays
/// enum-agnostic and the schema's TEXT columns aren't litigated per call.
/// </summary>
public sealed record NewAlert(
    string Type,
    string Severity,
    string SourceMonitor,
    string EntityKind,
    string EntityRef,
    string Title,
    string Detail);

/// <summary>
/// Row shape returned by <see cref="AlertsRepository.Query"/>. The IPC
/// handler wraps this with the AlertDto enum conversions and AppId derivation
/// before handing it to the wire.
/// </summary>
public sealed record AlertRow(
    long AlertId,
    string Type,
    string Severity,
    long CreatedAtUnixMs,
    string SourceMonitor,
    string EntityKind,
    string EntityRef,
    string Title,
    string Detail,
    long? AcknowledgedAtUnixMs);
