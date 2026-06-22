// SPDX-License-Identifier: GPL-3.0-or-later

using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Views;

/// <summary>
/// UI-side static lookups for the alerts catalog. Mirrors the brief §3
/// vocabulary and the catalog §1.1/§1.3/§2/§3 source-of-truth tables.
/// <para>
/// Each method is a pure function of the catalog enum — no state, no
/// dependencies — so the per-item DataTemplate can call through to a
/// single instance from any binding context. The display names follow
/// Q7's locked pattern ("describe the observation, not a verdict; plain
/// English; no jargon"); the why-copy blocks are reproduced verbatim
/// from brief §3 with no em-dashes per the catalog §1.2 vocabulary lock.
/// </para>
/// <para>
/// Future migration target: when the brief's WhyCopyResources.xaml string
/// table lands as a XAML ResourceDictionary, these lookups become thin
/// wrappers around <c>Application.Current.FindResource</c>. For Phase 4
/// the static C# is the simpler form; the lookup contract is stable.
/// </para>
/// </summary>
internal static class AlertCatalogLookups
{
    /// <summary>
    /// User-facing display name per type (Q7 lock + catalog §3 vocabulary).
    /// </summary>
    public static string DisplayName(AlertType type) => type switch
    {
        AlertType.UnsignedFromUserPath => "Unsigned from user folder",
        AlertType.InvalidSignature     => "Signature does not verify",
        AlertType.FirstRunWanTalker    => "First-time program reached network",
        AlertType.UnusualDailyVolume   => "Higher-than-usual data use",
        AlertType.LargeDownload        => "Large download in progress",
        AlertType.OutboundHeavy        => "Uploads dominated downloads",
        _ => type.ToString(),
    };

    /// <summary>
    /// User-facing label for the producer that raised the alert (catalog
    /// §1.3). The raw <see cref="SourceMonitor"/> value never reaches the
    /// UI; this lookup is the contract.
    /// </summary>
    public static string SourceLabel(SourceMonitor source) => source switch
    {
        SourceMonitor.Capture => "Capture",
        SourceMonitor.Rollup  => "Daily check",
        _ => source.ToString(),
    };

    /// <summary>
    /// Per-type SymbolIcon glyph for the 48px severity tile (brief §18
    /// flagged as provisional — individual icon choices iterate during
    /// validation; the lookup contract is the lock).
    /// </summary>
    public static SymbolRegular Icon(AlertType type) => type switch
    {
        AlertType.UnsignedFromUserPath => SymbolRegular.ShieldDismiss24,
        AlertType.InvalidSignature     => SymbolRegular.ShieldError24,
        AlertType.FirstRunWanTalker    => SymbolRegular.Sparkle24,
        AlertType.UnusualDailyVolume   => SymbolRegular.ArrowTrendingLines24,
        AlertType.LargeDownload        => SymbolRegular.ArrowDownload24,
        AlertType.OutboundHeavy        => SymbolRegular.ArrowUpload24,
        _ => SymbolRegular.Alert24,
    };

    /// <summary>
    /// "Why this matters" static framing block per type (brief §3, rendered
    /// verbatim). Plain English, no jargon, no em-dashes. Sourced from the
    /// catalog §2/§3 entries.
    /// </summary>
    public static string WhyMatters(AlertType type) => type switch
    {
        AlertType.UnsignedFromUserPath =>
            "An unsigned program is making network connections from a folder you can write to (Temp, AppData, Downloads, or similar). " +
            "This pattern shows up in installers, updater stubs, and small utilities; it also shows up in malware that uses the same folders to avoid attention. " +
            "ZenVizor cannot tell which one this is. The image path and signer below are the facts you can use to decide whether you recognize this program.",
        AlertType.InvalidSignature =>
            "This program was signed by its publisher, but the signature does not verify. " +
            "The binary may have been modified after signing, the certificate chain may be broken, or the certificate may have expired in a way the OS cannot resolve. " +
            "An invalid signature is a stronger signal than no signature at all and is worth examining before you keep running the program.",
        AlertType.FirstRunWanTalker =>
            "ZenVizor noticed this program for the first time and it has already made a network connection. " +
            "Most installed software phones home on first run. " +
            "This alert exists so you can spot the case where the program is one you do not remember installing.",
        AlertType.UnusualDailyVolume =>
            "One of your programs moved noticeably more data today than its typical day for the past two weeks. " +
            "Streaming sessions, big game patches, large cloud-sync runs, and runaway updaters all look like this. " +
            "Open the program's detail to see when the spike happened and which endpoints it talked to.",
        AlertType.LargeDownload =>
            "One of your programs just pulled down a large download. " +
            "Auto-updates for browsers, system components, and game launchers usually look like this. " +
            "This alert exists so you can spot the case where an update happened that you did not ask for or did not expect.",
        AlertType.OutboundHeavy =>
            "One of your programs sent out a lot more data than it pulled in today. " +
            "Backup clients, cloud-sync, and video-call apps legitimately look like this. " +
            "The pattern is also what data exfiltration looks like, so it is worth confirming the program is one you expect to be uploading.",
        _ => string.Empty,
    };

    /// <summary>
    /// User-facing severity label ("Critical" / "Warning" / "Info") for
    /// badges, filter checkboxes, and KPI eyebrows. Matches the catalog
    /// §1.4 vocabulary exactly.
    /// </summary>
    public static string SeverityDisplayName(NotableSeverity sev) => sev switch
    {
        NotableSeverity.Critical => "Critical",
        NotableSeverity.Warning  => "Warning",
        NotableSeverity.Info     => "Info",
        _ => sev.ToString(),
    };
}
