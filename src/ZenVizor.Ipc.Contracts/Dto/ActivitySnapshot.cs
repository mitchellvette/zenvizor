// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// A point-in-time view of per-app network activity, served from the in-memory
/// aggregate (no SQLite read). The window covers the previous completed flush
/// bucket plus whatever has accumulated since — typically 5–10 s.
/// </summary>
/// <param name="CapturedAtUnixMs">Server wall-clock at the moment of capture.</param>
/// <param name="WindowSeconds">
/// Total window the rates were computed over: completed bucket span + partial
/// elapsed since the last flush. Zero before the first flush has completed.
/// </param>
/// <param name="Apps">
/// Per-app rows for every app with non-zero bytes in the window. Unordered;
/// callers (UI / CLI) take their own top-N for display.
/// </param>
/// <param name="WanLocalBreakdown">
/// Aggregate byte totals split by remote-address classification (WAN vs
/// LAN/loopback) over the same window. Sum equals the sum of per-app
/// <see cref="AppActivity.BytesUpTotal"/> / <see cref="AppActivity.BytesDownTotal"/>
/// across the snapshot. Always present; an empty snapshot returns
/// <see cref="ClassBreakdown.Empty"/>.
/// </param>
public sealed record ActivitySnapshot(
    long CapturedAtUnixMs,
    double WindowSeconds,
    IReadOnlyList<AppActivity> Apps,
    ClassBreakdown WanLocalBreakdown);
