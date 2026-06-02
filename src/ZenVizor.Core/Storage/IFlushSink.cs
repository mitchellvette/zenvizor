namespace ZenVizor.Core.Storage;

/// <summary>
/// Atomic write-path for one flush tick. Implementations MUST process the
/// entire <see cref="FlushBatch"/> inside a single SQL transaction so partial
/// state never reaches disk on crash (CLAUDE.md invariant #4).
/// </summary>
public interface IFlushSink
{
    FlushBatchResult Flush(FlushBatch batch);
}
