using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZenVizor.Capture;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Monitoring;

namespace ZenVizor.Service;

/// <summary>
/// Phase 1's first <see cref="IMonitor"/>. Owns the capture-source → aggregator
/// pipeline plus the periodic flush tick. Starting the monitor:
/// <list type="number">
///   <item>Starts the ETW capture source.</item>
///   <item>Spawns a reader loop that feeds observations into the aggregator.</item>
///   <item>Starts a <see cref="PeriodicTimer"/> firing every <c>flush.interval_ms</c>
///         that calls <see cref="TrafficAggregator.Flush"/>.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CaptureMonitor : IMonitor, IAsyncDisposable
{
    private readonly EtwCaptureSource _captureSource;
    private readonly TrafficAggregator _aggregator;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _readerLoop;
    private Task? _flushLoop;

    public CaptureMonitor(
        EtwCaptureSource captureSource,
        TrafficAggregator aggregator,
        TimeSpan flushInterval,
        ILogger<CaptureMonitor> logger)
    {
        _captureSource = captureSource ?? throw new ArgumentNullException(nameof(captureSource));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _flushInterval = flushInterval;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "Capture";

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captureSource.Start();

        _readerLoop = Task.Run(() => ReaderLoopAsync(_cts.Token));
        _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));

        IsRunning = true;
        _logger.LogInformation("Capture monitor started. Flush interval = {Interval}.", _flushInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return;
        }

        _cts?.Cancel();

        if (_readerLoop is not null)
        {
            try { await _readerLoop.ConfigureAwait(false); } catch { /* best-effort */ }
        }
        if (_flushLoop is not null)
        {
            try { await _flushLoop.ConfigureAwait(false); } catch { /* best-effort */ }
        }

        await _captureSource.DisposeAsync().ConfigureAwait(false);

        // Final flush so in-memory observations aren't lost on shutdown.
        try
        {
            _aggregator.Flush(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final flush on shutdown failed.");
        }

        IsRunning = false;
        _cts?.Dispose();
        _cts = null;
        _logger.LogInformation("Capture monitor stopped.");
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var observation in _captureSource.ObserveAsync(cancellationToken).ConfigureAwait(false))
            {
                _aggregator.Observe(observation);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture reader loop terminated unexpectedly.");
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var summary = _aggregator.Flush(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (summary.SampleRowsWritten > 0 || summary.ConnectionUpserts > 0 || summary.SessionsClosed > 0)
                    {
                        _logger.LogDebug(
                            "Flush: samples={S} connections={C} closed={X}.",
                            summary.SampleRowsWritten, summary.ConnectionUpserts, summary.SessionsClosed);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Flush tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
