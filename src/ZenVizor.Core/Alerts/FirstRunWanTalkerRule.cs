// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Fires when a newly-created app opens its first WAN connection within
/// <see cref="FirstRunWindowMs"/> of being first observed. The signal
/// is "new program reached the internet shortly after install / first
/// launch" — useful for catching freshly-dropped binaries, undocumented
/// installer phone-homes, and similar "first-contact" patterns.
/// Severity Info per catalog §1.4 — informational, not alarming.
/// <para>
/// Cooldown is effectively permanent per app
/// (<see cref="long.MaxValue"/>/2). The rule is by definition a one-shot
/// per app's lifetime: "first run" only happens once. Re-arming on
/// dismiss is not desired — a dismissed first-run alert is the user
/// saying "I've seen this, move on."
/// </para>
/// </summary>
public sealed class FirstRunWanTalkerRule : IAlertRule
{
    /// <summary>
    /// Window between <c>apps.first_seen</c> and the qualifying WAN
    /// connection. 60 s default — long enough to catch installer
    /// phone-homes that happen seconds after a fresh binary lands,
    /// short enough that a long-running app doesn't re-qualify after
    /// it's been quiet for hours.
    /// </summary>
    public static readonly long FirstRunWindowMs = (long)TimeSpan.FromSeconds(60).TotalMilliseconds;

    /// <summary>
    /// Post-install settling window (Epic B, 1.2.0). Any app whose
    /// <c>first_seen</c> falls inside <c>install_epoch + BaselineWindowMs</c>
    /// is treated as pre-existing on this machine and does NOT trip the
    /// first-run rule. Corrects the day-one false-positive flood
    /// (Chrome, Teams, svchost, etc. that already lived on the machine
    /// but get a fresh <c>first_seen</c> the moment ZenVizor first
    /// observes them). Const — user data would need to show that
    /// long-tail installers keep unpacking past 48 h before this
    /// becomes tunable.
    /// </summary>
    public static readonly long BaselineWindowMs = (long)TimeSpan.FromHours(48).TotalMilliseconds;

    private readonly long _installEpochUnixMs;

    /// <summary>
    /// Constructs the rule with an install-epoch anchor. Zero disables
    /// the baseline gate (test paths, first-boot before the epoch key
    /// is written); a positive value gates raises inside the
    /// <see cref="BaselineWindowMs"/> settling window.
    /// </summary>
    public FirstRunWanTalkerRule(long installEpochUnixMs = 0)
    {
        _installEpochUnixMs = installEpochUnixMs;
    }

    /// <summary>
    /// Effectively-never cooldown. The rule fires once per app for the
    /// app's lifetime; if dismissed, it stays dismissed.
    /// </summary>
    public long CooldownMs => long.MaxValue / 2;

    public RaiseRequest? TryEvaluate(NewSessionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.WanConnection is null)
            return null;
        // AppFirstSeenUnixMs == 0 means the producer's lookup didn't
        // resolve the row. Treat as "no first-seen known" — silent.
        if (ctx.AppFirstSeenUnixMs <= 0)
            return null;

        // Epic B baseline gate — raise-gate for false-positive first-runs
        // (apps demonstrably present at install, either setup-scan-seeded
        // or genuinely first-observed inside the 48 h post-install
        // settling window). Not-raising here is *correct attribution* —
        // the app is not actually new — not a suppressed audit trail.
        if (_installEpochUnixMs > 0 &&
            ctx.AppFirstSeenUnixMs <= _installEpochUnixMs + BaselineWindowMs)
            return null;

        var ageMs = ctx.FlushTimeUnixMs - ctx.AppFirstSeenUnixMs;
        if (ageMs < 0 || ageMs > FirstRunWindowMs)
            return null;

        return new RaiseRequest(
            Type:          AlertType.FirstRunWanTalker,
            Severity:      NotableSeverity.Info,
            SourceMonitor: SourceMonitor.Capture,
            EntityKind:    AlertEntityKind.App,
            EntityRef:     ctx.AppId.ToString(CultureInfo.InvariantCulture),
            AppId:         ctx.AppId,
            Title:         $"Newly-installed program reached the network: {ctx.ImageName}");
    }

    public string RenderDetail(NewSessionContext ctx, int connectionCount)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (connectionCount <= 0) connectionCount = 1;

        var firstSeenLocal =
            DateTimeOffset.FromUnixTimeMilliseconds(ctx.AppFirstSeenUnixMs)
                          .LocalDateTime
                          .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var firstConnectionLocal =
            DateTimeOffset.FromUnixTimeMilliseconds(ctx.WanConnection.FirstSeenUnixMs)
                          .LocalDateTime
                          .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var ageSeconds = Math.Max(0L,
            (ctx.WanConnection.FirstSeenUnixMs - ctx.AppFirstSeenUnixMs) / 1000);

        return
            $"{ctx.ImageName} was first observed at {firstSeenLocal} and opened its " +
            $"first network connection at {firstConnectionLocal} ({ageSeconds} s after first observed). " +
            $"Image path: {ctx.ImagePath}. " +
            $"Connections so far: {connectionCount}.";
    }
}
