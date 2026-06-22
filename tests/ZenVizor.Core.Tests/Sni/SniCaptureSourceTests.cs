using System.Net;
using FluentAssertions;
using ZenVizor.Capture.Sni;
using ZenVizor.Core.Dns;

namespace ZenVizor.Core.Tests.Sni;

public sealed class SniCaptureSourceTests
{
    private const long Now = 1_700_000_000_000;

    /// <summary>
    /// Drives the full public source (Start -> substrate -> processor -> store)
    /// with an injected fake substrate, so the wiring is exercised end-to-end
    /// without any live capture. This is the IPC-free analogue of the manual
    /// "Chrome hostnames show up" gate.
    /// </summary>
    [Fact]
    public async Task Delivered_ipv4_clienthello_lands_in_store_via_public_source()
    {
        var store = new DnsResolutionStore();
        var fake = new FakeRawPacketSource();
        await using var source = new SniCaptureSource(store, fake, logger: null, now: () => Now);

        source.Start();

        var hello = SniTestFixtures.BuildClientHelloRecord("chrome.example.com");
        var packet = SniTestFixtures.BuildIpv4Tcp("198.51.100.7", srcPort: 52000, dstPort: 443, hello);
        fake.Deliver(packet);

        source.Hits.Should().Be(1);
        store.TryGetHostname(IPAddress.Parse("198.51.100.7"), Now + 1000, out var host).Should().BeTrue();
        host.Should().Be("chrome.example.com");
    }

    [Fact]
    public async Task Faulted_substrate_is_surfaced()
    {
        var store = new DnsResolutionStore();
        var fake = new FakeRawPacketSource();
        await using var source = new SniCaptureSource(store, fake, logger: null, now: () => Now);
        source.Start();

        fake.Fault();

        source.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_disposes_substrate()
    {
        var store = new DnsResolutionStore();
        var fake = new FakeRawPacketSource();
        var source = new SniCaptureSource(store, fake, logger: null, now: () => Now);
        source.Start();

        await source.DisposeAsync();

        fake.Disposed.Should().BeTrue();
    }

    private sealed class FakeRawPacketSource : IRawPacketSource
    {
        private Action<ReadOnlyMemory<byte>>? _onIpPacket;
        public bool Disposed { get; private set; }
        public bool IsFaulted { get; private set; }

        public void Start(Action<ReadOnlyMemory<byte>> onIpPacket) => _onIpPacket = onIpPacket;
        public void Deliver(ReadOnlyMemory<byte> ip) => _onIpPacket?.Invoke(ip);
        public void Fault() => IsFaulted = true;
        public void Dispose() => Disposed = true;
    }
}
