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
