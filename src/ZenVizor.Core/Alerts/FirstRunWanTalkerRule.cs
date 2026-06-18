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
