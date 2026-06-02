using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ZenVizor.Core.Observations;

namespace ZenVizor.Capture;

/// <summary>
/// Test/replay implementation of <see cref="ICaptureSource"/>. Holds an internal
/// channel that tests (and any future replay tooling) write scripted events to.
/// Output cadence is controlled by the writer — calls to <see cref="EmitAsync"/>
/// are observed in order, deterministically.
/// </summary>
public sealed class SyntheticCaptureSource : ICaptureSource, IAsyncDisposable
{
    private readonly Channel<NetworkObservation> _channel =
        Channel.CreateUnbounded<NetworkObservation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Write a scripted observation. Returns once the value is in the channel.</summary>
    public ValueTask EmitAsync(NetworkObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return _channel.Writer.WriteAsync(observation, cancellationToken);
    }

    /// <summary>Convenience for fire-and-forget emission from synchronous test code.</summary>
    public bool TryEmit(NetworkObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return _channel.Writer.TryWrite(observation);
    }

    /// <summary>Signal that no more observations will be emitted. The reader completes after draining.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<NetworkObservation> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var observation))
            {
                yield return observation;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
