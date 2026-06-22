// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Runtime.Versioning;
using FluentAssertions;
using ZenVizor.Attribution;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;

namespace ZenVizor.Attribution.Tests;

/// <summary>
/// Regression guard for the Phase-3 receive-path attribution bug. A fast
/// curl downloading 50 MB and exiting in &lt;1 s used to drop ~99 % of its
/// received bytes because the polled GetExtendedTcpTable snapshot never
/// captured the connection. This resolver populates a cache from ETW
/// connect events (which fire while the process is alive) and retains
/// entries for a grace window past disconnect so trailing receive events
/// still resolve.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectionLifecycleResolverTests
{
    private static IPEndPoint Local(int port) => new(IPAddress.Parse("10.0.0.5"), port);
    private static IPEndPoint Remote(int port) => new(IPAddress.Parse("8.8.8.8"), port);

    [Fact]
    public void OnConnect_PopulatesSnapshot()
    {
        var clock = new FakeClock(1_000);
        var fallback = new EmptyFallback();
        var resolver = new ConnectionLifecycleResolver(fallback, nowProvider: clock.Get);

        resolver.OnConnect(Protocol.Tcp, Local(51234), Remote(443),
            pid: 4242, timestampUnixMs: 1_000);

        var snap = resolver.CurrentSnapshot;
        snap.TryGetOwningPid(Protocol.Tcp, Local(51234), out var pid).Should().BeTrue();
        pid.Should().Be(4242);
    }

    [Fact]
    public void OnDisconnect_RetainsEntryWithinGraceWindow()
    {
        // The central regression test. curl exits at t=2000. ETW trailing
        // receive events arrive at t=2500. The resolver MUST still attribute
        // them to curl's PID; without this, ~99 % of the bytes get dropped.
        var clock = new FakeClock(1_000);
        var fallback = new EmptyFallback();
        var resolver = new ConnectionLifecycleResolver(
            fallback, graceMs: 60_000, nowProvider: clock.Get);

        resolver.OnConnect(Protocol.Tcp, Local(51234), Remote(443),
            pid: 4242, timestampUnixMs: 1_000);
        clock.Set(2_000);
        resolver.OnDisconnect(Protocol.Tcp, Local(51234), timestampUnixMs: 2_000);

        clock.Set(2_500);
        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Tcp, Local(51234), out var pid).Should().BeTrue();
        pid.Should().Be(4242);

        clock.Set(60_999);
        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Tcp, Local(51234), out var late).Should().BeTrue();
        late.Should().Be(4242);
    }

    [Fact]
    public void EntriesPastGraceWindow_AreEvicted()
    {
        var clock = new FakeClock(1_000);
        var fallback = new EmptyFallback();
        var resolver = new ConnectionLifecycleResolver(
            fallback, graceMs: 1_000, nowProvider: clock.Get);

        resolver.OnConnect(Protocol.Tcp, Local(51234), Remote(443), 4242, 1_000);
        resolver.OnDisconnect(Protocol.Tcp, Local(51234), 2_000);
        resolver.CachedCount.Should().Be(1);

        clock.Set(3_001);
        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Tcp, Local(51234), out _).Should().BeFalse();
        resolver.CachedCount.Should().Be(0);
    }

    [Fact]
    public void StillConnected_NeverEvictedRegardlessOfTime()
    {
        var clock = new FakeClock(1_000);
        var fallback = new EmptyFallback();
        var resolver = new ConnectionLifecycleResolver(
            fallback, graceMs: 10, nowProvider: clock.Get);

        resolver.OnConnect(Protocol.Tcp, Local(51234), Remote(443), 4242, 1_000);

        clock.Set(1_000_000);
        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Tcp, Local(51234), out _).Should().BeTrue();
    }

    [Fact]
    public void EndpointReuse_NewConnectOverwritesOldEntry()
    {
        var clock = new FakeClock(1_000);
        var fallback = new EmptyFallback();
        var resolver = new ConnectionLifecycleResolver(fallback, nowProvider: clock.Get);

        resolver.OnConnect(Protocol.Tcp, Local(51234), Remote(443), 1000, 1_000);
        resolver.OnDisconnect(Protocol.Tcp, Local(51234), 2_000);

        clock.Set(3_000);
        resolver.OnConnect(Protocol.Tcp, Local(51234), Remote(443), 2000, 3_000);

        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Tcp, Local(51234), out var pid).Should().BeTrue();
        pid.Should().Be(2000);
    }

    [Fact]
    public void CacheMiss_FallsBackToWrappedSource()
    {
        // Connection that existed before ZenVizor started: never saw a
        // connect event for it, but the IpHelper poller has it in its
        // snapshot. The resolver must surface fallback entries.
        var clock = new FakeClock(1_000);
        var fallback = new InMemoryFallback(new[]
        {
            new PidTableEntry(Protocol.Tcp, Local(80), 999),
        });
        var resolver = new ConnectionLifecycleResolver(fallback, nowProvider: clock.Get);

        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Tcp, Local(80), out var pid).Should().BeTrue();
        pid.Should().Be(999);
    }

    [Fact]
    public void CacheTakesPrecedenceOverFallback()
    {
        // ETW saw the connection-open and knows the real PID. The IpHelper
        // table might be stale (e.g. PID reuse race). Cache wins.
        var clock = new FakeClock(1_000);
        var fallback = new InMemoryFallback(new[]
        {
            new PidTableEntry(Protocol.Tcp, Local(51234), 1),
        });
        var resolver = new ConnectionLifecycleResolver(fallback, nowProvider: clock.Get);

        resolver.OnConnect(Protocol.Tcp, Local(51234), Remote(443), 4242, 1_000);

        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Tcp, Local(51234), out var pid).Should().BeTrue();
        pid.Should().Be(4242);
    }

    [Fact]
    public void UdpEntriesFlowThroughFromFallback()
    {
        // UDP has no connect event; the lifecycle resolver should pass UDP
        // queries straight to the fallback unchanged.
        var clock = new FakeClock(1_000);
        var fallback = new InMemoryFallback(new[]
        {
            new PidTableEntry(Protocol.Udp, Local(53), 555),
        });
        var resolver = new ConnectionLifecycleResolver(fallback, nowProvider: clock.Get);

        resolver.CurrentSnapshot.TryGetOwningPid(Protocol.Udp, Local(53), out var pid).Should().BeTrue();
        pid.Should().Be(555);
    }

    // ---- Test doubles ----

    private sealed class EmptyFallback : IPidTableSnapshotSource
    {
        public PidTableSnapshot CurrentSnapshot => PidTableSnapshot.Empty(0);
    }

    private sealed class InMemoryFallback : IPidTableSnapshotSource
    {
        private readonly IReadOnlyList<PidTableEntry> _entries;
        public InMemoryFallback(IReadOnlyList<PidTableEntry> entries) => _entries = entries;
        public PidTableSnapshot CurrentSnapshot => new(0, _entries);
    }

    private sealed class FakeClock
    {
        private long _now;
        public FakeClock(long initialUnixMs) => _now = initialUnixMs;
        public long Get() => _now;
        public void Set(long unixMs) => _now = unixMs;
    }
}
