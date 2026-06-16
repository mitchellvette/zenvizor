using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Capture;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Integration.Tests.Alerts;

/// <summary>
/// Sprint Plan §6 acceptance gate for the Phase 6.1 alert pipeline:
/// "fixture (unsigned, user-writable, has connections) raises exactly one
/// correctly-typed alert; acknowledge flow tested." Wires the synthetic
/// capture source → real TrafficAggregator → real SqliteFlushSink → real
/// AlertProducer → real AlertsRepository → captured AlertRaised event
/// (matching what the service forwards to AlertBroadcaster).
/// <para>
/// No real ETW, no real named pipe — those are manual gates per CLAUDE.md.
/// This test verifies the headless pipeline a CI runner can exercise.
/// </para>
/// </summary>
public sealed class AlertPipelineEndToEndTests : IDisposable
{
    private const long T0 = 1_780_704_000_000L;
    private const long Hour = 3_600_000L;

    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;

    public AlertPipelineEndToEndTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-alerts-e2e-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    [Fact]
    public async Task UnsignedUserWritableWanConnection_RaisesExactlyOneAlertAndBroadcasts()
    {
        var raised = new List<AlertDto>();
        var (aggregator, _, _, producer, repo) = BuildPipeline(now: () => T0);
        producer.AlertRaised += dto => raised.Add(dto);

        await EmitOneWanObservationAsync(aggregator, pid: 1001);
        aggregator.Flush(T0 + 5_000);

        var rows = repo.Query(AlertState.Active, 50);
        rows.Should().HaveCount(1);
        rows[0].Type.Should().Be(nameof(AlertType.UnsignedFromUserPath));
        rows[0].Severity.Should().Be(nameof(NotableSeverity.Critical));
        rows[0].EntityKind.Should().Be(nameof(AlertEntityKind.App));
        rows[0].EntityRef.Should().Be("1");  // first app row in a fresh DB
        rows[0].AcknowledgedAtUnixMs.Should().BeNull();
        rows[0].Detail.Should().Contain("Connections so far: 1.");

        raised.Should().ContainSingle();
        raised[0].AlertId.Should().Be(rows[0].AlertId);
        raised[0].AppId.Should().Be(1);
        raised[0].Type.Should().Be(AlertType.UnsignedFromUserPath);
    }

    [Fact]
    public async Task SecondQualifyingObservation_DedupesAndAdvancesConnectionCount()
    {
        var raised = new List<AlertDto>();
        var (aggregator, _, _, producer, repo) = BuildPipeline(now: () => T0);
        producer.AlertRaised += dto => raised.Add(dto);

        await EmitOneWanObservationAsync(aggregator, pid: 1001);
        aggregator.Flush(T0 + 5_000);

        await EmitOneWanObservationAsync(aggregator, pid: 1001, observationTime: T0 + 7_000);
        aggregator.Flush(T0 + 10_000);

        var rows = repo.Query(AlertState.All, 50);
        rows.Should().HaveCount(1, because: "the dedupe gate must keep the active alert as one row");
        rows[0].Detail.Should().Contain("Connections so far: 2.");

        raised.Should().ContainSingle(because: "AlertRaised fires once, on the original insert");
    }

    [Fact]
    public async Task DismissedThenRaised_WithinCooldown_DoesNotReRaise()
    {
        var (aggregator, _, _, producer, repo) = BuildPipeline(now: () => T0);

        await EmitOneWanObservationAsync(aggregator, pid: 1001);
        aggregator.Flush(T0 + 5_000);
        var alertId = repo.Query(AlertState.Active, 50).Single().AlertId;

        repo.Dismiss(alertId, T0 + 1 * Hour).Should().BeTrue();

        // Same app, fresh observation 12 h later — still inside the 24 h
        // cooldown. The session is also still tracked (we won't have hit
        // the stale threshold) so the producer's in-memory state is stale
        // but the SQL gate refuses the insert regardless.
        producer.ForgetActive(nameof(AlertType.UnsignedFromUserPath), "1");
        await EmitOneWanObservationAsync(aggregator, pid: 1001, observationTime: T0 + 12 * Hour);
        aggregator.Flush(T0 + 12 * Hour + 5_000);

        var rows = repo.Query(AlertState.All, 50);
        rows.Should().HaveCount(1, because: "cooldown gate must suppress the re-raise");
        rows.Single().AcknowledgedAtUnixMs.Should().NotBeNull();
    }

