// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// One alert row, as the service hands it to the UI. Mirrors the persisted
/// <c>alerts</c> table per PRD §7.6, with one user-facing-vs-internal split:
/// the database column is <c>acknowledged_at</c> (Phase 6 schema, locked
/// for no migration) but every visible string, IPC method, and CLI
/// subcommand uses the "dismiss" vocabulary per catalog §1.2 and brief §3.5.
/// <para>
/// <see cref="Severity"/> reuses <see cref="NotableSeverity"/> — that enum
/// was originally authored for the Reports Notable surface but its docstring
/// explicitly notes it inherits the Alerts entity's severity vocabulary,
/// so reuse keeps a single source of truth for the locked severity-to-token
/// mapping (Info → status.neutral, Warning → status.caution, Critical →
/// status.critical) across both surfaces.
/// </para>
/// <para>
/// <see cref="EntityRef"/> shape varies by <see cref="EntityKind"/>: for
/// <see cref="AlertEntityKind.App"/> it's <c>app_id.ToString()</c>; for
/// <see cref="AlertEntityKind.Session"/> it's <c>session_id.ToString()</c>.
/// Stored as a string so the post-MVP <c>Device</c> / <c>File</c> entity
/// kinds (PRD §7.6 reserved) can carry richer references without schema
/// churn.
/// </para>
/// <para>
/// <see cref="AppId"/> carries the parent app reference for alerts whose
/// primary <see cref="EntityKind"/> isn't <see cref="AlertEntityKind.App"/>
/// (most commonly Session-scoped alerts). It lets the UI offer a
/// "View app" drill on session-scoped alerts without an extra
/// session→app lookup round-trip. Null when no app context applies (e.g.
/// future Device-scoped alerts). For App-scoped alerts it's redundant
/// with <see cref="EntityRef"/> and producers may leave it null; the UI
/// falls back to parsing EntityRef for that case.
/// </para>
/// </summary>
public sealed record AlertDto(
    long AlertId,
    AlertType Type,
    NotableSeverity Severity,
    long CreatedAtUnixMs,
    SourceMonitor Source,
    AlertEntityKind EntityKind,
    string EntityRef,
    string Title,
    string Detail,
    long? AcknowledgedAtUnixMs,
    int? AppId = null);
