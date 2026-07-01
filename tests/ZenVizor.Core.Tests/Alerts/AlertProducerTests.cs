// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests.Alerts;

/// <summary>
/// Producer-level behaviour: dedupe gate routing, per-active-alert count
/// + first_seen state machine, the restart-drift quiet path (no
/// UpdateDetail when TryInsert fails on a key the producer doesn't already
/// track), and AlertRaised event emission semantics. Uses an in-memory
/// <see cref="IAlertSink"/> fake so no SQLite is involved.
/// </summary>
public sealed class AlertProducerTests
{
    private const long T0 = 1_780_704_000_000L;
    private const long Hour = 3_600_000L;

    private static AppIdentity App(string sig = "Unsigned", bool userWritable = true) =>
        new(@"C:\Users\u\AppData\Local\Temp\bad.exe", "bad.exe", Publisher: null, sig, userWritable);

    private static NewSessionEvent Event(int appId = 47, long when = T0) =>
        new(AppId: appId,
            SessionId: appId * 10,
            App: App(),
            WanConnection: new PendingConnection(
                Pid: appId, Protocol: Protocol.Tcp,
                RemoteAddress: "1.1.1.1", RemotePort: 443,
                RemoteClass: RemoteClass.Wan,
                BytesUpDelta: 0, BytesDownDelta: 0,
                FirstSeenUnixMs: when, LastSeenUnixMs: when),
            FlushTimeUnixMs: when);

    private sealed class FakeAlertSink : IAlertSink
    {
        private long _nextId = 1;
        // Keyed by (type, entity_kind, entity_ref). Records detail + dismissed flag.
        public Dictionary<(string T, string K, string R), Row> Rows { get; } = new();
        public int UpdateDetailCalls;

        public sealed class Row
        {
            public long AlertId;
            public string Detail = "";
            public bool Dismissed;
            public long CreatedAtUnixMs;
        }

        /// <summary>Simulate a row inserted by a prior process — TryInsert
        /// will return 0 for the same key while this row stays active.</summary>
        public void PrepopulateActive(string type, string kind, string entityRef, string detail)
        {
            Rows[(type, kind, entityRef)] = new Row
            {
                AlertId = ++_nextId,
                Detail = detail,
                Dismissed = false,
            };
        }

        public long TryInsert(
            string type, string severity, string sourceMonitor,
            string entityKind, string entityRef,
            string title, string detail,
            long nowUnixMs, long cooldownMs)
        {
            var key = (type, entityKind, entityRef);
            if (Rows.TryGetValue(key, out var existing))
            {
                if (!existing.Dismissed) return 0;
                if (existing.CreatedAtUnixMs + cooldownMs > nowUnixMs &&
                    existing.Dismissed) return 0;
            }

            var row = new Row
            {
                AlertId = ++_nextId,
                Detail = detail,
                Dismissed = false,
                CreatedAtUnixMs = nowUnixMs,
            };
            Rows[key] = row;
            return row.AlertId;
        }

        public int UpdateDetail(string type, string entityKind, string entityRef, string detail)
        {
            UpdateDetailCalls++;
            var key = (type, entityKind, entityRef);
            if (!Rows.TryGetValue(key, out var row)) return 0;
            if (row.Dismissed) return 0;
            row.Detail = detail;
            return 1;
        }
    }

    [Fact]
    public void FirstObservation_NewKey_InsertsAndRaises()
    {
        var sink = new FakeAlertSink();
        var raised = new List<AlertDto>();
        var producer = new AlertProducer(
            new[] { new UnsignedFromUserPathRule() }, sink, nowProvider: () => T0);
        producer.AlertRaised += dto => raised.Add(dto);

        producer.OnSessionConnectedWan(Event(appId: 47, when: T0));

        sink.Rows.Should().HaveCount(1);
        sink.UpdateDetailCalls.Should().Be(0);
        raised.Should().ContainSingle();
        raised[0].AppId.Should().Be(47);
        raised[0].Detail.Should().Contain("Connections so far: 1.");
    }

    [Fact]
    public void SecondObservation_SameKey_UpdatesDetailAndDoesNotRaise()
    {
        var sink = new FakeAlertSink();
        var raised = new List<AlertDto>();
        var producer = new AlertProducer(
            new[] { new UnsignedFromUserPathRule() }, sink, nowProvider: () => T0);
        producer.AlertRaised += dto => raised.Add(dto);

        producer.OnSessionConnectedWan(Event(appId: 47, when: T0));
        producer.OnSessionConnectedWan(Event(appId: 47, when: T0 + 1_000));
        producer.OnSessionConnectedWan(Event(appId: 47, when: T0 + 2_000));

        // One insert, two updates.
        sink.Rows.Should().HaveCount(1);
        sink.UpdateDetailCalls.Should().Be(2);

        // Only the first observation fired AlertRaised.
        raised.Should().ContainSingle();

        // Detail string reflects the latest count.
        var row = sink.Rows.Values.Single();
        row.Detail.Should().Contain("Connections so far: 3.");
    }

