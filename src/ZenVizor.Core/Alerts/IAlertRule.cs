namespace ZenVizor.Core.Alerts;

/// <summary>
/// One alert-evaluation rule. Pure: <see cref="TryEvaluate"/> + <see cref="RenderDetail"/>
/// take a <see cref="NewSessionContext"/> and return data — no DB writes, no
/// network, no shared mutable state. The producer is the rule's only caller
/// and owns dedupe / cooldown / connection-count state on the rule's behalf.
/// <para>
/// <see cref="CooldownMs"/> is the per-rule dedupe window. Once a rule's
/// alert for a given <c>(EntityKind, EntityRef)</c> is dismissed, the same
/// rule cannot raise a fresh alert for that entity until cooldown elapses.
/// Brief §13 lock for <see cref="UnsignedFromUserPathRule"/> is 24 hours.
/// </para>
/// </summary>
public interface IAlertRule
{
    /// <summary>
    /// Cooldown after dismiss before this rule can re-raise for the same
    /// entity. Per-rule because severity tiers warrant different cadences:
    /// a critical alert might allow only 24h cooldown; an info one might
    /// allow shorter.
    /// </summary>
    long CooldownMs { get; }

    /// <summary>
    /// Returns a <see cref="RaiseRequest"/> if this rule fires against the
    /// supplied context; null otherwise. Implementations are pure and
    /// side-effect free — the producer decides whether the request actually
    /// lands in storage (dedupe / cooldown gate).
    /// </summary>
    RaiseRequest? TryEvaluate(NewSessionContext ctx);

    /// <summary>
    /// Renders the user-facing <c>detail</c> body for this rule's alert
    /// given a session context and a current connection count. Called twice
    /// per alert lifecycle: once on initial raise (count=1), once per
    /// subsequent qualifying observation (count++).
    /// <para>
    /// The producer is responsible for caching the original first-seen
    /// timestamp from the initial raise and synthesizing a context with
    /// the same timestamp on subsequent renders, so a rule's template that
    /// reads <c>ctx.WanConnection.FirstSeenUnixMs</c> produces a stable
    /// "First connection: …" phrase across the alert's lifetime.
    /// </para>
    /// </summary>
    string RenderDetail(NewSessionContext ctx, int connectionCount);
}
