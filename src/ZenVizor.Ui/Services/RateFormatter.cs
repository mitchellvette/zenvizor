using System.Globalization;
using System.Runtime.Versioning;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Humanizes a bytes-per-second value to a short user-facing string
/// (`"19.4 KB/s"`, `"1.2 MB/s"`). Single source of truth for rate
/// formatting across the UI — Dashboard chart Y-axis labeler, status-card
/// values, bottom-bar mirror, and talkers list all call through here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RateFormatter
{
    /// <summary>
    /// <c>"19.4 KB/s"</c>-style format. Returns <c>"0 B/s"</c> for NaN or
    /// non-positive input. One decimal until value &gt;= 100, then no
    /// decimal so the digit count stays bounded (max 5 chars + unit).
    /// </summary>
    public static string FormatRate(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || bytesPerSec <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        var value = bytesPerSec;
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        var formatted = value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return formatted + " " + units[unit];
    }
}
