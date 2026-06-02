namespace ZenVizor.Core.Attribution;

/// <summary>
/// Test-only <see cref="IProcessImageResolver"/> backed by a mutable PID map.
/// Tests can update entries to simulate process exit (remove) or PID reuse
/// (replace with new start time).
/// </summary>
public sealed class InMemoryProcessImageResolver : IProcessImageResolver
{
    private readonly Dictionary<int, ProcessImageInfo> _byPid = new();

    public ProcessImageInfo? Resolve(int pid) =>
        _byPid.TryGetValue(pid, out var info) ? info : null;

    public void Set(ProcessImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        _byPid[info.Pid] = info;
    }

    public void Remove(int pid) => _byPid.Remove(pid);
}
