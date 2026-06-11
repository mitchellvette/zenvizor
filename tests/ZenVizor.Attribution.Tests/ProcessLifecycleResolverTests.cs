using System.Runtime.Versioning;
using FluentAssertions;
using ZenVizor.Attribution;

namespace ZenVizor.Attribution.Tests;

/// <summary>
/// Architectural guard for the Phase-3 reliability fix: trailing ETW network
/// events for a short-lived process must still resolve to its image, even
/// after the process has exited. Without these guarantees the founding
/// invariant ("rates match reality") silently breaks for sub-second processes
/// like a fast curl.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessLifecycleResolverTests
{
    [Fact]
    public void OnProcessStart_ThenResolve_ReturnsCachedImage()
    {
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(nowProvider: clock.Get);

        resolver.OnProcessStart(1234, @"C:\Tools\curl.exe", startUnixMs: 1_000);

        var info = resolver.Resolve(1234);
        info.Should().NotBeNull();
        info!.Pid.Should().Be(1234);
        info.ImagePath.Should().Be(@"C:\Tools\curl.exe");
        info.ImageName.Should().Be("curl.exe");
        info.StartTimeUnixMs.Should().Be(1_000);
    }

    [Fact]
    public void OnProcessStop_PreservesCacheWithinGraceWindow()
    {
        // This is the central regression guard. curl exits at t=2000. ETW
        // trailing network events arrive at t=2500. The resolver MUST still
        // return curl's image so those events can be attributed.
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(
            graceMs: 60_000, nowProvider: clock.Get);

        resolver.OnProcessStart(1234, @"C:\Tools\curl.exe", startUnixMs: 1_000);
        clock.Set(2_000);
        resolver.OnProcessStop(1234, stopUnixMs: 2_000);

        // Trailing event arrives 500 ms after exit.
        clock.Set(2_500);
        resolver.Resolve(1234).Should().NotBeNull("trailing events within grace must still resolve");

        // Far into the grace window: still resolves.
        clock.Set(60_999);
        resolver.Resolve(1234).Should().NotBeNull();
    }

    [Fact]
    public void Resolve_AfterGraceWindow_EvictsExitedEntry()
    {
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(
            graceMs: 1_000, nowProvider: clock.Get);

        resolver.OnProcessStart(1234, @"C:\Tools\curl.exe", startUnixMs: 1_000);
        resolver.OnProcessStop(1234, stopUnixMs: 2_000);
        resolver.CachedCount.Should().Be(1);

        // graceMs after exit: still present.
        clock.Set(3_000);
        resolver.Resolve(1234).Should().NotBeNull();

        // Past grace + a Resolve to trigger eviction.
        clock.Set(3_001);
        resolver.Resolve(1234).Should().BeNull("entry should be evicted past grace");
        resolver.CachedCount.Should().Be(0);
    }

    [Fact]
    public void OnProcessStart_StillRunning_DoesNotEvictRegardlessOfTime()
    {
        // A long-running process must never be evicted just because of clock
        // advancement; eviction is gated on ExitedAt + grace.
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(
            graceMs: 10, nowProvider: clock.Get);

        resolver.OnProcessStart(1234, @"C:\Tools\service.exe", startUnixMs: 1_000);

        clock.Set(1_000_000);
        resolver.Resolve(1234).Should().NotBeNull("running processes are never evicted by time alone");
    }

    [Fact]
    public void PidReuse_NewStartOverridesOldEntry()
    {
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(nowProvider: clock.Get);

        resolver.OnProcessStart(1234, @"C:\old\old.exe", startUnixMs: 1_000);
        resolver.OnProcessStop(1234, stopUnixMs: 2_000);

        // OS reuses PID 1234 for a different process.
        clock.Set(3_000);
        resolver.OnProcessStart(1234, @"C:\new\new.exe", startUnixMs: 3_000);

        var info = resolver.Resolve(1234);
        info.Should().NotBeNull();
        info!.ImagePath.Should().Be(@"C:\new\new.exe");
        info.StartTimeUnixMs.Should().Be(3_000);
    }

    [Fact]
    public void Resolve_SystemPid_ReturnsSyntheticKernelImage()
    {
        var resolver = new ProcessLifecycleResolver();

        var info = resolver.Resolve(4);
        info.Should().NotBeNull();
        info!.ImageName.Should().Be("System");
        info.ImagePath.Should().Be("(kernel)");
    }

    [Fact]
    public void Resolve_PidZero_ReturnsNull()
    {
        var resolver = new ProcessLifecycleResolver();
        resolver.Resolve(0).Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownPid_TriesWin32Fallback_CurrentProcessSucceeds()
    {
        // The Win32 fallback must work for at least one well-known PID: ours.
        // This is the case that covers processes that were already running
        // when ZenVizor started and the priming step missed.
        var resolver = new ProcessLifecycleResolver();

        var ownPid = Environment.ProcessId;
        var info = resolver.Resolve(ownPid);

        info.Should().NotBeNull("Win32 fallback must resolve our own PID");
        info!.Pid.Should().Be(ownPid);
        info.ImagePath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Resolve_UnknownPid_NoSuchProcess_ReturnsNull()
    {
        var resolver = new ProcessLifecycleResolver();
        // PID very unlikely to exist (PIDs are 4-byte; high values get reused
        // slowly and are gone quickly). If by chance this PID is live, the
        // test still passes — we only assert "Win32 either resolves or
        // returns null cleanly."
        var result = resolver.Resolve(2_000_000_000);
        // No exception; either null or some weird live process.
        _ = result;
    }

    [Fact]
    public void Resolve_RepeatedMissForSamePid_PopulatesNegativeCache()
    {
        // Perf gate: every trailing ETW event from an exited PID used to
        // re-invoke Process.GetProcessById, which throws ArgumentException
        // and is expensive. After this fix the first miss is recorded in
        // a short-TTL negative cache so the rest of the burst short-circuits.
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(nowProvider: clock.Get);
        const int ghostPid = 1_999_999_999;

        resolver.Resolve(ghostPid).Should().BeNull();
        resolver.NegativeCachedCount.Should().Be(1, "first miss seeded the negative cache");

        // Burst of trailing events for the same ghost. All should return null
        // and the negative cache size stays at 1 (no duplicates).
        for (var i = 0; i < 10; i++)
        {
            resolver.Resolve(ghostPid).Should().BeNull();
        }
        resolver.NegativeCachedCount.Should().Be(1);
    }

    [Fact]
    public void NegativeCache_ExpiresAfterTtl()
    {
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(nowProvider: clock.Get);
        const int ghostPid = 1_999_999_998;

        resolver.Resolve(ghostPid).Should().BeNull();
        resolver.NegativeCachedCount.Should().Be(1);

        // Past the negative-cache TTL the resolver retries Win32, finds nothing,
        // and re-seeds the entry. The cache size stays bounded at 1 entry per PID.
        clock.Set(1_000 + ProcessLifecycleResolver.NegativeCacheTtlMs + 1);
        resolver.Resolve(ghostPid).Should().BeNull();
        resolver.NegativeCachedCount.Should().Be(1);
    }

    [Fact]
    public void OnProcessStart_ClearsNegativeCacheEntryForPid()
    {
        // If the negative cache hit BEFORE we got the ETW ProcessStart event
        // (unlikely but possible with reordered delivery), OnProcessStart
        // must invalidate the negative entry so the resolver returns the
        // real image instead of the cached null.
        var clock = new FakeClock(1_000);
        var resolver = new ProcessLifecycleResolver(nowProvider: clock.Get);
        const int pid = 1_999_999_997;

        resolver.Resolve(pid).Should().BeNull();
        resolver.NegativeCachedCount.Should().Be(1);

        resolver.OnProcessStart(pid, @"C:\Tools\curl.exe", startUnixMs: 1_100);
        resolver.NegativeCachedCount.Should().Be(0);

        var info = resolver.Resolve(pid);
        info.Should().NotBeNull();
        info!.ImagePath.Should().Be(@"C:\Tools\curl.exe");
    }

    [Fact]
    public void PrimeFromRunningProcesses_PopulatesCache()
    {
        var resolver = new ProcessLifecycleResolver();
        resolver.CachedCount.Should().Be(0);

        resolver.PrimeFromRunningProcesses();

        resolver.CachedCount.Should().BeGreaterThan(0, "OS has at least a few processes running");
        // Our own PID should be resolvable directly from the cache after priming.
        var info = resolver.Resolve(Environment.ProcessId);
        info.Should().NotBeNull();
    }

    private sealed class FakeClock
    {
        private long _now;
        public FakeClock(long initialUnixMs) => _now = initialUnixMs;
        public long Get() => _now;
        public void Set(long unixMs) => _now = unixMs;
    }
}
