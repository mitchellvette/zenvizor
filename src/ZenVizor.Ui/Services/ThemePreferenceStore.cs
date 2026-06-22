// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Local-disk cache of the user's <c>appearance.theme</c> preference. The
/// canonical row lives in the service-owned <c>settings</c> table; this
/// cache exists so <see cref="App.OnStartup"/> can resolve the theme
/// without blocking the first window paint on a service-pipe handshake.
/// </summary>
/// <remarks>
/// File path: <c>%LocalAppData%\ZenVizor\ui.theme</c>. Contents are a
/// single line — one of <c>system</c> / <c>light</c> / <c>dark</c>. On
/// any read / parse error the store returns <see cref="AppTheme.System"/>
/// so a corrupt cache degrades to the safe default instead of crashing
/// startup. Writes are best-effort fire-and-forget; a failed write means
/// the next launch uses the previous cached value (or <c>System</c> if
/// nothing was ever cached) — service-side reconciliation on first
/// <c>GetSettingsAsync</c> will repair the divergence.
/// </remarks>
internal static class ThemePreferenceStore
{
    private const string CacheFileName = "ui.theme";

    /// <summary>
    /// Returns the cached preference, or <see cref="AppTheme.System"/>
    /// when the cache is absent / unreadable / unparseable.
    /// </summary>
    public static AppTheme Load()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path)) return AppTheme.System;

            var raw = File.ReadAllText(path).Trim();
            return raw.ToLowerInvariant() switch
            {
                "light"  => AppTheme.Light,
                "dark"   => AppTheme.Dark,
                "system" => AppTheme.System,
                _        => AppTheme.System,
            };
        }
        catch
        {
            return AppTheme.System;
        }
    }

    /// <summary>
    /// Overwrites the cache with <paramref name="theme"/>. Best-effort —
    /// IO failures are swallowed (the cache is a hint, not authoritative).
    /// </summary>
    public static void Save(AppTheme theme)
    {
        try
        {
            var path = GetCachePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var token = theme switch
            {
                AppTheme.Light => "light",
                AppTheme.Dark  => "dark",
                _              => "system",
            };
            File.WriteAllText(path, token);
        }
        catch
        {
            // Cache is a hint; service-side row is authoritative.
        }
    }

    private static string GetCachePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ZenVizor", CacheFileName);
    }
}
