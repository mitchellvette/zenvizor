namespace ZenVizor.Ipc.Contracts.Dto;

public sealed record ServiceStatusResult(
    string ServiceName,
    string Version,
    string ProtocolVersion,
    long StartedAtUnixMs,
    long UptimeMs,
    string DbPath,
    bool CaptureActive);
