namespace TitaniRun.Core.Attribution;

/// <summary>
/// Resolves a PID to the list of Windows services it hosts (for svchost-like
/// processes). Returns <c>null</c> when the PID is not a service host. Phase 2
/// Q2: snapshot is taken at session-open and never refreshed; that boundary is
/// documented in CLAUDE.md invariant #5.
/// </summary>
public interface IServiceHostResolver
{
    /// <summary>
    /// <c>null</c> when <paramref name="pid"/> hosts no services. A non-empty
    /// list otherwise. Bytes are NOT split across the services — see
    /// CLAUDE.md invariant #5.
    /// </summary>
    IReadOnlyList<string>? ResolveHostedServices(int pid);
}

/// <summary>
/// Default for code paths and tests that don't care about service host resolution.
/// Always returns <c>null</c>.
/// </summary>
public sealed class NoOpServiceHostResolver : IServiceHostResolver
{
    public static NoOpServiceHostResolver Instance { get; } = new();

    private NoOpServiceHostResolver() { }

    public IReadOnlyList<string>? ResolveHostedServices(int pid) => null;
}
