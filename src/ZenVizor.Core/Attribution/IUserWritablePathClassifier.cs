// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Core.Attribution;

/// <summary>
/// Decides whether an image path lives under a user-writable location.
/// Phase 2 Q4: prefix match against a known set of roots
/// (<c>%TEMP%</c>, <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>, the rest of the
/// user profile, <c>%PROGRAMDATA%</c>, <c>%PUBLIC%</c>) — no filesystem
/// ACL syscalls.
/// </summary>
public interface IUserWritablePathClassifier
{
    bool IsUserWritable(string imagePath);

    /// <summary>
    /// Three-state classification. Default implementation maps the
    /// boolean back through <see cref="PathClassification.UserWritable"/>
    /// / <see cref="PathClassification.System"/>; implementations that
    /// can detect unrooted (basename-only) inputs SHOULD override and
    /// return <see cref="PathClassification.Unknown"/> for those — that
    /// is the load-bearing state for the Phase-6 alert.
    /// </summary>
    PathClassification Classify(string imagePath) =>
        IsUserWritable(imagePath)
            ? PathClassification.UserWritable
            : PathClassification.System;
}
