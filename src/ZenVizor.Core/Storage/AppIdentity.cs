// SPDX-License-Identifier: GPL-3.0-or-later

using ZenVizor.Core.Attribution;

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
    bool IsUserWritablePath)
{
    /// <summary>
    /// Three-state path classification persisted to <c>apps.path_class</c>.
    /// Init-only with a <see cref="PathClassification.System"/> default so
    /// positional-ctor callers (mostly tests) keep compiling; the AppEnricher
    /// / SessionTracker pipeline sets it explicitly via <c>with</c>.
    /// Bug-2 follow-up: distinguishes "we know this is in a system folder"
    /// from "we have no idea where this binary lives" (basename-only ETW
    /// attribution). The latter MUST NOT collapse to <c>System</c> downstream.
    /// </summary>
    public PathClassification PathClass { get; init; } = PathClassification.System;
}
