using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;

namespace ZenVizor.Attribution;

/// <summary>
/// Composes <see cref="ISignatureVerifier"/> and
/// <see cref="IUserWritablePathClassifier"/> into a single per-binary lookup,
/// cached by <c>(image_path, mtime_unix_ms, size_bytes)</c> per Phase 2 Q1.
/// A swap-attack at the same path triggers a cache miss because mtime and/or
/// size change.
/// </summary>
/// <remarks>
/// <para>
/// If the file cannot be stat-ed (missing, permission denied, transient I/O),
/// the enricher returns <see cref="EnrichmentResult.Unchecked"/> and does NOT
/// cache the negative result — a future session-open for the same path can
/// retry. Per CLAUDE.md performance budget, <c>Unchecked</c> rows should be
/// rare after Phase 2 ships.
/// </para>
/// <para>
/// The cache is bounded (<see cref="MaxCacheEntries"/>) to keep working set
/// in check on long-running services. A typical desktop sees on the order
/// of 100 distinct binaries; the bound is generous and only matters as a
/// safety net against pathological binary churn. Eviction is FIFO via an
/// insertion-order queue.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppEnricher : IAppEnricher
{
    /// <summary>
    /// Cap on the number of <c>(path, mtime, size)</c> tuples retained.
    /// Beyond this, oldest entries are evicted FIFO so the cache cannot
    /// grow unbounded across a long-running service lifetime.
    /// </summary>
    public const int MaxCacheEntries = 1024;

    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IUserWritablePathClassifier _pathClassifier;
    private readonly ILogger _logger;
    private readonly int _maxCacheEntries;
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, EnrichmentResult> _cache = new();
    private readonly Queue<CacheKey> _insertionOrder = new();

    public AppEnricher(
        ISignatureVerifier signatureVerifier,
        IUserWritablePathClassifier pathClassifier,
        ILogger<AppEnricher>? logger = null)
        : this(signatureVerifier, pathClassifier, logger, MaxCacheEntries)
    {
    }

    internal AppEnricher(
        ISignatureVerifier signatureVerifier,
        IUserWritablePathClassifier pathClassifier,
        ILogger<AppEnricher>? logger,
        int maxCacheEntries)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _pathClassifier    = pathClassifier    ?? throw new ArgumentNullException(nameof(pathClassifier));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        if (maxCacheEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCacheEntries));
        }
        _maxCacheEntries = maxCacheEntries;
    }

    public EnrichmentResult Enrich(ProcessImageInfo image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var key = TryReadCacheKey(image.ImagePath);
        if (key is null)
        {
            // Can't stat the file — Unchecked, don't cache so a retry next time succeeds.
            return EnrichmentResult.Unchecked;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(key.Value, out var cached))
            {
                return cached;
            }
        }

        var sig = _signatureVerifier.Verify(image.ImagePath);
        var isUserWritable = _pathClassifier.IsUserWritable(image.ImagePath);
        var result = new EnrichmentResult(
            Publisher: sig.Publisher,
            SignatureStatus: sig.Status,
            IsUserWritablePath: isUserWritable);

        // Don't cache Unchecked results — those are transient (file locked etc.)
        // and we want a retry on the next session-open for that binary.
        if (sig.Status == "Unchecked")
        {
            return result;
        }

        lock (_gate)
        {
            // Re-check in case another caller raced us to populate.
            if (_cache.ContainsKey(key.Value))
            {
                return _cache[key.Value];
            }

            if (_cache.Count >= _maxCacheEntries)
            {
                // FIFO eviction. The insertion-order queue may contain keys
                // that were already removed (e.g. via a future explicit
                // invalidation path), so loop until we actually free a slot.
                while (_insertionOrder.Count > 0)
                {
                    var oldest = _insertionOrder.Dequeue();
                    if (_cache.Remove(oldest))
                    {
                        break;
                    }
                }
            }

            _cache[key.Value] = result;
            _insertionOrder.Enqueue(key.Value);
        }
        return result;
    }

    private CacheKey? TryReadCacheKey(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(imagePath);
            if (!info.Exists)
            {
                return null;
            }
            var mtimeMs = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                .ToUnixTimeMilliseconds();
            return new CacheKey(imagePath, mtimeMs, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not stat {Path} for enrichment cache key.", imagePath);
            return null;
        }
    }

    /// <summary>Test diagnostic — count of cached binary entries.</summary>
    internal int CacheCount
    {
        get { lock (_gate) { return _cache.Count; } }
    }

    private readonly record struct CacheKey(string Path, long MtimeUnixMs, long SizeBytes);
}
