// SPDX-License-Identifier: GPL-3.0-or-later

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
///   <item>Starts the capture source.</item>
///   <item>Spawns a reader loop that feeds observations into the aggregator.</item>
///   <item>Starts a <see cref="PeriodicTimer"/> firing every <c>flush.interval_ms</c>
///         that calls <see cref="TrafficAggregator.Flush"/>.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CaptureMonitor : IMonitor, IAsyncDisposable
{
    private readonly ICaptureSource _captureSource;
    private readonly TrafficAggregator _aggregator;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _readerLoop;
    private Task? _flushLoop;
    private volatile bool _started;
    private volatile bool _stopRequested;

    public CaptureMonitor(
        ICaptureSource captureSource,
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

    /// <summary>
    /// True iff the monitor was started, has not been stopped, AND the
    /// underlying capture path is still alive. Captures three failure modes
    /// the old "settable bool" surface masked:
    /// <list type="bullet">
    ///   <item>Source flipped its <see cref="ICaptureSource.IsFaulted"/> flag
    ///         (e.g. ETW Process loop threw).</item>
    ///   <item>Reader loop completed without a stop being requested — the
    ///         channel closed under us, no more observations will arrive.</item>
    /// </list>
    /// The IPC handler's <c>CaptureActive</c> field is wired straight to this,
    /// so a dead capture path now correctly reports as inactive.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (!_started || _stopRequested) return false;
            if (_captureSource.IsFaulted) return false;
            var reader = _readerLoop;
            if (reader is not null && reader.IsCompleted) return false;
            return true;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captureSource.Start();

        _readerLoop = Task.Run(() => ReaderLoopAsync(_cts.Token));
        _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));

        _started = true;
        _logger.LogInformation("Capture monitor started. Flush interval = {Interval}.", _flushInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started || _stopRequested)
        {
            return;
        }

        _stopRequested = true;

        // 1. Close the source first. Its writer completes the channel, so
        //    whatever observations are still buffered get drained by the
        //    reader instead of being silently dropped.
        if (_captureSource is IAsyncDisposable asyncDisposable)
        {
            try { await asyncDisposable.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
        else if (_captureSource is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { /* best-effort */ }
        }

        // 2. Drain the reader loop. We deliberately do NOT cancel _cts before
        //    this — cancellation would short-circuit ObserveAsync and drop
        //    in-flight observations that the just-completed source already
        //    wrote into the channel. The reader exits naturally when the
        //    channel completes.
        if (_readerLoop is not null)
        {
            try { await _readerLoop.ConfigureAwait(false); } catch { /* best-effort */ }
        }

        // 3. Now stop the flush ticker. The final flush below will catch
        //    whatever the reader handed off after the last tick fired.
        _cts?.Cancel();
        if (_flushLoop is not null)
        {
            try { await _flushLoop.ConfigureAwait(false); } catch { /* best-effort */ }
        }

        // 4. Final flush so the just-drained observations land in storage.
        try
        {
            _aggregator.Flush(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final flush on shutdown failed.");
        }

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
