namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Aggregate byte totals for the snapshot window, split by remote-address
/// classification (WAN vs LAN/loopback). Powers the Dashboard's WAN-vs-LOCAL
/// status card.
/// <para>
/// Values are window-cumulative bytes, NOT rates — the UI computes the
/// WAN/Local ratio (and rates if needed) from these against
/// <see cref="ActivitySnapshot.WindowSeconds"/>.
/// </para>
/// <para>
/// Per-app classification is intentionally NOT exposed here: the underlying
/// <c>SampleKey</c> in the aggregator IS keyed by class, but the Dashboard
/// only needs the aggregate. If per-app split becomes a future requirement,
/// it goes on <see cref="AppActivity"/> in a separate contract bump.
/// </para>
/// </summary>
public sealed record ClassBreakdown(
    long WanBytesUp,
    long WanBytesDown,
    long LocalBytesUp,
    long LocalBytesDown)
{
    /// <summary>An all-zero breakdown, returned by empty snapshots.</summary>
    public static readonly ClassBreakdown Empty = new(0, 0, 0, 0);
}
