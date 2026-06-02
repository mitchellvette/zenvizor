using System.Net;
using FluentAssertions;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;

namespace ZenVizor.Core.Attribution.Tests;

public sealed class PidCorrectorTests
{
    private static readonly IPEndPoint LocalTcp = new(IPAddress.Parse("10.0.0.5"), 51234);
    private static readonly IPEndPoint RemoteTcp = new(IPAddress.Parse("8.8.8.8"), 443);
    private static readonly IPEndPoint LocalUdp = new(IPAddress.Parse("10.0.0.5"), 60000);
    private static readonly IPEndPoint RemoteUdp = new(IPAddress.Parse("1.1.1.1"), 53);

    private static PidTableSnapshot SnapshotWith(params PidTableEntry[] entries) =>
        new(takenAtUnixMs: 1_000_000, entries);

    private static NetworkObservation Recv(int? etwPid, IPEndPoint local, IPEndPoint remote, Protocol proto = Protocol.Tcp) =>
        new(TimestampUnixMs: 1_000_500,
            Pid: etwPid,
            Protocol: proto,
            LocalEndpoint: local,
            RemoteEndpoint: remote,
            Direction: Direction.Down,
            Bytes: 1024);

    private static NetworkObservation Send(int? etwPid, IPEndPoint local, IPEndPoint remote, Protocol proto = Protocol.Tcp) =>
        new(TimestampUnixMs: 1_000_500,
            Pid: etwPid,
            Protocol: proto,
            LocalEndpoint: local,
            RemoteEndpoint: remote,
            Direction: Direction.Up,
            Bytes: 1024);

    [Fact]
    public void Receive_WrongEtwPid_CorrectedFromSnapshot()
    {
        var snapshot = SnapshotWith(new PidTableEntry(Protocol.Tcp, LocalTcp, OwningPid: 4242));
        var corrector = new PidCorrector();

        // ETW reports PID 0 (a common DPC-context glitch); snapshot has the real owner.
        var obs = Recv(etwPid: 0, LocalTcp, RemoteTcp);

        corrector.Correct(obs, snapshot).Should().Be(4242);
    }

    [Fact]
    public void Receive_NullEtwPid_CorrectedFromSnapshot()
    {
        var snapshot = SnapshotWith(new PidTableEntry(Protocol.Tcp, LocalTcp, OwningPid: 4242));
        var corrector = new PidCorrector();

        corrector.Correct(Recv(etwPid: null, LocalTcp, RemoteTcp), snapshot)
            .Should().Be(4242);
    }

    [Fact]
    public void Receive_SnapshotMissesEndpoint_FallsBackToEtwPid()
    {
        var snapshot = PidTableSnapshot.Empty(takenAtUnixMs: 1_000_000);
        var corrector = new PidCorrector();

        // No entry in snapshot: keep the ETW PID even if it might be off.
        corrector.Correct(Recv(etwPid: 1234, LocalTcp, RemoteTcp), snapshot)
            .Should().Be(1234);
    }

    [Fact]
    public void Send_TrustsEtwPid_EvenWhenSnapshotDisagrees()
    {
        var snapshot = SnapshotWith(new PidTableEntry(Protocol.Tcp, LocalTcp, OwningPid: 9999));
        var corrector = new PidCorrector();

        // PRD §8: send-path ETW PID is generally correct; only fall back on null.
        corrector.Correct(Send(etwPid: 1234, LocalTcp, RemoteTcp), snapshot)
            .Should().Be(1234);
    }

    [Fact]
    public void Send_NullEtwPid_FallsBackToSnapshot()
    {
        var snapshot = SnapshotWith(new PidTableEntry(Protocol.Tcp, LocalTcp, OwningPid: 9999));
        var corrector = new PidCorrector();

        corrector.Correct(Send(etwPid: null, LocalTcp, RemoteTcp), snapshot)
            .Should().Be(9999);
    }

    [Fact]
    public void Send_NullEtwPid_NoSnapshotEntry_ReturnsNull()
    {
        var snapshot = PidTableSnapshot.Empty(1_000_000);
        var corrector = new PidCorrector();

        corrector.Correct(Send(etwPid: null, LocalTcp, RemoteTcp), snapshot)
            .Should().BeNull();
    }

    [Fact]
    public void Snapshot_DistinguishesTcpFromUdp()
    {
        // Same local endpoint, different protocol -> separate entries.
        var snapshot = SnapshotWith(
            new PidTableEntry(Protocol.Tcp, LocalTcp, OwningPid: 100),
            new PidTableEntry(Protocol.Udp, LocalTcp, OwningPid: 200));
        var corrector = new PidCorrector();

        corrector.Correct(Recv(null, LocalTcp, RemoteTcp, Protocol.Tcp), snapshot).Should().Be(100);
        corrector.Correct(Recv(null, LocalTcp, RemoteUdp, Protocol.Udp), snapshot).Should().Be(200);
    }

    [Fact]
    public void Snapshot_DistinguishesV4FromV6()
    {
        var v4Local = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234);
        var v6Local = new IPEndPoint(IPAddress.Parse("fe80::1"), 51234);

        var snapshot = SnapshotWith(
            new PidTableEntry(Protocol.Tcp, v4Local, OwningPid: 11),
            new PidTableEntry(Protocol.Tcp, v6Local, OwningPid: 22));
        var corrector = new PidCorrector();

        corrector.Correct(Recv(null, v4Local, RemoteTcp), snapshot).Should().Be(11);
        corrector.Correct(Recv(null, v6Local, RemoteTcp), snapshot).Should().Be(22);
    }
}
