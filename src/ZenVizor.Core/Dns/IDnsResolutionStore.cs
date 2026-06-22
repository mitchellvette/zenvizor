// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace ZenVizor.Core.Dns;

/// <summary>
/// Read-side contract for the passive DNS observer's IP → hostname store.
/// <see cref="ZenVizor.Core.Aggregation.TrafficAggregator"/> depends on this
/// interface (not the concrete store) so tests can substitute a fake without
/// pulling in the LRU/TTL machinery.
/// </summary>
public interface IDnsResolutionStore
{
    /// <summary>
    /// Returns true and sets <paramref name="hostname"/> when the store holds
    /// an unexpired mapping for <paramref name="ip"/>. Lookup does not mutate
    /// the underlying LRU — see <see cref="DnsResolutionStore"/> for the
    /// rationale.
    /// </summary>
    bool TryGetHostname(IPAddress ip, long nowUnixMs, out string hostname);
}