    [Fact]
    public void UpdatedDetail_UsesCachedFirstSeen_NotLatestObservationTimestamp()
    {
        var sink = new FakeAlertSink();
        var producer = new AlertProducer(
            new[] { new UnsignedFromUserPathRule() }, sink, nowProvider: () => T0);

        producer.OnSessionConnectedWan(Event(appId: 47, when: T0));
        var firstDetail = sink.Rows.Values.Single().Detail;

        producer.OnSessionConnectedWan(Event(appId: 47, when: T0 + 5 * Hour));
        var updatedDetail = sink.Rows.Values.Single().Detail;

        var firstTsLine = firstDetail.Split("First connection: ")[1].Split(".")[0];
        var updatedTsLine = updatedDetail.Split("First connection: ")[1].Split(".")[0];

        updatedTsLine.Should().Be(firstTsLine,
            because: "the producer must reuse the cached first_seen so the " +
                     "'First connection: …' phrase stays stable across observations");
    }

    [Fact]
    public void Restart_QuietPath_TryInsertFailsAndProducerSkipsUpdateDetail()
    {
        var sink = new FakeAlertSink();
        // Simulate a row left by a prior service process — still active.
        sink.PrepopulateActive(
            nameof(AlertType.UnsignedFromUserPath),
            nameof(AlertEntityKind.App),
            "47",
            "Pre-restart detail with count from old run");

        var raised = new List<AlertDto>();
        var producer = new AlertProducer(
            new[] { new UnsignedFromUserPathRule() }, sink, nowProvider: () => T0);
        producer.AlertRaised += dto => raised.Add(dto);

        producer.OnSessionConnectedWan(Event(appId: 47, when: T0));

        // No UpdateDetail and no AlertRaised: the producer stays quiet
        // when it finds an active row it didn't create in this process.
        sink.UpdateDetailCalls.Should().Be(0);
        raised.Should().BeEmpty();
        sink.Rows.Values.Single().Detail
            .Should().Be("Pre-restart detail with count from old run");
    }

    [Fact]
    public void DifferentApps_TrackedIndependently()
    {
        var sink = new FakeAlertSink();
        var raised = new List<AlertDto>();
        var producer = new AlertProducer(
            new[] { new UnsignedFromUserPathRule() }, sink, nowProvider: () => T0);
        producer.AlertRaised += dto => raised.Add(dto);

        producer.OnSessionConnectedWan(Event(appId: 47));
        producer.OnSessionConnectedWan(Event(appId: 48));
        producer.OnSessionConnectedWan(Event(appId: 47));  // bumps app 47
        producer.OnSessionConnectedWan(Event(appId: 48));  // bumps app 48

        sink.Rows.Should().HaveCount(2);
        sink.UpdateDetailCalls.Should().Be(2);
        raised.Should().HaveCount(2);
    }

    [Fact]
    public void SignedApp_TriggersNoRuleEvaluation_NoSinkCalls()
    {
        var sink = new FakeAlertSink();
        var producer = new AlertProducer(
            new[] { new UnsignedFromUserPathRule() }, sink, nowProvider: () => T0);

        var evt = new NewSessionEvent(
            AppId: 50,
            SessionId: 500,
            App: App(sig: "Signed"),
            WanConnection: Event().WanConnection,
            FlushTimeUnixMs: T0);

        producer.OnSessionConnectedWan(evt);

        sink.Rows.Should().BeEmpty();
        sink.UpdateDetailCalls.Should().Be(0);
    }

    // ── Epic B fresh-install simulation ─────────────────────────────────

    /// <summary>
    /// Setup-scan-seeded pre-existing apps hit WAN inside the FirstRun
    /// window; the baseline gate must reject every raise. This is the
    /// day-one Chrome/Teams/svchost flood the epic corrects. Compares
    /// against the same producer wiring the service uses in prod.
    /// </summary>
    [Fact]
    public void FreshInstall_TenPreexistingAppsHitWan_ProducesNoFirstRunRaises()
    {
        var installEpoch = T0;
        var sink = new FakeAlertSink();

        // Simulate the setup-scan seeder having written first_seen =
        // install_epoch for every pre-existing app id (1..10). The
        // producer's lookup answers with these values.
        var firstSeen = new Dictionary<int, long>();
        for (int i = 1; i <= 10; i++) firstSeen[i] = installEpoch;

        var raised = new List<AlertDto>();
        var producer = new AlertProducer(
            new IAlertRule[] { new FirstRunWanTalkerRule(installEpoch) },
            sink,
            nowProvider: () => installEpoch + 30_000,
            appFirstSeenLookup: id => firstSeen.TryGetValue(id, out var v) ? v : 0);
        producer.AlertRaised += dto => raised.Add(dto);

        // Every pre-existing app opens a WAN connection 30 s after
        // capture starts — well inside the 60 s first-run window and
        // inside the 48 h baseline window.
        for (int appId = 1; appId <= 10; appId++)
        {
            producer.OnSessionConnectedWan(SignedEvent(appId, when: installEpoch + 30_000));
        }

        sink.Rows.Should().BeEmpty(
            because: "baseline gate must suppress false first-runs for pre-existing apps");
        raised.Should().BeEmpty();
    }

