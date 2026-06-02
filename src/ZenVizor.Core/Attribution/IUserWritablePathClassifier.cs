namespace ZenVizor.Core.Attribution;

/// <summary>
/// Decides whether an image path lives under a user-writable location.
/// Phase 2 Q4: prefix match against a known set of roots
/// (<c>%TEMP%</c>, <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>,
/// <c>%USERPROFILE%\Downloads</c>, <c>%PUBLIC%</c>) — no filesystem
/// ACL syscalls.
/// </summary>
public interface IUserWritablePathClassifier
{
    bool IsUserWritable(string imagePath);
}
