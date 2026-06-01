using TitaniRun.Core.Observations;

namespace TitaniRun.Capture;

/// <summary>
/// A source of <see cref="NetworkObservation"/>s. Real production source is
/// ETW (<see cref="EtwCaptureSource"/>); the synthetic source
/// (<see cref="SyntheticCaptureSource"/>) replays scripted events on CI so
/// the rest of the pipeline is deterministically testable without ETW.
/// </summary>
public interface ICaptureSource
{
    /// <summary>
    /// Begin emitting observations. The enumerable completes when
    /// <paramref name="cancellationToken"/> is cancelled or the underlying
    /// source is exhausted (synthetic case).
    /// </summary>
    IAsyncEnumerable<NetworkObservation> ObserveAsync(CancellationToken cancellationToken);
}
