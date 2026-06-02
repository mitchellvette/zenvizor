namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// One row in an <see cref="ActivitySnapshot"/>. The rollup key is
/// <c>(AppIdentity, HostedServices)</c> so multiple PIDs of the same app
/// (e.g. 12× chrome.exe) collapse into one row, while distinct svchost PIDs
/// hosting different service sets stay separate per CLAUDE.md invariant #5.
/// </summary>
public sealed record AppActivity(
    string ImageName,
    string ImagePath,
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath,
    string? HostedServices,
    long BytesUpTotal,
    long BytesDownTotal,
    double BytesUpPerSec,
    double BytesDownPerSec);
