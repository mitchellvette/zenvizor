namespace ZenVizor.Core.Attribution;

/// <summary>
/// Where an image lives, for the Phase-6 unsigned-binary alert. The third
/// state (<see cref="Unknown"/>) is load-bearing: ETW basename-only
/// attributions used to collapse to <see cref="System"/>, which made a
/// downloaded payload running with no full-path attribution look identical
/// to a signed system binary. CLAUDE.md's "never fabricate precision":
/// unknown reads as unknown.
/// </summary>
public enum PathClassification
{
    /// <summary>
    /// Path lives outside any known user-writable root (Program Files,
    /// System32, vendor app dirs).
    /// </summary>
    System = 0,

    /// <summary>
    /// Path is under a user-writable root (a user profile,
    /// <c>%ProgramData%</c>, <c>%TEMP%</c>, <c>%PUBLIC%</c>).
    /// </summary>
    UserWritable = 1,

    /// <summary>
    /// Path is unrooted (basename only) or otherwise unresolvable. We have
    /// NO evidence about where this image lives — downstream alert
    /// consumers MUST NOT treat it as <see cref="System"/>.
    /// </summary>
    Unknown = 2,
}

public static class PathClassificationExtensions
{
    /// <summary>
    /// Stable string form persisted to <c>apps.path_class</c>. Matches the
    /// schema-default literal in <c>005_phase2_path_class.sql</c> so legacy
    /// rows compare equal to <see cref="PathClassification.System"/>.
    /// </summary>
    public static string ToStorageString(this PathClassification value) => value switch
    {
        PathClassification.System => "System",
        PathClassification.UserWritable => "UserWritable",
        PathClassification.Unknown => "Unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static PathClassification FromStorageString(string value) => value switch
    {
        "System" => PathClassification.System,
        "UserWritable" => PathClassification.UserWritable,
        "Unknown" => PathClassification.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
