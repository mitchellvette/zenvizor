namespace ZenVizor.Core.Storage;

/// <summary>
/// The dedup key + display fields for an entry in the <c>apps</c> table.
/// Phase 1 keeps publisher null and signature_status = "Unchecked"; the
/// Phase 2 enrichment will populate them.
/// </summary>
/// <param name="ImagePath">Full normalized image path. Forms part of the dedup key.</param>
/// <param name="ImageName">Filename portion of the image path.</param>
/// <param name="Publisher">Null in Phase 1.</param>
/// <param name="SignatureStatus">"Signed" | "Unsigned" | "Invalid" | "Unchecked".</param>
/// <param name="IsUserWritablePath">1 if image lives under a user-writable location.</param>
public sealed record AppIdentity(
    string ImagePath,
    string ImageName,
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath);
