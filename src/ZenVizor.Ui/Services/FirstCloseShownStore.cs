// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Tracks whether the "ZenVizor is still running in the tray" balloon
/// notification has already been shown to this user. Pure existence-check
/// against a local marker file — the contents are irrelevant.
/// </summary>
/// <remarks>
/// File path: <c>%LocalAppData%\ZenVizor\ui.first-close-shown</c>. The
/// hint is per-user UI state, not service-side configuration, so it lives
/// locally instead of in the settings DB (no IPC round-trip on close).
/// Read errors fall through as "not shown yet" — at worst a returning
/// user sees the balloon twice; far less alarming than missing it on a
/// fresh install.
/// </remarks>
internal static class FirstCloseShownStore
{
    private const string MarkerFileName = "ui.first-close-shown";

    /// <summary>True when the balloon has already been shown.</summary>
    public static bool HasBeenShown()
    {
        try
        {
            return File.Exists(GetMarkerPath());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the marker so subsequent close-to-tray actions stay silent.
    /// Best-effort; a write failure means the next close will re-emit the
    /// balloon, which is recoverable.
    /// </summary>
    public static void MarkShown()
    {
        try
        {
            var path = GetMarkerPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, string.Empty);
        }
        catch
        {
            // Marker is a hint; worst case the user sees the balloon twice.
        }
    }

    private static string GetMarkerPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ZenVizor", MarkerFileName);
    }
}
