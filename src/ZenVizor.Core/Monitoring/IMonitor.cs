// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Core.Monitoring;

/// <summary>
/// Seam #1 (PRD §6). The collector/monitor contract: a long-lived component
/// that observes some local source and emits typed observations into the rest
/// of the system. Phase 1's only implementation wraps an
/// <see cref="ZenVizor.Capture.ICaptureSource"/> — future passive watchers
/// (hosts file, proxy settings, ARP cache) slot in here without core changes.
/// </summary>
public interface IMonitor
{
    /// <summary>A stable identifier used for logs, alerts, and diagnostics.</summary>
    string Name { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
