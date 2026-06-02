namespace ZenVizor.Core.Attribution;

/// <summary>
/// Produces <see cref="PidTableSnapshot"/>s on demand. The real implementation
/// wraps <c>GetExtendedTcpTable</c> / <c>GetExtendedUdpTable</c>; the in-memory
/// implementation is used by tests to drive deterministic correction scenarios.
/// </summary>
public interface IPidTableSnapshotSource
{
    /// <summary>
    /// Return the current (possibly cached) snapshot. Callers must not mutate
    /// the returned object; implementations may share a single instance across
    /// callers within the cache window.
    /// </summary>
    PidTableSnapshot CurrentSnapshot { get; }
}
