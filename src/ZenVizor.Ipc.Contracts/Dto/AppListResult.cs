namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Per-app totals over the requested window. Apps with zero bytes in window
/// are excluded; otherwise sorted by total bytes descending server-side.
/// </summary>
public sealed record AppListResult(
    QueryWindow Window,
    IReadOnlyList<AppListEntry> Apps);

public sealed record AppListEntry(
    int AppId,
    string ImageName,
    string ImagePath,
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath,
    long BytesUp,
    long BytesDown,
    long FirstSeenUnixMs,
    long LastSeenUnixMs);
