// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ZenVizor.Core.Attribution;

namespace ZenVizor.Attribution.Paths;

/// <summary>
/// Prefix-match classifier for the <c>is_user_writable_path</c> heuristic
/// (Phase 2 Q4). Compares the image path against a precomputed set of
/// user-writable root prefixes — no filesystem ACL syscalls.
/// </summary>
/// <remarks>
/// <para>
/// Service runs as LocalSystem, so reading <c>%TEMP%</c> or <c>%LOCALAPPDATA%</c>
/// out of the process environment would return SYSTEM's directories, not the
/// signed-in user's. Instead we enumerate <c>C:\Users\*</c> and synthesize the
/// per-user roots, plus the system-wide ones.
/// </para>
/// <para>
/// Per-user coverage is the WHOLE profile root (e.g. <c>C:\Users\alice</c>),
/// excluding Public/Default/Default User/All Users. Earlier revisions covered
/// only AppData + Downloads; that missed Desktop, Documents, OneDrive-synced
/// folders, Roaming-shimmed locations, and any custom subdirectory a user can
/// drop a payload into. CLAUDE.md's "never fabricate precision" applies: if
/// we say a binary outside a user profile is "not user-writable", that bit
/// has to be load-bearing for the Phase-6 alert.
/// </para>
/// <para>
/// <c>%ProgramData%</c> is included because its default ACL grants
/// CREATOR-OWNER write to subdirectories (vendors install per-machine state
/// here, including writable spool dirs), so it is a routine drop location.
/// </para>
/// <para>
/// The default-constructor snapshot is taken once at construction (cheap; this
/// instance is shared across enrichments). New user profiles created after the
/// service starts will not be detected until restart — acceptable for the
/// product's threat model, since session creation for those profiles is rare
/// relative to service uptime and the false-negative is low-impact (we'd just
/// fail to flag a binary from a newly created user's profile).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UserWritablePathClassifier : IUserWritablePathClassifier
{
    private const string LongPathPrefix    = @"\\?\";
    private const string LongUncPathPrefix = @"\\?\UNC\";

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

    public bool IsUserWritable(string imagePath) =>
        Classify(imagePath) == PathClassification.UserWritable;

    public PathClassification Classify(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return PathClassification.Unknown;
        }

        var rough = StripLongPathPrefixes(imagePath.Replace('/', '\\'));
        if (!Path.IsPathRooted(rough))
        {
            // Basename or relative path: ETW handed us an ImageFileName the
            // capture-side couldn't promote to a full path. Honest answer:
            // we don't know where this lives. Downstream alert logic must
            // not treat this as "safe" (CLAUDE.md "never fabricate precision").
            return PathClassification.Unknown;
        }

        var normalized = NormalizeRooted(rough);
        foreach (var prefix in _prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return PathClassification.UserWritable;
            }
        }
        return PathClassification.System;
    }

    private static IEnumerable<string> EnumerateDefaultPrefixes()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? systemDrive + @"\Windows";

        yield return systemRoot + @"\Temp";
        yield return systemDrive + @"\Temp";

        var publicDir = Environment.GetEnvironmentVariable("PUBLIC") ?? systemDrive + @"\Users\Public";
        yield return publicDir;

        // %ProgramData% defaults to C:\ProgramData. CREATOR-OWNER write on
        // subdirectories is the default ACL, so vendors and per-machine
        // services routinely write there; it is a routine drop location.
        var programData = Environment.GetEnvironmentVariable("ProgramData")
            ?? systemDrive + @"\ProgramData";
        yield return programData;

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

                // The whole profile root: Desktop, Documents, OneDrive,
                // Roaming, AppData, Downloads, custom subdirs — anything
                // the signed-in user can drop a payload into.
                yield return userDir;
            }
        }
    }

    /// <summary>
    /// Normalize a rooted path so prefix matching is robust against:
    /// the <c>\\?\</c> long-path prefix, mixed separators, embedded
    /// <c>.</c>/<c>..</c> traversals, and 8.3 short names
    /// (e.g. <c>C:\Users\MITCH~1\AppData\bad.exe</c>).
    /// </summary>
    private static string NormalizeRooted(string path)
    {
        // GetFullPath also strips the long-path prefix and collapses
        // separators; we did our own strip first because GetFullPath's
        // behavior with the prefix differs between framework versions.
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            full = path;
        }

        full = ExpandLongPathName(full);
        return full.TrimEnd('\\');
    }

    /// <summary>
    /// Win32 <c>GetLongPathNameW</c> wrapper that expands 8.3 short names
    /// to their full LFN form. Returns the input unchanged when the path
    /// doesn't exist (Win32 returns 0) — we want to attempt the prefix
    /// match anyway because the classifier is informational, not gating.
    /// </summary>
    private static string ExpandLongPathName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        try
        {
            var buffer = new StringBuilder(path.Length + 16);
            var needed = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
            if (needed == 0)
            {
                return path;
            }
            if (needed > buffer.Capacity)
            {
                buffer = new StringBuilder((int)needed);
                needed = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
                if (needed == 0)
                {
                    return path;
                }
            }
            return buffer.ToString();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return path;
        }
    }

    private static string StripLongPathPrefixes(string path)
    {
        if (path.StartsWith(LongUncPathPrefix, StringComparison.Ordinal))
        {
            return @"\\" + path.Substring(LongUncPathPrefix.Length);
        }
        if (path.StartsWith(LongPathPrefix, StringComparison.Ordinal))
        {
            return path.Substring(LongPathPrefix.Length);
        }
        return path;
    }

    private static string NormalizePrefix(string prefix)
    {
        var rough = StripLongPathPrefixes(prefix.Replace('/', '\\'));
        if (Path.IsPathRooted(rough))
        {
            rough = NormalizeRooted(rough);
        }
        else
        {
            rough = rough.TrimEnd('\\');
        }
        // Force a trailing separator so "C:\Temp" doesn't match "C:\Temporary".
        return rough + '\\';
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
}
