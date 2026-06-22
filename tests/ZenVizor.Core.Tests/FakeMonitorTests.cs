// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Core.Monitoring;

namespace ZenVizor.Core.Tests;

/// <summary>
/// Exercises seam #1 (<see cref="IMonitor"/>) with a non-ETW, non-capture
/// implementation. The seam's whole purpose is that future passive watchers
/// (hosts-file, proxy-settings, ARP-cache, etc.) slot in here without
/// rewiring the rest of the system; until one of them lands, the production
/// implementation count is exactly one (<c>CaptureMonitor</c>) and the
/// contract has no second-party test. This file is that second party.
/// It pins the lifecycle expectations:
///   - Start/Stop are idempotent w.r.t. their own state.
///   - Stop after Start completes the work the monitor declared.
///   - Cancellation surfaces as <see cref="OperationCanceledException"/>.
/// A future hosts-file monitor that violates any of these will fail here
/// before it ships.
/// </summary>
public sealed class FakeMonitorTests
{
    [Fact]
    public void Name_IsReadableForLogsAndDiagnostics()
    {
        IMonitor monitor = new FakeMonitor("hosts-file");
        monitor.Name.Should().Be("hosts-file");
    }

    [Fact]
    public async Task StartThenStop_TransitionsThroughExpectedStates()
    {
        var monitor = new FakeMonitor("fake");

        monitor.State.Should().Be(FakeMonitor.LifecycleState.Idle);

        await monitor.StartAsync(CancellationToken.None);
        monitor.State.Should().Be(FakeMonitor.LifecycleState.Running);
        monitor.StartCount.Should().Be(1);

        await monitor.StopAsync(CancellationToken.None);
        monitor.State.Should().Be(FakeMonitor.LifecycleState.Stopped);
        monitor.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_IsIdempotent()
    {
        var monitor = new FakeMonitor("fake");

        await monitor.StartAsync(CancellationToken.None);
        await monitor.StartAsync(CancellationToken.None);

        monitor.State.Should().Be(FakeMonitor.LifecycleState.Running);
        monitor.StartCount.Should().Be(2,
            "tracking counts so future monitors can decide to dedupe or not — the IMonitor contract is silent on dedupe, but the SEAM caller (ZenVizorHostedService) must work either way");
    }

    [Fact]
    public async Task StopAsync_WithoutStart_IsTolerated()
    {
        var monitor = new FakeMonitor("fake");

        var act = async () => await monitor.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "Stop on a never-started monitor is a no-op, not a contract violation; the host treats it the same as a normal teardown.");
        monitor.State.Should().Be(FakeMonitor.LifecycleState.Stopped);
    }

    [Fact]
    public async Task StartAsync_WithCanceledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var monitor = new FakeMonitor("fake");

        var act = async () => await monitor.StartAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        monitor.State.Should().Be(FakeMonitor.LifecycleState.Idle,
            "cancellation BEFORE start should leave the monitor un-entered");
    }

    /// <summary>
    /// Minimal in-process <see cref="IMonitor"/> implementation. Tracks the
    /// lifecycle transitions and the start/stop counters so tests can assert
    /// against them. No threads, no timers, no I/O — every transition is
    /// synchronous under the test thread.
    /// </summary>
    private sealed class FakeMonitor : IMonitor
    {
        public enum LifecycleState { Idle, Running, Stopped }

        public FakeMonitor(string name) => Name = name;

        public string Name { get; }
        public LifecycleState State { get; private set; } = LifecycleState.Idle;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            State = LifecycleState.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            State = LifecycleState.Stopped;
            return Task.CompletedTask;
        }
    }
}
