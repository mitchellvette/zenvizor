namespace ZenVizor.Ipc.Contracts.Dto;

public sealed record PingResult(
    string Pong,
    long ServerTimestampUnixMs);
