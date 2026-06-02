namespace ZenVizor.Core.Attribution;

/// <summary>
/// Test-only <see cref="IPidTableSnapshotSource"/> backed by a single mutable
/// snapshot. Tests call <see cref="SetSnapshot"/> to drive a scenario.
/// </summary>
public sealed class InMemoryPidTableSource : IPidTableSnapshotSource
{
    private PidTableSnapshot _current;

    public InMemoryPidTableSource(PidTableSnapshot? initial = null)
    {
        _current = initial ?? PidTableSnapshot.Empty(takenAtUnixMs: 0);
    }

    public PidTableSnapshot CurrentSnapshot => _current;

    public void SetSnapshot(PidTableSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _current = snapshot;
    }
}
