using System.IO;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Local-disk cache of the <c>ui.start_minimized</c> preference. Mirrors
/// the service-owned <c>settings</c> row so <see cref="App.OnStartup"/>
/// can decide whether to drop straight to the tray without blocking the
/// first window paint on the named-pipe handshake.
/// </summary>
/// <remarks>
/// File path: <c>%LocalAppData%\ZenVizor\ui.start-minimized</c>. Contents
/// are a single character: <c>1</c> = minimize on start, anything else
/// (including missing / empty / unreadable) = show normally. Default-false
/// is the safe degradation: a corrupt cache should not silently hide the
/// UI on a manual launch. Writes are best-effort; the service-side row is
/// authoritative and reconciles on the next <c>GetSettingsAsync</c>.
/// </remarks>
internal static class StartMinimizedStore
{
    private const string CacheFileName = "ui.start-minimized";

    /// <summary>
    /// Returns true when the cache says the UI should launch into the
    /// tray. Returns false on any read error, missing file, or unparseable
    /// content — the conservative default that never hides the window
    /// unexpectedly.
    /// </summary>
    public static bool Load()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path)) return false;
            return File.ReadAllText(path).Trim() == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Overwrites the cache with <paramref name="enabled"/>. Best-effort;
    /// the service-side row is authoritative.
    /// </summary>
    public static void Save(bool enabled)
    {
        try
        {
            var path = GetCachePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, enabled ? "1" : "0");
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
