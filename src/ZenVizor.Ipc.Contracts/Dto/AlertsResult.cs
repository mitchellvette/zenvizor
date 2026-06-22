// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// <c>GetAlertsAsync</c> payload. Echoes the resolved
/// <see cref="AlertsFilter"/> so the UI can confirm which axis values
/// the server filtered against; <see cref="Alerts"/> carries the matched
/// rows ordered server-side reverse-chronologically (newest first, per
/// brief §11 discovery-over-ranking).
/// <para>
/// <see cref="HasMore"/> signals that the server truncated at
/// <see cref="AlertsFilter.MaxRows"/>. The UI doesn't currently paginate
/// — the alerts feed is meant to be viewable in one virtualized scroll
/// — but the flag exists so a future "show all dismissed" path can
/// surface a "load more" affordance without an envelope-schema bump.
/// </para>
/// </summary>
public sealed record AlertsResult(
    AlertsFilter Filter,
    IReadOnlyList<AlertDto> Alerts,
    bool HasMore);
