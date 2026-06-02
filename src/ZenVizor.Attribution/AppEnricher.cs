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
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppEnricher : IAppEnricher
{
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IUserWritablePathClassifier _pathClassifier;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, EnrichmentResult> _cache = new();

    public AppEnricher(
        ISignatureVerifier signatureVerifier,
        IUserWritablePathClassifier pathClassifier,
        ILogger<AppEnricher>? logger = null)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _pathClassifier    = pathClassifier    ?? throw new ArgumentNullException(nameof(pathClassifier));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
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
            _cache[key.Value] = result;
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
