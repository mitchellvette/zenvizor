// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// A half-open time range <c>[FromUnixMs, ToUnixMs)</c> for history queries.
/// UTC unix-ms throughout (per Phase 4 Q5); the UI converts to local time
/// for display only.
/// </summary>
public sealed record QueryWindow(long FromUnixMs, long ToUnixMs)
{
    /// <summary>Span of the window in milliseconds. Never negative.</summary>
    public long SpanMs => Math.Max(0, ToUnixMs - FromUnixMs);
}
