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
    private Func<ActivitySnapshot> _snapshotProvider;

    public FakeIpcHandler(Func<string, NegotiateVersionResult>? versionPolicy = null)
    {
        _versionPolicy = versionPolicy ?? DefaultPolicy;
        _snapshotProvider = () => new ActivitySnapshot(0, 0.0, Array.Empty<AppActivity>());
    }

    public int PingCount { get; private set; }
    public int ActivitySnapshotCount { get; private set; }
    public string? LastNegotiatedClientVersion { get; private set; }

    public int SnapshotSchemaVersion { get; set; } = 1;

    public void SetSnapshot(ActivitySnapshot snapshot) => _snapshotProvider = () => snapshot;
    public void SetSnapshotProvider(Func<ActivitySnapshot> provider) => _snapshotProvider = provider;

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

    public Task<IpcEnvelope<ActivitySnapshot>> GetCurrentActivitySnapshotAsync()
    {
        ActivitySnapshotCount++;
        return Task.FromResult(new IpcEnvelope<ActivitySnapshot>(
            SchemaVersion: SnapshotSchemaVersion,
            Payload: _snapshotProvider()));
    }

    public CaptureStats Stats { get; set; } = new(
        CapturedAtUnixMs: 1_700_000_000_000L,
        ObservationsSeen: 0,
        ObservationsUnattributed: 0);

    public Task<IpcEnvelope<CaptureStats>> GetCaptureStatsAsync()
    {
        return Task.FromResult(new IpcEnvelope<CaptureStats>(
            SchemaVersion: 1,
            Payload: Stats));
    }

    // ---- Phase 4 query surface: scriptable stubs for contract tests ----

    public AppListResult AppList { get; set; } =
        new(new QueryWindow(0, 0), Array.Empty<AppListEntry>());
    public AppDetailResult AppDetail { get; set; } =
        new(new QueryWindow(0, 0), TrafficGrain.Samples,
            new AppListEntry(0, "", "", null, "Unchecked", false, 0, 0, 0, 0),
            Array.Empty<TrafficPoint>(), Array.Empty<SessionInfo>());
    public ConnectionListResult Connections { get; set; } =
        new(new QueryWindow(0, 0), Array.Empty<ConnectionRow>());
    public TrafficHistoryResult History { get; set; } =
        new(new QueryWindow(0, 0), TrafficGrain.Samples, Array.Empty<TrafficPoint>());

    public Task<IpcEnvelope<AppListResult>> GetAppListAsync(QueryWindow window) =>
        Task.FromResult(new IpcEnvelope<AppListResult>(1, AppList));

    public Task<IpcEnvelope<AppDetailResult>> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain) =>
        Task.FromResult(new IpcEnvelope<AppDetailResult>(1, AppDetail));

    public Task<IpcEnvelope<ConnectionListResult>> GetConnectionsAsync(int appId, QueryWindow window) =>
        Task.FromResult(new IpcEnvelope<ConnectionListResult>(1, Connections));

    public Task<IpcEnvelope<TrafficHistoryResult>> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain) =>
        Task.FromResult(new IpcEnvelope<TrafficHistoryResult>(1, History));

    private static NegotiateVersionResult DefaultPolicy(string clientVersion) =>
        ProtocolVersion.IsCompatible(clientVersion)
            ? new NegotiateVersionResult(Accepted: true, ServerVersion: ProtocolVersion.Current, Reason: null)
            : new NegotiateVersionResult(
                Accepted: false,
                ServerVersion: ProtocolVersion.Current,
                Reason: $"Client major version {clientVersion} is not compatible with server major {ProtocolVersion.Major}.");
}
