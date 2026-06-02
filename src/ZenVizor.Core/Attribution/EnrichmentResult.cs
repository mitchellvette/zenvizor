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
/// Downloads / Public).
/// </param>
public sealed record EnrichmentResult(
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath)
{
    public static EnrichmentResult Unchecked { get; } =
        new(Publisher: null, SignatureStatus: "Unchecked", IsUserWritablePath: false);
}
