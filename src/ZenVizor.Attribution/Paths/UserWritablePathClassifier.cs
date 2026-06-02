using System.Runtime.Versioning;
using ZenVizor.Core.Attribution;

namespace ZenVizor.Attribution.Paths;

/// <summary>
/// Prefix-match classifier for the <c>is_user_writable_path</c> heuristic
/// (Phase 2 Q4). Compares the image path against a precomputed set of
/// user-writable root prefixes — no filesystem ACL syscalls.
/// </summary>
/// <remarks>
/// Service runs as LocalSystem, so reading <c>%TEMP%</c> or <c>%LOCALAPPDATA%</c>
/// out of the process environment would return SYSTEM's directories, not the
/// signed-in user's. Instead we enumerate <c>C:\Users\*</c> and synthesize the
/// per-user roots, plus the system-wide ones.
/// <para>
/// The default-constructor snapshot is taken once at construction (cheap; this
/// instance is shared across enrichments). New user profiles created after the
/// service starts will not be detected until restart — acceptable for the
/// product's threat model, since session creation for those profiles is rare
/// relative to service uptime and the false-negative is low-impact (we'd just
/// fail to flag a binary from a newly created user's AppData).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UserWritablePathClassifier : IUserWritablePathClassifier
{
    private readonly string[] _prefixes;

    public UserWritablePathClassifier()
        : this(EnumerateDefaultPrefixes())
    {
    }

    /// <summary>Test seam: supply an arbitrary prefix set.</summary>
    public UserWritablePathClassifier(IEnumerable<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        _prefixes = prefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The prefixes this classifier matches against. Test diagnostic.</summary>
    public IReadOnlyList<string> Prefixes => _prefixes;

    public bool IsUserWritable(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return false;
        }

        var normalized = NormalizePath(imagePath);
        foreach (var prefix in _prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> EnumerateDefaultPrefixes()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? systemDrive + @"\Windows";

        yield return systemRoot + @"\Temp";
        yield return systemDrive + @"\Temp";

        var publicDir = Environment.GetEnvironmentVariable("PUBLIC") ?? systemDrive + @"\Users\Public";
        yield return publicDir;

        var usersDir = systemDrive + @"\Users";
        if (Directory.Exists(usersDir))
        {
            string[] userDirs;
            try
            {
                userDirs = Directory.GetDirectories(usersDir);
            }
            catch (UnauthorizedAccessException)
            {
                userDirs = Array.Empty<string>();
            }
            catch (IOException)
            {
                userDirs = Array.Empty<string>();
            }

            foreach (var userDir in userDirs)
            {
                // Skip well-known non-user folders that live under C:\Users.
                var leaf = Path.GetFileName(userDir);
                if (string.Equals(leaf, "Public", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leaf, "Default", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leaf, "Default User", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leaf, "All Users", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return Path.Combine(userDir, "AppData");
                yield return Path.Combine(userDir, "Downloads");
            }
        }
    }

    private static string NormalizePath(string path)
    {
        var p = path.Replace('/', '\\');
        // Keep trailing separator off so prefix comparisons work uniformly.
        return p.TrimEnd('\\');
    }

    private static string NormalizePrefix(string prefix)
    {
        var p = NormalizePath(prefix);
        // Force a trailing separator so "C:\Temp" doesn't match "C:\Temporary".
        return p + '\\';
    }
}
