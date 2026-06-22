// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests.Alerts;

/// <summary>
/// Unit tests for <see cref="LargeDownloadRule"/>. Verifies the
/// per-connection cumulative gate, the 60 s window, the threshold
/// reading via <see cref="StaticAlertSettingsLookup"/>, dedup against
/// the same connection, and the detail multi-PID enrichment.
/// </summary>
public sealed class LargeDownloadRuleTests
{
    private const long T0 = 1_780_704_000_000L;

    private static AppIdentity App(string name = "chrome.exe", string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe") =>
        new(ImagePath: path, ImageName: name, Publisher: "Google LLC",
            SignatureStatus: "Signed", IsUserWritablePath: false);

    private static FlushConnectionState Conn(
        int pid = 4242,
        int sessionId = 100,
        int appId = 7,
        string remote = "203.0.113.1",
        int port = 443,
        long bytesDown = 0,
        long firstSeen = T0,
        long lastSeen = T0,
        AppIdentity? app = null) =>
        new(
            Pid:                pid,
            AppId:              appId,
            SessionId:          sessionId,
            App:                app ?? App(),
            Protocol:           Protocol.Tcp,
            RemoteAddress:      remote,
            RemotePort:         port,
            RemoteClass:        RemoteClass.Wan,
            BytesUpDelta:       0,
            BytesDownDelta:     bytesDown,
            FirstSeenUnixMs:    firstSeen,
            LastSeenUnixMs:     lastSeen);

    private static FlushAlertEvent Evt(long flushTime, params FlushConnectionState[] conns) =>
        new(FlushTimeUnixMs: flushTime, FlushIntervalMs: 5_000, Connections: conns);

    [Fact]
    public void Evaluate_HitsThresholdInWindow_RaisesOnce()
    {
        var settings = new StaticAlertSettingsLookup(largeDownloadMb: 50);
        var rule = new LargeDownloadRule(settings);

        // 60 MB landed in a single flush within 5 s of first-seen.
        var bytes = 60L * 1024L * 1024L;
        var results = rule.Evaluate(Evt(T0 + 5_000, Conn(bytesDown: bytes, firstSeen: T0, lastSeen: T0 + 5_000))).ToList();

        results.Should().ContainSingle();
        var (req, detail) = results[0];
        req.Type.Should().Be(AlertType.LargeDownload);
        req.Severity.Should().Be(NotableSeverity.Info);
        req.SourceMonitor.Should().Be(SourceMonitor.Capture);
        req.EntityKind.Should().Be(AlertEntityKind.App);
        req.EntityRef.Should().Be("7");
        req.Title.Should().Be("Large download by chrome.exe");
        detail.Should().Contain("60 MB");
        detail.Should().Contain("203.0.113.1:443");
        detail.Should().Contain("PID 4242");
        detail.Should().NotContain("—");
    }

    [Fact]
    public void Evaluate_AccumulatesAcrossFlushes_FiresWhenCumulativeCrossesThreshold()
    {
        var settings = new StaticAlertSettingsLookup(largeDownloadMb: 50);
        var rule = new LargeDownloadRule(settings);

        // First flush: 20 MB. Second flush: 35 MB. Cumulative crosses 50 MB on flush 2.
        rule.Evaluate(Evt(T0 + 5_000,
            Conn(bytesDown: 20L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 5_000))).Should().BeEmpty();

        var second = rule.Evaluate(Evt(T0 + 10_000,
            Conn(bytesDown: 35L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 10_000))).ToList();
        second.Should().ContainSingle();
    }

    [Fact]
    public void Evaluate_HitsThresholdAfterWindow_DoesNotFire()
    {
        var settings = new StaticAlertSettingsLookup(largeDownloadMb: 50);
        var rule = new LargeDownloadRule(settings);

        // 60 MB but the window has elapsed (lastSeen - firstSeen > 60s).
        var results = rule.Evaluate(Evt(T0 + 70_000,
            Conn(bytesDown: 60L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 70_000))).ToList();

        results.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_SameConnectionInLaterFlush_DoesNotDoubleFire()
    {
        var settings = new StaticAlertSettingsLookup(largeDownloadMb: 50);
        var rule = new LargeDownloadRule(settings);

        // Flush 1: alert raised.
        var first = rule.Evaluate(Evt(T0 + 5_000,
            Conn(bytesDown: 60L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 5_000))).ToList();
        first.Should().ContainSingle();

        // Flush 2: same connection keeps pulling bytes — must NOT
        // produce a second raise from the same key. Producer dedupe
        // would absorb it anyway, but the rule shouldn't try.
        var second = rule.Evaluate(Evt(T0 + 10_000,
            Conn(bytesDown: 30L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 10_000))).ToList();
        second.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ThresholdHonorsSettingsLookup()
    {
        // Bump threshold to 200 MB; a 100 MB download no longer fires.
        var settings = new StaticAlertSettingsLookup(largeDownloadMb: 200);
        var rule = new LargeDownloadRule(settings);

        rule.Evaluate(Evt(T0 + 5_000,
            Conn(bytesDown: 100L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 5_000))).Should().BeEmpty();

        // 250 MB clears the higher bar.
        var results = rule.Evaluate(Evt(T0 + 10_000,
            Conn(sessionId: 200, remote: "198.51.100.7", bytesDown: 250L * 1024L * 1024L,
                 firstSeen: T0 + 5_000, lastSeen: T0 + 10_000))).ToList();
        results.Should().ContainSingle();
    }

    [Fact]
    public void Evaluate_MultiplePidsSameApp_DetailEnumeratesPids()
    {
        var settings = new StaticAlertSettingsLookup(largeDownloadMb: 50);
        var rule = new LargeDownloadRule(settings);

        var conn1 = Conn(pid: 1111, sessionId: 100, remote: "203.0.113.1",
                         bytesDown: 60L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 5_000);
        var conn2 = Conn(pid: 2222, sessionId: 200, remote: "203.0.113.2",
                         bytesDown: 70L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 5_000);

        var results = rule.Evaluate(Evt(T0 + 5_000, conn1, conn2)).ToList();

        results.Should().HaveCount(2);
        // The second raise's detail aggregates both connections.
        var lastDetail = results[1].Detail;
        lastDetail.Should().Contain("Total qualifying downloads: 2");
        lastDetail.Should().Contain("PIDs 1111, 2222");
    }

    [Fact]
    public void Evaluate_BelowThreshold_ReturnsEmpty()
    {
        var settings = new StaticAlertSettingsLookup(largeDownloadMb: 50);
        var rule = new LargeDownloadRule(settings);

        rule.Evaluate(Evt(T0 + 5_000,
            Conn(bytesDown: 10L * 1024L * 1024L, firstSeen: T0, lastSeen: T0 + 5_000))).Should().BeEmpty();
    }

    [Fact]
    public void CooldownMs_Is24Hours()
    {
        new LargeDownloadRule(new StaticAlertSettingsLookup())
            .CooldownMs.Should().Be((long)TimeSpan.FromHours(24).TotalMilliseconds);
    }
}
