using FluentAssertions;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// Shared helper — opens a gated RPC session and finishes negotiation so
/// the test body can call envelope-tier methods without re-running the
/// handshake every test. Mirrors the pattern in
/// <see cref="SettingsContractTests"/>.
/// </summary>
internal static class NegotiatedSessionHelper
{
    public static async Task<GatedRpcSession> CreateAsync(IZenVizorIpc handler)
    {
        var session = GatedRpcSession.Create(handler);
        var negotiate = await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);
        negotiate.Accepted.Should().BeTrue();
        return session;
    }
}

/// <summary>
/// Phase 8 — contract tests for the <see cref="ConnectionRow.ResolvedHost"/>
/// field. Verifies that a present hostname survives the JSON-RPC round-trip,
/// that null survives, and that the schema-floor check enforces the v1 → v2
/// bump (a v2 client refuses a v1 server's envelope). Constant-value
/// assertion guards against an accidental bump-back.
/// </summary>
public sealed class ConnectionResolvedHostContractTests
{
    [Fact]
    public void IpcSchemaVersion_Query_is_v2_after_Phase_8_bump()
    {
        // This assertion exists so anyone bumping the constant has to also
        // update this test — making the bump intentional rather than
        // accidental. If you're here because the test failed: confirm the
        // bump matches a real shape change AND update the Phase 8 design
        // decision D2 entry in docs/zenvizor-sprint-plan.md.
        IpcSchemaVersion.Query.Should().Be(2);
    }

    [Fact]
    public async Task GetConnections_round_trips_resolved_host_when_present()
    {
        var handler = new FakeIpcHandler();
        handler.Connections = new ConnectionListResult(
            new QueryWindow(0, 1_000),
            new[]
            {
                new ConnectionRow(
                    Protocol:        "TCP",
                    RemoteAddress:   "52.96.222.114",
                    RemotePort:      443,
                    RemoteClass:     "Wan",
                    BytesUp:         100,
                    BytesDown:       200,
                    FirstSeenUnixMs: 0,
                    LastSeenUnixMs:  1_000,
                    ResolvedHost:    "outlook.office.com"),
            });
        await using var session = await NegotiatedSessionHelper.CreateAsync(handler);

        var envelope = await session.Proxy.GetConnectionsAsync(1, new QueryWindow(0, 1_000));

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.Query);
        envelope.Payload.Connections.Should().ContainSingle()
            .Which.ResolvedHost.Should().Be("outlook.office.com");
    }

    [Fact]
    public async Task GetConnections_round_trips_null_resolved_host()
    {
        var handler = new FakeIpcHandler();
        handler.Connections = new ConnectionListResult(
            new QueryWindow(0, 1_000),
            new[]
            {
                new ConnectionRow(
                    Protocol: "TCP", RemoteAddress: "8.8.8.8", RemotePort: 53,
                    RemoteClass: "Wan", BytesUp: 0, BytesDown: 0,
                    FirstSeenUnixMs: 0, LastSeenUnixMs: 1_000,
                    ResolvedHost: null),
            });
        await using var session = await NegotiatedSessionHelper.CreateAsync(handler);

        var envelope = await session.Proxy.GetConnectionsAsync(1, new QueryWindow(0, 1_000));

        envelope.Payload.Connections.Should().ContainSingle()
            .Which.ResolvedHost.Should().BeNull();
    }

    [Fact]
    public void UnwrapWithSchemaCheck_v2_envelope_against_v2_client_unwraps()
    {
        var envelope = new IpcEnvelope<ConnectionListResult>(
            SchemaVersion: 2,
            Payload: new ConnectionListResult(new QueryWindow(0, 1_000), Array.Empty<ConnectionRow>()));

        var act = () => envelope.UnwrapWithSchemaCheck(nameof(ConnectionListResult), expectedMinSchemaVersion: 2);

        act.Should().NotThrow();
    }

    [Fact]
    public void UnwrapWithSchemaCheck_v1_envelope_against_v2_client_throws()
    {
        // Floor check is load-bearing: a v2-aware client must refuse a v1
        // server envelope so it doesn't silently fall back to ResolvedHost
        // being unrendered (a legitimate-looking but lossy outcome).
        var envelope = new IpcEnvelope<ConnectionListResult>(
            SchemaVersion: 1,
            Payload: new ConnectionListResult(new QueryWindow(0, 1_000), Array.Empty<ConnectionRow>()));

        var act = () => envelope.UnwrapWithSchemaCheck(nameof(ConnectionListResult), expectedMinSchemaVersion: 2);

        act.Should().Throw<IpcSchemaVersionException>()
            .Where(e => e.ActualSchemaVersion == 1 && e.ExpectedMinSchemaVersion == 2);
    }
}
