// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Epic A (1.1.0) — display model for the windowed query pages
/// (<c>PerAppPage</c>, <c>AppDetailPage</c>). Three shapes:
///   * a rolling <see cref="WindowPreset"/> (1h/24h/7d/30d/90d) that
///     recomputes against wall-clock on each <see cref="ToWindow"/> call;
///   * a <i>fixed</i> absolute <see cref="QueryWindow"/> that does not
///     recompute — the deep-link target of the History popover and the
///     output of the user-driven custom-range flyout;
///   * a <i>sentinel</i> "Custom range…" entry — placeholder that triggers
///     the flyout when selected. Has no window of its own (<see cref="ToWindow"/>
///     throws); callers must check <see cref="IsSentinel"/> in the combo's
///     SelectionChanged handler and open the flyout instead of refreshing.
///
/// One record with two discriminating nullables (not an inheritance
/// hierarchy), because the ComboBox <c>ItemTemplate</c> binds to
/// <c>Short</c> + <c>Label</c> directly and WPF reflection-based binding
/// likes a flat shape.
///
/// Designed reusable: Epic I (endpoint lookup) and H (device-peer view)
/// will want the same arbitrary-window abstraction.
/// </summary>
internal sealed record WindowSelection(
    string Short,
    string Label,
    QueryWindow? FixedWindow,
    WindowPreset? Preset)
{
    public bool IsFixed => FixedWindow is not null;

    public bool IsSentinel => FixedWindow is null && Preset is null;

    public QueryWindow ToWindow() =>
        FixedWindow
        ?? Preset?.ToWindow()
        ?? throw new InvalidOperationException(
            "Custom-range sentinel has no window; check IsSentinel before calling ToWindow().");

    public static WindowSelection FromPreset(WindowPreset p) =>
        new(p.Short, p.Label, FixedWindow: null, Preset: p);

    public static WindowSelection FromFixedWindow(QueryWindow w) =>
        new(Short: "Custom", Label: FormatRange(w), FixedWindow: w, Preset: null);

    /// <summary>
    /// Singleton "Custom range…" entry that triggers the custom-range
    /// flyout when selected. Always pinned to the bottom of the combo;
    /// the pages' SelectionChanged handlers special-case it and open the
    /// flyout instead of refreshing.
    /// </summary>
    public static WindowSelection CustomSentinel { get; } =
        new(Short: "Custom range…",
            Label: "Pick a from / to range.",
            FixedWindow: null,
            Preset: null);

    /// <summary>
    /// The 5 rolling presets wrapped once. Combo construction is
    /// <c>Presets.Append(CustomSentinel)</c> in an <c>ObservableCollection</c>
    /// so a real <see cref="FromFixedWindow"/> entry can be inserted at
    /// position 0 in place when the user applies a custom range (or the
    /// History popover deep-links in).
    /// </summary>
    public static IReadOnlyList<WindowSelection> Presets { get; } =
        WindowPreset.All.Select(FromPreset).ToArray();

    private static string FormatRange(QueryWindow w)
    {
        var fromLocal = DateTimeOffset.FromUnixTimeMilliseconds(w.FromUnixMs).LocalDateTime;
        var toLocal   = DateTimeOffset.FromUnixTimeMilliseconds(w.ToUnixMs).LocalDateTime;
        return string.Format(
            CultureInfo.InvariantCulture,
            "Custom range: {0} – {1} ({2})",
            FormatPoint(fromLocal),
            FormatPoint(toLocal),
            FormatSpan(w.SpanMs));
    }

    private static string FormatPoint(DateTime t) =>
        t.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    private static string FormatSpan(long ms)
    {
        if (ms < 60_000)     return $"{ms / 1000} s";
        if (ms < 3_600_000)  return $"{ms / 60_000} min";
        if (ms < 86_400_000) return $"{ms / 3_600_000} h";
        return $"{ms / 86_400_000} d";
    }
}
