using FluentAssertions;
using ZenVizor.Attribution;
using ZenVizor.Attribution.Paths;
using ZenVizor.Core.Attribution;

namespace ZenVizor.Attribution.Tests;

/// <summary>
/// Headless tests for <see cref="AppEnricher"/>. Use a real temp file so the
/// <c>(path, mtime, size)</c> cache key exercises real filesystem stat
/// behavior. Signature verification is faked at the interface boundary so the
/// test suite does not depend on any signed binary fixtures.
/// </summary>
public sealed class AppEnricherTests : IDisposable
{
    private readonly string _tempFile;

    public AppEnricherTests()
    {
        _tempFile = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-enrich-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(_tempFile, new byte[] { 0x4d, 0x5a }); // MZ
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); }
        catch (IOException) { }
    }

    private ProcessImageInfo ImageFor(string path) =>
        new(Pid: 1234, ImagePath: path, ImageName: Path.GetFileName(path), StartTimeUnixMs: 100);

    [Fact]
    public void Enrich_SameBinary_VerifierCalledOnce()
    {
        var verifier = new RecordingVerifier(new SignatureVerificationResult("Signed", "Acme Co"));
        var classifier = new FakeClassifier(isUserWritable: false);
        var enricher = new AppEnricher(verifier, classifier);

        var first  = enricher.Enrich(ImageFor(_tempFile));
        var second = enricher.Enrich(ImageFor(_tempFile));

        first.SignatureStatus.Should().Be("Signed");
        first.Publisher.Should().Be("Acme Co");
        first.IsUserWritablePath.Should().BeFalse();
        second.Should().BeEquivalentTo(first);
        verifier.CallCount.Should().Be(1);
    }

    [Fact]
    public void Enrich_MtimeChange_InvalidatesCache()
    {
        var verifier = new RecordingVerifier(new SignatureVerificationResult("Signed", "Acme Co"));
        var enricher = new AppEnricher(verifier, new FakeClassifier(false));

        enricher.Enrich(ImageFor(_tempFile));

        // Bump mtime by writing new contents (also changes size — exercises both).
        File.WriteAllBytes(_tempFile, new byte[] { 0x4d, 0x5a, 0x00, 0x00, 0x99 });
        File.SetLastWriteTimeUtc(_tempFile, DateTime.UtcNow.AddMinutes(5));

        enricher.Enrich(ImageFor(_tempFile));

        verifier.CallCount.Should().Be(2);
    }

    [Fact]
    public void Enrich_MissingFile_ReturnsUnchecked_NoCache()
    {
        var verifier = new RecordingVerifier(new SignatureVerificationResult("Signed", "Should not happen"));
        var enricher = new AppEnricher(verifier, new FakeClassifier(false));
        var missing = Path.Combine(Path.GetTempPath(), $"zenvizor-missing-{Guid.NewGuid():N}.exe");

        var first = enricher.Enrich(ImageFor(missing));
        var second = enricher.Enrich(ImageFor(missing));

        first.Should().BeEquivalentTo(EnrichmentResult.Unchecked);
        second.Should().BeEquivalentTo(EnrichmentResult.Unchecked);
        verifier.CallCount.Should().Be(0); // never even invoked
        enricher.CacheCount.Should().Be(0);
    }

    [Fact]
    public void Enrich_VerifierReturnsUnchecked_NotCached()
    {
        var verifier = new RecordingVerifier(new SignatureVerificationResult("Unchecked", null));
        var enricher = new AppEnricher(verifier, new FakeClassifier(false));

        enricher.Enrich(ImageFor(_tempFile));
        enricher.Enrich(ImageFor(_tempFile));

        // Both should miss cache since Unchecked means "retry next time".
        verifier.CallCount.Should().Be(2);
        enricher.CacheCount.Should().Be(0);
    }

    [Fact]
    public void Enrich_DifferentPaths_CachedSeparately()
    {
        var verifier = new RecordingVerifier(new SignatureVerificationResult("Signed", "Acme Co"));
        var enricher = new AppEnricher(verifier, new FakeClassifier(false));

        var second = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-enrich-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(second, new byte[] { 0x4d, 0x5a });
        try
        {
            enricher.Enrich(ImageFor(_tempFile));
            enricher.Enrich(ImageFor(second));

            verifier.CallCount.Should().Be(2);
            enricher.CacheCount.Should().Be(2);
        }
        finally
        {
            File.Delete(second);
        }
    }

    [Fact]
    public void Enrich_CacheGrowsPastBound_OldestEntriesEvictedFifo()
    {
        // Working-set guard: the cache must not grow without bound on a
        // long-running service. Use the internal cap-injecting ctor with a
        // small bound so the test stays fast — production default is 1024.
        const int cap = 3;
        var verifier = new RecordingVerifier(new SignatureVerificationResult("Signed", "Acme Co"));
        var enricher = new AppEnricher(verifier, new FakeClassifier(false), logger: null, maxCacheEntries: cap);

        var paths = new List<string>();
        for (var i = 0; i < cap + 2; i++)
        {
            var p = Path.Combine(Path.GetTempPath(), $"zenvizor-bound-{Guid.NewGuid():N}.exe");
            File.WriteAllBytes(p, new byte[] { 0x4d, 0x5a, (byte)i });
            paths.Add(p);
        }
        try
        {
            foreach (var p in paths)
            {
                enricher.Enrich(ImageFor(p));
            }

            enricher.CacheCount.Should().Be(cap,
                "the cache must never exceed its configured cap");

            // The two oldest paths (paths[0] and paths[1]) were evicted FIFO,
            // so re-enriching them must invoke the verifier again. The most-
            // recently-inserted entries (paths[2..]) remain a cache hit.
            var callsAfterInitialFill = verifier.CallCount;
            enricher.Enrich(ImageFor(paths[0]));
            verifier.CallCount.Should().Be(callsAfterInitialFill + 1,
                "oldest entry was evicted; verifier re-runs");

            enricher.Enrich(ImageFor(paths[cap + 1]));
            verifier.CallCount.Should().Be(callsAfterInitialFill + 1,
                "most-recent entry is still cached");
        }
        finally
        {
            foreach (var p in paths)
            {
                try { File.Delete(p); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void Enrich_UnsignedFromTempPath_FlagsUserWritable()
    {
        var verifier = new RecordingVerifier(new SignatureVerificationResult("Unsigned", null));
        var classifier = new UserWritablePathClassifier(new[] { Path.GetTempPath() });
        var enricher = new AppEnricher(verifier, classifier);

        var result = enricher.Enrich(ImageFor(_tempFile));

        result.SignatureStatus.Should().Be("Unsigned");
        result.IsUserWritablePath.Should().BeTrue();
        result.Publisher.Should().BeNull();
    }

    private sealed class RecordingVerifier : ISignatureVerifier
    {
        private readonly SignatureVerificationResult _result;
        public int CallCount { get; private set; }
        public RecordingVerifier(SignatureVerificationResult result) => _result = result;
        public SignatureVerificationResult Verify(string imagePath)
        {
            CallCount++;
            return _result;
        }
    }

    private sealed class FakeClassifier : IUserWritablePathClassifier
    {
        private readonly bool _isUserWritable;
        public FakeClassifier(bool isUserWritable) => _isUserWritable = isUserWritable;
        public bool IsUserWritable(string imagePath) => _isUserWritable;
    }
}
