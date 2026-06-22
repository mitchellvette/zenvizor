// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// The single-select State filter axis on the Alerts feed (brief §3.7).
/// Server-side filter — applied in <c>GetAlertsAsync</c> before returning;
/// the UI's Severity and Type axes are client-side (in-memory) per brief §14.
/// </summary>
public enum AlertState
{
    /// <summary>
    /// Undismissed alerts only. Default filter — the "is there anything to
    /// look at right now" landing view.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Dismissed alerts that have not yet aged out per retention
    /// (dismissed + 90 days by default, configurable per PRD §7.9).
    /// </summary>
    Dismissed = 1,

    /// <summary>
    /// Both active and dismissed in one view; dismissed rows are visually
    /// demoted at the per-item template level.
    /// </summary>
    All = 2,
}
