using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// In-memory IPC handler used by contract tests. The version-negotiation policy
/// is configurable so we can drive both the accepted and the rejected paths.
/// </summary>
internal sealed class FakeIpcHandler : IZenVizorIpc
{
    private readonly Func<string, NegotiateVersionResult> _versionPolicy;

    public FakeIpcHandler(Func<string, NegotiateVersionResult>? versionPolicy = null)
    {
        _versionPolicy = versionPolicy ?? DefaultPolicy;
    }

    public int PingCount { get; private set; }
    public string? LastNegotiatedClientVersion { get; private set; }

    public Task<NegotiateVersionResult> NegotiateVersionAsync(string clientVersion)
    {
        LastNegotiatedClientVersion = clientVersion;
        return Task.FromResult(_versionPolicy(clientVersion));
    }

    public Task<PingResult> PingAsync()
    {
        PingCount++;
        return Task.FromResult(new PingResult(
            Pong: "pong",
            ServerTimestampUnixMs: 1_700_000_000_000L));
    }

    public Task<ServiceStatusResult> GetServiceStatusAsync()
    {
        return Task.FromResult(new ServiceStatusResult(
            ServiceName: "ZenVizor.Service",
            Version: "0.1.0",
            ProtocolVersion: ProtocolVersion.Current,
            StartedAtUnixMs: 1_700_000_000_000L,
            UptimeMs: 0,
            DbPath: @"C:\fake\zenvizor.db",
            CaptureActive: false));
    }

    private static NegotiateVersionResult DefaultPolicy(string clientVersion) =>
        ProtocolVersion.IsCompatible(clientVersion)
            ? new NegotiateVersionResult(Accepted: true, ServerVersion: ProtocolVersion.Current, Reason: null)
            : new NegotiateVersionResult(
                Accepted: false,
                ServerVersion: ProtocolVersion.Current,
                Reason: $"Client major version {clientVersion} is not compatible with server major {ProtocolVersion.Major}.");
}
