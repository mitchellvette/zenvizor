using FluentAssertions;
using TitaniRun.Core.Aggregation;
using TitaniRun.Core.Attribution;

namespace TitaniRun.Core.Tests;

public sealed class SessionTrackerTests
{
    private static ProcessImageInfo Image(int pid, long startMs, string path = @"C:\app\a.exe") =>
        new(pid, path, Path.GetFileName(path), startMs);

    [Fact]
    public void TryTrack_FirstObservation_QueuesPendingOpen()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 100, startMs: 500));
        var tracker = new SessionTracker(resolver);

        var tracked = tracker.TryTrack(100, nowUnixMs: 1000);

        tracked.Should().BeTrue();
        var pending = tracker.CollectPendingOpens();
        pending.Should().ContainSingle();
        pending[0].Pid.Should().Be(100);
        pending[0].StartTimeUnixMs.Should().Be(500);
        tracker.TryGetSessionId(100, out _).Should().BeFalse(); // not yet persisted
    }

    [Fact]
    public void OnFlushCommitted_PromotesPendingToPersisted()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 100, startMs: 500));
        var tracker = new SessionTracker(resolver);
        tracker.TryTrack(100, 1000);

        tracker.OnFlushCommitted(
            pidToNewSessionId: new Dictionary<int, int> { [100] = 42 },
            closedSessionIds: Array.Empty<int>());

        tracker.CollectPendingOpens().Should().BeEmpty();
        tracker.TryGetSessionId(100, out var sid).Should().BeTrue();
        sid.Should().Be(42);
        tracker.SnapshotPersistedSessions().Should().ContainKey(100).WhoseValue.Should().Be(42);
    }

    [Fact]
    public void TryTrack_PidReuse_QueuesCloseOfOldAndPendingForNew()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 100, startMs: 500, path: @"C:\a\old.exe"));
        var tracker = new SessionTracker(resolver);
        tracker.TryTrack(100, 1000);
        tracker.OnFlushCommitted(
            new Dictionary<int, int> { [100] = 7 },
            Array.Empty<int>());

        // New process inherits PID 100 with a later start time.
        resolver.Set(Image(pid: 100, startMs: 5000, path: @"C:\a\new.exe"));
        tracker.TryTrack(100, 6000);

        var stale = tracker.CollectStaleSessionIds(nowUnixMs: 6000);
        stale.Should().Contain(7);

        var pending = tracker.CollectPendingOpens();
        pending.Should().ContainSingle();
        pending[0].App.ImagePath.Should().Be(@"C:\a\new.exe");
        pending[0].StartTimeUnixMs.Should().Be(5000);
    }

    [Fact]
    public void TryTrack_IdlePid_Skipped()
    {
        var tracker = new SessionTracker(new InMemoryProcessImageResolver());

        tracker.TryTrack(SessionTracker.IdlePid, 1000).Should().BeFalse();
        tracker.CollectPendingOpens().Should().BeEmpty();
    }

    [Fact]
    public void TryTrack_NoImageInfo_Skipped()
    {
        // Resolver has no entry for PID 9999.
        var tracker = new SessionTracker(new InMemoryProcessImageResolver());

        tracker.TryTrack(9999, 1000).Should().BeFalse();
    }

    [Fact]
    public void CollectStaleSessionIds_ReportsExitedPersistedSessions()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 100, startMs: 500));
        var tracker = new SessionTracker(resolver, staleThresholdMs: 5_000);
        tracker.TryTrack(100, 1000);
        tracker.OnFlushCommitted(new Dictionary<int, int> { [100] = 11 }, Array.Empty<int>());

        // Process exits (resolver no longer finds it) and time passes.
        resolver.Remove(100);
        var stale = tracker.CollectStaleSessionIds(nowUnixMs: 10_000);

        stale.Should().ContainSingle().Which.Should().Be(11);
    }

    [Fact]
    public void CollectStaleSessionIds_KeepsLivingButQuietSessions()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 100, startMs: 500));
        var tracker = new SessionTracker(resolver, staleThresholdMs: 5_000);
        tracker.TryTrack(100, 1000);
        tracker.OnFlushCommitted(new Dictionary<int, int> { [100] = 11 }, Array.Empty<int>());

        // Time has passed but resolver still finds the same process.
        var stale = tracker.CollectStaleSessionIds(nowUnixMs: 10_000);

        stale.Should().BeEmpty();
    }

    [Fact]
    public void OnFlushCommitted_RemovesClosedSessionsFromMap()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 100, startMs: 500));
        var tracker = new SessionTracker(resolver);
        tracker.TryTrack(100, 1000);
        tracker.OnFlushCommitted(new Dictionary<int, int> { [100] = 11 }, Array.Empty<int>());

        tracker.OnFlushCommitted(
            pidToNewSessionId: new Dictionary<int, int>(),
            closedSessionIds: new[] { 11 });

        tracker.TryGetSessionId(100, out _).Should().BeFalse();
        tracker.TrackedCount.Should().Be(0);
    }

    [Fact]
    public void TryTrack_SystemPid4_IsAccepted()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(new ProcessImageInfo(
            Pid: 4, ImagePath: "(kernel)", ImageName: "System", StartTimeUnixMs: 0));
        var tracker = new SessionTracker(resolver);

        tracker.TryTrack(4, 1000).Should().BeTrue();
        tracker.CollectPendingOpens().Should().ContainSingle().Which.Pid.Should().Be(4);
    }

    // ----- Phase 2 enrichment + service-host wiring -----

    [Fact]
    public void TryTrack_NewSession_AppliesEnrichmentToAppIdentity()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 200, startMs: 500, path: @"C:\Programs\app.exe"));
        var enricher = new RecordingEnricher(new EnrichmentResult(
            Publisher: "Acme Co",
            SignatureStatus: "Signed",
            IsUserWritablePath: false));
        var tracker = new SessionTracker(resolver, enricher, NoOpServiceHostResolver.Instance);

        tracker.TryTrack(200, 1000);

        var pending = tracker.CollectPendingOpens();
        pending.Should().ContainSingle();
        pending[0].App.Publisher.Should().Be("Acme Co");
        pending[0].App.SignatureStatus.Should().Be("Signed");
        pending[0].App.IsUserWritablePath.Should().BeFalse();
        enricher.CallCount.Should().Be(1);
    }

    [Fact]
    public void TryTrack_SamePidObservedTwice_EnricherCalledOnce()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 200, startMs: 500));
        var enricher = new RecordingEnricher(new EnrichmentResult(null, "Unsigned", true));
        var tracker = new SessionTracker(resolver, enricher, NoOpServiceHostResolver.Instance);

        tracker.TryTrack(200, 1000);
        tracker.TryTrack(200, 1500);
        tracker.TryTrack(200, 2000);

        enricher.CallCount.Should().Be(1);
    }

    [Fact]
    public void TryTrack_PidReuse_TriggersFreshEnrichment()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 200, startMs: 500, path: @"C:\a\old.exe"));
        var enricher = new RecordingEnricher(new EnrichmentResult(null, "Unsigned", true));
        var tracker = new SessionTracker(resolver, enricher, NoOpServiceHostResolver.Instance);

        tracker.TryTrack(200, 1000);
        tracker.OnFlushCommitted(new Dictionary<int, int> { [200] = 7 }, Array.Empty<int>());

        // PID reused with a different start time → fresh enrichment.
        resolver.Set(Image(pid: 200, startMs: 5000, path: @"C:\a\new.exe"));
        tracker.TryTrack(200, 6000);

        enricher.CallCount.Should().Be(2);
        enricher.LastImagePath.Should().Be(@"C:\a\new.exe");
    }

    [Fact]
    public void TryTrack_SvchostPid_PopulatesHostedServices()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 1500, startMs: 500, path: @"C:\Windows\System32\svchost.exe"));
        var services = new FakeServiceHostResolver();
        services.Set(1500, new[] { "Dnscache", "NlaSvc", "Dhcp" });
        var tracker = new SessionTracker(resolver, NoOpAppEnricher.Instance, services);

        tracker.TryTrack(1500, 1000);

        var pending = tracker.CollectPendingOpens();
        pending.Should().ContainSingle();
        pending[0].HostedServices.Should().Be("Dnscache,NlaSvc,Dhcp");
        services.CallCount.Should().Be(1);
    }

    [Fact]
    public void TryTrack_NonServiceHostPid_LeavesHostedServicesNull()
    {
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(Image(pid: 1500, startMs: 500, path: @"C:\app\a.exe"));
        var services = new FakeServiceHostResolver(); // no entry for 1500
        var tracker = new SessionTracker(resolver, NoOpAppEnricher.Instance, services);

        tracker.TryTrack(1500, 1000);

        tracker.CollectPendingOpens()[0].HostedServices.Should().BeNull();
    }

    private sealed class RecordingEnricher : IAppEnricher
    {
        private readonly EnrichmentResult _result;
        public int CallCount { get; private set; }
        public string? LastImagePath { get; private set; }
        public RecordingEnricher(EnrichmentResult result) => _result = result;
        public EnrichmentResult Enrich(ProcessImageInfo image)
        {
            CallCount++;
            LastImagePath = image.ImagePath;
            return _result;
        }
    }

    private sealed class FakeServiceHostResolver : IServiceHostResolver
    {
        private readonly Dictionary<int, IReadOnlyList<string>> _byPid = new();
        public int CallCount { get; private set; }
        public void Set(int pid, IEnumerable<string> services) => _byPid[pid] = services.ToList();
        public IReadOnlyList<string>? ResolveHostedServices(int pid)
        {
            CallCount++;
            return _byPid.TryGetValue(pid, out var list) ? list : null;
        }
    }
}
