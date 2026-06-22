// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Storage abstraction the producer writes through. Production wires this to
/// the <c>AlertsRepository</c> via an adapter in <c>ZenVizor.Service</c>;
/// tests substitute an in-memory fake to assert dedupe / cooldown / update
/// behavior without standing up SQLite.
/// <para>
/// Keeping the sink in <c>ZenVizor.Core</c> with no reference to
/// <c>ZenVizor.Storage</c> preserves Core's headless-testable invariant —
/// rule + producer tests can run on CI without a DB file.
/// </para>
/// </summary>
public interface IAlertSink
{
    /// <summary>
    /// Dedupe-gated insert. Returns the new alert id, or 0 if an active or
    /// cooling-down row already exists for the same
    /// <c>(type, entity_kind, entity_ref)</c>. Idempotent and race-free —
    /// the underlying SQL uses <c>WHERE NOT EXISTS</c> to gate on the same
    /// connection.
    /// </summary>
    long TryInsert(string type, string severity, string sourceMonitor,
                   string entityKind, string entityRef,
                   string title, string detail,
                   long nowUnixMs, long cooldownMs);

    /// <summary>
    /// In-place body update for an active alert matching
    /// <c>(type, entity_kind, entity_ref)</c>. Returns the number of rows
    /// updated (0 or 1). Used by the producer to refresh the
    /// "Connections so far: N" phrase as the same app generates more
    /// qualifying observations — the row's title and dedupe key are
    /// immutable, only the body mutates.
    /// </summary>
    int UpdateDetail(string type, string entityKind, string entityRef, string detail);
}