    [Fact]
    public async Task SignedApp_DoesNotRaise()
    {
        var raised = new List<AlertDto>();
        var (aggregator, _, _, producer, repo) = BuildPipeline(
            now: () => T0,
            enrichment: new EnrichmentResult(
                Publisher: "Microsoft Corporation",
                SignatureStatus: "Signed",
                IsUserWritablePath: false));
        producer.AlertRaised += dto => raised.Add(dto);

        await EmitOneWanObservationAsync(aggregator, pid: 1001);
        aggregator.Flush(T0 + 5_000);

        repo.Query(AlertState.All, 50).Should().BeEmpty();
        raised.Should().BeEmpty();
    }

    [Fact]
    public async Task UnsignedButSystemPath_DoesNotRaise()
    {
        var raised = new List<AlertDto>();
        var (aggregator, _, _, producer, repo) = BuildPipeline(
            now: () => T0,
            enrichment: new EnrichmentResult(
                Publisher: null,
                SignatureStatus: "Unsigned",
                IsUserWritablePath: false));
        producer.AlertRaised += dto => raised.Add(dto);

        await EmitOneWanObservationAsync(aggregator, pid: 1001);
        aggregator.Flush(T0 + 5_000);

        repo.Query(AlertState.All, 50).Should().BeEmpty();
        raised.Should().BeEmpty();
    }

    /// <summary>
    /// Emit one local-to-WAN TCP observation for the given pid. The synthetic
    /// resolver hands the aggregator a user-writable temp-path image so the
    /// rule's IsUserWritablePath gate passes; the enricher (configured at
    /// pipeline-build time) controls the SignatureStatus.
    /// </summary>
    private static async Task EmitOneWanObservationAsync(
        TrafficAggregator aggregator, int pid, long observationTime = T0 + 500)
    {
        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51_000);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"),  443);

        var source = new SyntheticCaptureSource();
        source.TryEmit(new NetworkObservation(
            TimestampUnixMs: observationTime,
            Pid: pid,
            Protocol: Protocol.Tcp,
            LocalEndpoint: local,
            RemoteEndpoint: remote,
            Direction: Direction.Up,
            Bytes: 512));
        source.Complete();

        await foreach (var obs in source.ObserveAsync(CancellationToken.None))
        {
            aggregator.Observe(obs);
        }
    }

    private (TrafficAggregator Aggregator,
             SessionTracker Tracker,
             InMemoryProcessImageResolver Resolver,
             AlertProducer Producer,
             AlertsRepository Repo) BuildPipeline(
        Func<long> now,
        EnrichmentResult? enrichment = null)
    {
        var sink = new SqliteFlushSink(_connections);
        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(new ProcessImageInfo(
            Pid: 1001,
            ImagePath: @"C:\Users\Mitch\AppData\Local\Temp\bad.exe",
            ImageName: "bad.exe",
            StartTimeUnixMs: T0 - 60_000));

        var snapshotSource = new InMemoryPidTableSource();
        var enricher = new ScriptedEnricher(enrichment ?? new EnrichmentResult(
            Publisher: null,
            SignatureStatus: "Unsigned",
            IsUserWritablePath: true));
        var tracker = new SessionTracker(resolver, enricher, NoOpServiceHostResolver.Instance);

        var repo = new AlertsRepository(_connections);
        var sinkAdapter = new RepoSinkAdapter(repo);
        var producer = new AlertProducer(
            new IAlertRule[] { new UnsignedFromUserPathRule() },
            sinkAdapter,
            nowProvider: now);

        var aggregator = new TrafficAggregator(
            tracker,
            new PidCorrector(),
            snapshotSource,
            sink,
            nowProvider: now,
            alertEventSink: producer);

        return (aggregator, tracker, resolver, producer, repo);
    }

    private sealed class ScriptedEnricher : IAppEnricher
    {
        private readonly EnrichmentResult _result;
        public ScriptedEnricher(EnrichmentResult result) => _result = result;
        public EnrichmentResult Enrich(ProcessImageInfo image) => _result;
    }

    /// <summary>
    /// Local copy of the production AlertsRepositorySink so this test
    /// project doesn't have to take a project reference to ZenVizor.Service
    /// (which has a Windows-Service host stack pulled in via WorkerService).
    /// Behaviour-equivalent — same two-method bridge.
    /// </summary>
    private sealed class RepoSinkAdapter : IAlertSink
    {
        private readonly AlertsRepository _repo;
        public RepoSinkAdapter(AlertsRepository repo) => _repo = repo;

        public long TryInsert(
            string type, string severity, string sourceMonitor,
            string entityKind, string entityRef,
            string title, string detail,
            long nowUnixMs, long cooldownMs)
            => _repo.TryInsert(
                new NewAlert(type, severity, sourceMonitor, entityKind, entityRef, title, detail),
                nowUnixMs, cooldownMs);

        public int UpdateDetail(string type, string entityKind, string entityRef, string detail)
            => _repo.UpdateDetail(type, entityKind, entityRef, detail);
    }
}
