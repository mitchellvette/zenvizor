// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ui.Services;

/// <summary>
/// Strips the SourceLink "+{commit-hash}" suffix off
/// <c>AssemblyInformationalVersion</c> strings so user-facing chrome
/// (Settings About card, bottom-bar service line) reads as clean SemVer
/// instead of "0.1.0+abc1234abcdef...". Diagnostic surfaces (zvctl,
/// log lines) keep the full string by not going through here.
/// </summary>
internal static class VersionFormatter
{
    public static string Display(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";
        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
