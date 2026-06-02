using System.Reflection;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Service;

/// <summary>
/// The service-side implementation of <see cref="IZenVizorIpc"/>.
/// Phase 0 stub: capture/DB-derived fields will be wired in later phases.
/// </summary>
internal sealed class ZenVizorIpcHandler : IZenVizorIpc
{
    private readonly long _startedAtUnixMs;
    private readonly string _dbPath;
    private readonly Func<bool> _isCaptureActive;
    private readonly string _serviceVersion;

    public ZenVizorIpcHandler(long startedAtUnixMs, string dbPath, Func<bool>? isCaptureActive = null)
    {
        _startedAtUnixMs = startedAtUnixMs;
        _dbPath = dbPath;
        _isCaptureActive = isCaptureActive ?? (() => false);
        _serviceVersion = typeof(ZenVizorIpcHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(ZenVizorIpcHandler).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    public Task<NegotiateVersionResult> NegotiateVersionAsync(string clientVersion)
    {
        if (ProtocolVersion.IsCompatible(clientVersion))
        {
            return Task.FromResult(new NegotiateVersionResult(
                Accepted: true,
                ServerVersion: ProtocolVersion.Current,
                Reason: null));
        }

        return Task.FromResult(new NegotiateVersionResult(
            Accepted: false,
            ServerVersion: ProtocolVersion.Current,
            Reason: $"Client version '{clientVersion}' is not compatible with server major {ProtocolVersion.Major}."));
    }

    public Task<PingResult> PingAsync()
    {
        return Task.FromResult(new PingResult(
            Pong: "pong",
            ServerTimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public Task<ServiceStatusResult> GetServiceStatusAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Task.FromResult(new ServiceStatusResult(
            ServiceName: ServiceConstants.ServiceName,
            Version: _serviceVersion,
            ProtocolVersion: ProtocolVersion.Current,
            StartedAtUnixMs: _startedAtUnixMs,
            UptimeMs: now - _startedAtUnixMs,
            DbPath: _dbPath,
            CaptureActive: _isCaptureActive()));
    }
}
