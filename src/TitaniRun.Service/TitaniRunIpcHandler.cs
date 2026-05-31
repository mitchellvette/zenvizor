using System.Reflection;
using TitaniRun.Ipc.Contracts;
using TitaniRun.Ipc.Contracts.Dto;

namespace TitaniRun.Service;

/// <summary>
/// The service-side implementation of <see cref="ITitaniRunIpc"/>.
/// Phase 0 stub: capture/DB-derived fields will be wired in later phases.
/// </summary>
internal sealed class TitaniRunIpcHandler : ITitaniRunIpc
{
    private readonly long _startedAtUnixMs;
    private readonly string _dbPath;
    private readonly string _serviceVersion;

    public TitaniRunIpcHandler(long startedAtUnixMs, string dbPath)
    {
        _startedAtUnixMs = startedAtUnixMs;
        _dbPath = dbPath;
        _serviceVersion = typeof(TitaniRunIpcHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(TitaniRunIpcHandler).Assembly.GetName().Version?.ToString()
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
            CaptureActive: false));
    }
}