    /// <summary>
    /// A user installs a genuinely new app 49 h after ZenVizor was
    /// installed. It reaches out to the network within 60 s of its
    /// first_seen — the "no permanent disable" acceptance criterion.
    /// </summary>
    [Fact]
    public void PostBaseline_GenuineFirstRun_RaisesNormally()
    {
        var installEpoch = T0;
        var appFirstSeen = installEpoch + FirstRunWanTalkerRule.BaselineWindowMs + Hour; // 49 h after install
        var flushTime = appFirstSeen + 30_000;

        var sink = new FakeAlertSink();
        var raised = new List<AlertDto>();
        var producer = new AlertProducer(
            new IAlertRule[] { new FirstRunWanTalkerRule(installEpoch) },
            sink,
            nowProvider: () => flushTime,
            appFirstSeenLookup: id => id == 99 ? appFirstSeen : 0);
        producer.AlertRaised += dto => raised.Add(dto);

        producer.OnSessionConnectedWan(SignedEvent(appId: 99, when: flushTime));

        raised.Should().ContainSingle()
            .Which.Type.Should().Be(AlertType.FirstRunWanTalker);
    }

    /// <summary>
    /// A dropper installs inside the 48 h baseline window and reaches
    /// out. The Critical UnsignedFromUserPath rule fires regardless of
    /// the baseline window — the whole point of the gate is that it
    /// only affects the Info first-run signal. This is the roadmap
    /// lock at line 94 of the epic doc.
    /// </summary>
    [Fact]
    public void CriticalUnsignedFromUserPath_StillFiresInsideBaselineWindow()
    {
        var installEpoch = T0;
        var flushTime = installEpoch + Hour; // 1 h after install — well inside baseline

        var sink = new FakeAlertSink();
        var raised = new List<AlertDto>();
        var producer = new AlertProducer(
            new IAlertRule[]
            {
                new UnsignedFromUserPathRule(),
                new FirstRunWanTalkerRule(installEpoch),
            },
            sink,
            nowProvider: () => flushTime,
            appFirstSeenLookup: _ => installEpoch); // "pre-existing" — baseline seeder ran

        producer.AlertRaised += dto => raised.Add(dto);

        // Unsigned + user-writable path — Event() default builds this.
        producer.OnSessionConnectedWan(Event(appId: 47, when: flushTime));

        raised.Should().ContainSingle()
            .Which.Type.Should().Be(AlertType.UnsignedFromUserPath);
    }

    private static NewSessionEvent SignedEvent(int appId, long when) => new(
        AppId: appId,
        SessionId: appId * 10,
        App: new AppIdentity(
            ImagePath: $@"C:\Program Files\Vendor{appId}\app.exe",
            ImageName: $"app{appId}.exe",
            Publisher: $"Vendor {appId}",
            SignatureStatus: "Signed",
            IsUserWritablePath: false),
        WanConnection: new PendingConnection(
            Pid: appId, Protocol: Protocol.Tcp,
            RemoteAddress: "1.1.1.1", RemotePort: 443,
            RemoteClass: RemoteClass.Wan,
            BytesUpDelta: 0, BytesDownDelta: 0,
            FirstSeenUnixMs: when, LastSeenUnixMs: when),
        FlushTimeUnixMs: when);

    [Fact]
    public void ForgetActive_AllowsPostCooldownReinsert()
    {
        var sink = new FakeAlertSink();
        var producer = new AlertProducer(
            new[] { new UnsignedFromUserPathRule() }, sink, nowProvider: () => T0);

        producer.OnSessionConnectedWan(Event(appId: 47, when: T0));
        sink.Rows.Should().HaveCount(1);

        // Simulate a dismiss + cooldown elapsing externally — mark the
        // fake row dismissed with an old created_at then forget the
        // producer's in-memory cache.
        var row = sink.Rows.Values.Single();
        row.Dismissed = true;
        row.CreatedAtUnixMs = T0 - 48 * Hour;  // way past cooldown
        producer.ForgetActive(nameof(AlertType.UnsignedFromUserPath), "47");

        producer.OnSessionConnectedWan(Event(appId: 47, when: T0 + 30 * Hour));

        sink.Rows.Should().HaveCount(1); // same key reused; fake overwrites
        sink.Rows.Values.Single().Dismissed.Should().BeFalse();
    }
}
