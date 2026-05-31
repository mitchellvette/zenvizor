namespace TitaniRun.Ipc.Contracts.Dto;

public sealed record NegotiateVersionResult(
    bool Accepted,
    string ServerVersion,
    string? Reason);
