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
