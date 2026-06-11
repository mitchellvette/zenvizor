using ZenVizor.Core.Observations;

namespace ZenVizor.Capture;

/// <summary>
/// A source of <see cref="NetworkObservation"/>s. Real production source is
/// ETW (<see cref="EtwCaptureSource"/>); the synthetic source
/// (<see cref="SyntheticCaptureSource"/>) replays scripted events on CI so
/// the rest of the pipeline is deterministically testable without ETW.
/// </summary>
public interface ICaptureSource
{
    /// <summary>
    /// Begin emitting observations. Default no-op — sources that need an
    /// explicit start step (ETW kernel session) override; sources that
    /// stream from a pre-loaded buffer (synthetic) leave the default.
    /// </summary>
    void Start() { }

    /// <summary>
    /// True once the source has terminated unexpectedly (e.g. ETW
    /// <c>Source.Process</c> threw, or exited without a shutdown request).
    /// CaptureMonitor reads this when reporting capture-active health so a
    /// dead capture loop is not masked as healthy.
    /// </summary>
    bool IsFaulted => false;

    /// <summary>
    /// Begin emitting observations. The enumerable completes when
    /// <paramref name="cancellationToken"/> is cancelled or the underlying
    /// source is exhausted (synthetic case).
    /// </summary>
    IAsyncEnumerable<NetworkObservation> ObserveAsync(CancellationToken cancellationToken);
}
