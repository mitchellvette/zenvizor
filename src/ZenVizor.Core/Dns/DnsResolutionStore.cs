// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace ZenVizor.Core.Dns;

/// <summary>
/// In-memory IP → hostname store populated by the passive DNS observer
/// (Phase 8). Read at flush time by
/// <see cref="ZenVizor.Core.Aggregation.TrafficAggregator"/> to stamp
/// <c>resolved_host</c> on outgoing connection rows.
/// <para>
/// Storage shape: bounded LRU (<see cref="DefaultCapacity"/> entries by
/// default — same order of magnitude as the Windows DNS cache). On overflow
/// the least-recently-<i>recorded</i> entry is evicted. Lookups do NOT
/// promote in the LRU — promotion is a Record-only operation. Reason: a
/// lookup happens once per connection per flush (read-heavy), and promoting
/// on read would let a stale name linger forever just because something
/// kept talking to its IP.
/// </para>
/// <para>
/// TTL: each entry carries an absolute expiry timestamp (observed + ttl).
/// Expired entries are skipped on lookup and reclaimed by
/// <see cref="EvictExpired"/>, which the DNS source's tick calls
/// periodically.
/// </para>
/// <para>
/// Thread safety: every public member is safe to call from multiple threads.
/// Single mutex; writes are infrequent (DNS event rate is low) so contention
/// is not a concern at the target workload.
/// </para>
/// </summary>
public sealed class DnsResolutionStore : IDnsResolutionStore
{
    /// <summary>
    /// Default LRU cap. Sized close to the Windows DNS resolver cache so a
    /// host that talks to many endpoints over a long uptime doesn't grow the
    /// store unboundedly.
    /// </summary>
    public const int DefaultCapacity = 64 * 1024;

    private readonly int _capacity;
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<IPAddress, LinkedListNode<Entry>> _index;
    private readonly object _gate = new();

    public DnsResolutionStore(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
        _capacity = capacity;
        _index = new Dictionary<IPAddress, LinkedListNode<Entry>>(capacity);
    }

    /// <summary>Current entry count. Diagnostic + test surface.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>Configured LRU cap (constructor argument).</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Insert or overwrite the mapping for <paramref name="ip"/>. An existing
    /// entry moves to the LRU head; a new entry is pushed to the head and the
    /// LRU tail is evicted if we are at capacity. Hostname is stored as-given
    /// (the decoder is responsible for any normalisation).
    /// <para>
    /// Zero or negative <paramref name="ttlSeconds"/> is clamped up to one
    /// second so a TTL=0 response (RFC-permitted; sometimes returned by
    /// load-balancers) survives at least one flush tick instead of
    /// self-expiring on the same millisecond it was recorded.
    /// </para>
    /// </summary>
    public void Record(IPAddress ip, string hostname, int ttlSeconds, long observedAtUnixMs)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        var effectiveTtl = ttlSeconds < 1 ? 1 : ttlSeconds;
        var expiresAt = observedAtUnixMs + ((long)effectiveTtl * 1000L);
        var entry = new Entry(ip, hostname, expiresAt);

        lock (_gate)
        {
            if (_index.TryGetValue(ip, out var existing))
            {
                existing.Value = entry;
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            if (_index.Count >= _capacity)
            {
                var evicted = _order.Last!;
                _order.RemoveLast();
                _index.Remove(evicted.Value.Ip);
            }

            var node = new LinkedListNode<Entry>(entry);
            _order.AddFirst(node);
            _index[ip] = node;
        }
    }

    /// <inheritdoc />
    public bool TryGetHostname(IPAddress ip, long nowUnixMs, out string hostname)
    {
        ArgumentNullException.ThrowIfNull(ip);
        lock (_gate)
        {
            if (_index.TryGetValue(ip, out var node) && node.Value.ExpiresAtUnixMs > nowUnixMs)
            {
                hostname = node.Value.Hostname;
                return true;
            }
        }
        hostname = string.Empty;
        return false;
    }

    /// <summary>
    /// Drop every entry whose absolute expiry timestamp is at or before
    /// <paramref name="nowUnixMs"/>. Returns the count dropped — exposed for
    /// the DNS source's periodic tick logging and for tests.
    /// </summary>
    public int EvictExpired(long nowUnixMs)
    {
        var dropped = 0;
        lock (_gate)
        {
            var node = _order.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.ExpiresAtUnixMs <= nowUnixMs)
                {
                    _order.Remove(node);
                    _index.Remove(node.Value.Ip);
                    dropped++;
                }
                node = next;
            }
        }
        return dropped;
    }

    private readonly record struct Entry(IPAddress Ip, string Hostname, long ExpiresAtUnixMs);
}
