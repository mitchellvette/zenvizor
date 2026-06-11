using System.Runtime.Versioning;
using FluentAssertions;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ipc.Server;
using ZenVizor.Service;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// End-to-end ZenVizorPipeServer ↔ ZenVizorPipeClient over a real Windows named
/// pipe. Contract tests in <c>ZenVizor.Ipc.Tests</c> drive an in-memory duplex
/// stream and so don't exercise the actual OS pipe stack — this test fills that
/// gap. CLAUDE.md mandates the placement: "Pipe round-trips go in the
/// integration test project." Named pipes work unelevated on CI runners
/// (Interactive SID has read/write on the pipe ACL), so this is safe.
/// <para>
/// Uses a per-test unique pipe name so concurrent CI runs don't collide.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeRoundTripTests
{
    [Fact]
    public async Task ConnectThenCall_ProductionPipeServer_NegotiatesAndReturnsStatsEnvelope()
    {
        // Unique pipe name per test run so concurrent CI / parallel xUnit
        // collections can't squat on each other.
        var pipeName = $"ZenVizor.Ipc.v1.test.{Guid.NewGuid():N}";

        var stats = new CaptureStats(
            CapturedAtUnixMs: 1_700_000_000_000L,
            ObservationsSeen: 42,
            ObservationsUnattributed: 1);
        var handler = new ZenVizorIpcHandler(
            startedAtUnixMs: 1_700_000_000_000L,
            dbPath: @"C:\fake\zenvizor.db",
            isCaptureActive: () => true,
            statsProvider: () => stats);

        await using var server = new ZenVizorPipeServer(handler, logger: null, pipeName: pipeName);
        server.Start();

        // ConnectAsync runs the version-negotiation handshake on connect; if
        // either step (pipe-level handshake OR JSON-RPC NegotiateVersion call)
        // fails, this throws and the test fails.
        await using var client = await ZenVizorPipeClient.ConnectAsync(
            pipeName: pipeName,
            connectTimeout: TimeSpan.FromSeconds(5));

        // Phase-0 method (open before negotiation) — sanity check that the
        // pipe carries JSON-RPC traffic both ways.
        var ping = await client.Proxy.PingAsync();
        ping.Pong.Should().Be("pong");

        // Envelope-tier method through the NegotiationGate that the pipe
        // server stamps onto every accepted connection.
        var envelope = await client.Proxy.GetCaptureStatsAsync();

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.CaptureStats);
        envelope.Payload.ObservationsSeen.Should().Be(42);
        envelope.Payload.ObservationsUnattributed.Should().Be(1);

        // And the floor-check helper unwraps cleanly when the version matches.
        var unwrapped = envelope.UnwrapWithSchemaCheck(
            nameof(CaptureStats), IpcSchemaVersion.CaptureStats);
        unwrapped.ObservationsSeen.Should().Be(42);
    }
}
