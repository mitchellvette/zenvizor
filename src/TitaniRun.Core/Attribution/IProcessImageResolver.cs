namespace TitaniRun.Core.Attribution;

/// <summary>
/// PID → process image identity. Implementations cache by PID — image lookups
/// are not in the per-event hot path.
/// </summary>
public interface IProcessImageResolver
{
    ProcessImageInfo? Resolve(int pid);
}

/// <param name="Pid">PID this snapshot pertains to.</param>
/// <param name="ImagePath">Full image path (e.g. <c>C:\Windows\System32\svchost.exe</c>).</param>
/// <param name="ImageName">Just the filename portion (<c>svchost.exe</c>).</param>
/// <param name="StartTimeUnixMs">
/// Process start time (Unix-ms). Used to detect PID reuse: if the cached session's
/// start time differs from a newly observed start time for the same PID, the
/// session has rolled over.
/// </param>
public sealed record ProcessImageInfo(
    int Pid,
    string ImagePath,
    string ImageName,
    long StartTimeUnixMs);
