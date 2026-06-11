namespace ZenVizor.Core.Attribution;

/// <summary>
/// The Phase 2 enrichment slots: signature verdict, publisher (subject CN of the
/// signing cert), and the user-writable-path bit. Built once per
/// <c>(image_path, mtime, size)</c> by <see cref="IAppEnricher"/> and copied
/// into <see cref="Storage.AppIdentity"/>.
/// </summary>
/// <param name="Publisher">
/// Subject CN of the signing certificate when <paramref name="SignatureStatus"/>
/// is <c>"Signed"</c>; otherwise <c>null</c>.
/// </param>
/// <param name="SignatureStatus">
/// One of <c>"Signed"</c>, <c>"Unsigned"</c>, <c>"Invalid"</c>, <c>"Unchecked"</c>.
/// </param>
/// <param name="IsUserWritablePath">
/// True if the image lives under a user-writable location (TEMP / AppData /
/// Downloads / Public). Preserved for backwards compatibility with the
/// boolean column on the <c>apps</c> table; new consumers SHOULD prefer
/// <see cref="PathClass"/>, which distinguishes <see cref="PathClassification.Unknown"/>.
/// </param>
public sealed record EnrichmentResult(
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath)
{
    /// <summary>
    /// Three-state path classification. Set explicitly by
    /// <see cref="IAppEnricher"/>; init-only with a <see cref="PathClassification.System"/>
    /// default so legacy positional-ctor callers in tests still compile.
    /// The static <see cref="Unchecked"/> sentinel is
    /// <see cref="PathClassification.Unknown"/> because the AppEnricher
    /// returns it precisely when it can't stat the image (basename-only
    /// attribution being the canonical case).
    /// </summary>
    public PathClassification PathClass { get; init; } = PathClassification.System;

    public static EnrichmentResult Unchecked { get; } =
        new(Publisher: null, SignatureStatus: "Unchecked", IsUserWritablePath: false)
        {
            PathClass = PathClassification.Unknown,
        };
}
