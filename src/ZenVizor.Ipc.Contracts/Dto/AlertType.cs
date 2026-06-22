// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// The catalog of alert types ZenVizor knows how to raise. All six are
/// design vocabulary from day one (alerts catalog §3 + Alerts brief §3) so
/// the UI feed renderer is built once for a heterogeneous catalog; only
/// <see cref="UnsignedFromUserPath"/> has a producer in Phase 6 — the
/// remaining five light up when their rules ship post-MVP.
/// <para>
/// Order is the canonical display order. Severity and source-monitor are
/// not encoded here (each type has fixed locked mappings — see the catalog
/// §1.4 + §2/§3 — but those are static lookups on the UI side, not part of
/// the enum surface).
/// </para>
/// </summary>
public enum AlertType
{
    /// <summary>Critical. Unsigned binary from a user-writable folder making network connections. Phase 6 MVP producer.</summary>
    UnsignedFromUserPath = 0,

    /// <summary>Critical. Signed binary whose signature does not verify.</summary>
    InvalidSignature = 1,

    /// <summary>Info. A newly-created app reached the network within seconds of first-seen.</summary>
    FirstRunWanTalker = 2,

    /// <summary>Warning. An app's daily bytes are robustly above its 14-day baseline (median + MAD).</summary>
    UnusualDailyVolume = 3,

    /// <summary>Info. A single connection pulled down a large download in a short window.</summary>
    LargeDownload = 4,

    /// <summary>Warning. An app's outbound bytes dominate inbound by a configured ratio over the absolute floor.</summary>
    OutboundHeavy = 5,
}
