using System.Text;
using FluentAssertions;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Phase 5c — exercises <see cref="DailyReportCsvWriter"/> against synthesized
/// <see cref="DailyReportResult"/> payloads. The tests target the TextWriter
/// overload so they're encoding-independent; the BOM contract on the Stream
/// overload is covered by its own dedicated test.
/// </summary>
public sealed class DailyReportCsvWriterTests
{
    private static readonly DateOnly ReportDate = new(2026, 6, 8);
    private readonly DailyReportCsvWriter _writer = new();

    // ─── Header block ──────────────────────────────────────────────────────

    [Fact]
    public void Header_IncludesIdentificationDateAnchorAndLocalCallout()
    {
        var output = WriteToString(MinimalReport());

        output.Should().Contain("# ZenVizor daily report");
        output.Should().Contain("# Date: 2026-06-08");
        output.Should().Contain("# Anchor: 7-day average");
        output.Should().Contain("# Generated locally · No network used");
    }

    [Theory]
    [InlineData(AnchorMode.Avg7d,  null, "7-day average")]
    [InlineData(AnchorMode.Avg30d, null, "30-day average")]
    [InlineData(AnchorMode.Avg90d, null, "90-day average")]
    public void Header_AnchorRenderForRollingAverages(AnchorMode mode, string? _, string expected)
    {
        var output = WriteToString(MinimalReport() with { Anchor = mode });
        output.Should().Contain($"# Anchor: {expected}");
    }

    [Fact]
    public void Header_AnchorRenderForSpecificDate()
    {
        var report = MinimalReport() with
        {
            Anchor = AnchorMode.SpecificDate,
            AnchorSpecificDate = new DateOnly(2026, 6, 1),
        };
        var output = WriteToString(report);
        output.Should().Contain("# Anchor: Specific date: 2026-06-01");
    }

    // ─── Hero block ────────────────────────────────────────────────────────

    [Fact]
    public void Hero_EmitsAllMetricsWithFriendlyFormatting()
    {
        var hero = new DailyReportHero(
            TotalUpBytes:           1_200_000_000L,
            TotalDownBytes:         8_700_000_000L,
            WanRatio:               0.73,
            LocalRatio:             0.27,
            TotalDeltaPct:          -3.0,
            UpDeltaPct:             18.0,
            DownDeltaPct:           -6.0,
            BaselineDaysAvailable:  7);
        var output = WriteToString(MinimalReport() with { Hero = hero });

        output.Should().Contain("# Hero");
        output.Should().Contain("Metric,Value");
        output.Should().Contain("TotalBytes,9.2 GB");           // 1.2 + 8.7 ≈ 9.9 raw bytes = 9.21 GiB
        output.Should().Contain("UploadedBytes,1.1 GB");        // 1.2e9 ≈ 1.12 GiB
        output.Should().Contain("DownloadedBytes,8.1 GB");      // 8.7e9 ≈ 8.10 GiB
        output.Should().Contain("WANPercent,73%");
        output.Should().Contain("LocalPercent,27%");
        output.Should().Contain("TotalDeltaVsAnchorPct,-3.0");
        output.Should().Contain("UpDeltaVsAnchorPct,18.0");
        output.Should().Contain("DownDeltaVsAnchorPct,-6.0");
    }

    // ─── Top apps block ────────────────────────────────────────────────────

    [Fact]
    public void TopApps_RowPerEntry_WithHeader()
    {
        var report = MinimalReport() with
        {
            TopApps = new[]
            {
                new DailyReportAppRow(1, "claude.exe",  @"C:\bin\claude.exe",  "Anthropic",            "Signed",   false, 142_000_000L, 3_100_000_000L, false),
                new DailyReportAppRow(2, "msedge.exe",  @"C:\bin\msedge.exe",  "Microsoft Corporation","Signed",   false,  88_000_000L, 2_200_000_000L, false),
            },
        };
        var output = WriteToString(report);

        output.Should().Contain("# Top apps (ranked by total bytes)");
        output.Should().Contain("App,Publisher,Signature,UploadedBytes,DownloadedBytes,UserWritablePath");
        output.Should().Contain("claude.exe,Anthropic,Signed,135 MB,2.9 GB,false");
        output.Should().Contain("msedge.exe,Microsoft Corporation,Signed,83.9 MB,2.0 GB,false");
    }

    [Fact]
    public void TopApps_PublisherWithComma_GetsWrappedInQuotes()
    {
        var report = MinimalReport() with
        {
            TopApps = new[]
            {
                new DailyReportAppRow(1, "claude.exe", "p", "Anthropic, PBC", "Signed", false, 0, 0, false),
            },
        };
        var output = WriteToString(report);
        output.Should().Contain("claude.exe,\"Anthropic, PBC\",Signed,");
    }

    [Fact]
    public void TopApps_BracketedImageName_StaysIntact_NoQuotes()
    {
        var report = MinimalReport() with
        {
            TopApps = new[]
            {
                new DailyReportAppRow(1, "svchost.exe [Dnscache, NlaSvc]", "p", "Microsoft", "Signed", false, 0, 0, false),
            },
        };
        var output = WriteToString(report);
        // Bracketed name has a comma → must be quote-wrapped.
        output.Should().Contain("\"svchost.exe [Dnscache, NlaSvc]\",Microsoft,Signed,");
    }

    // ─── Uncommon talkers block ────────────────────────────────────────────

    [Fact]
    public void UncommonTalkers_GroupsAllCategoriesUnderSingleHeader()
    {
        var report = MinimalReport() with
        {
            UncommonTalkers = new[]
            {
                new DailyReportTalker(UncommonCategory.NewToday,      1, "a.exe", "Pub", "Signed",   "First publisher seen.",  false),
                new DailyReportTalker(UncommonCategory.UnusualVolume, 2, "b.exe", "Pub", "Signed",   "Uploaded 5 MB; 7x med.", false),
                new DailyReportTalker(UncommonCategory.RiskyPaths,    3, "c.exe", null,  "Unsigned", "Running from %TEMP%.",   false),
            },
        };
        var output = WriteToString(report);

        output.Should().Contain("# Uncommon talkers");
        output.Should().Contain("Category,App,Publisher,Signature,Reason");
        output.Should().Contain("NewToday,a.exe,Pub,Signed,First publisher seen.");
        output.Should().Contain("UnusualVolume,b.exe,Pub,Signed,Uploaded 5 MB; 7x med.");
        output.Should().Contain("RiskyPaths,c.exe,,Unsigned,Running from %TEMP%.");
    }

    // ─── Notable block ─────────────────────────────────────────────────────

    [Fact]
    public void Notable_RowPerEntry_WithEventTimeHHmmss()
    {
        // 2026-06-08 14:22:00 local — build via DateOnly + TimeOnly to be
        // host-timezone independent (the serializer uses ToLocalTime, so the
        // bytes-on-disk hh:mm:ss matches the host's local interpretation).
        var localEventTime = new DateTime(2026, 6, 8, 14, 22, 0, DateTimeKind.Local);
        var unixMs = new DateTimeOffset(localEventTime).ToUnixTimeMilliseconds();

        var report = MinimalReport() with
        {
            Notable = new[]
            {
                new DailyReportNotable(
                    Severity:        NotableSeverity.Critical,
                    Title:           "Unsigned binary from a user-writable path",
                    Detail:          "updater_x.exe is unsigned.",
                    AppId:           1,
                    ImageName:       "updater_x.exe",
                    Pid:             8841,
                    EventTimeUnixMs: unixMs,
                    AlertId:         1),
            },
        };
        var output = WriteToString(report);

        output.Should().Contain("# Notable today");
        output.Should().Contain("Severity,Title,Detail,App,Pid,EventTime");
        output.Should().Contain("Critical,Unsigned binary from a user-writable path,updater_x.exe is unsigned.,updater_x.exe,8841,14:22:00");
    }

    // ─── Empty result ──────────────────────────────────────────────────────

    [Fact]
    public void Empty_AllSectionHeadersAndColumnHeadersStillPresent()
    {
        var output = WriteToString(MinimalReport());
        output.Should().Contain("# Hero");
        output.Should().Contain("# Top apps");
        output.Should().Contain("# Uncommon talkers");
        output.Should().Contain("# Notable today");
        output.Should().Contain("App,Publisher,Signature,UploadedBytes,DownloadedBytes,UserWritablePath");
        output.Should().Contain("Category,App,Publisher,Signature,Reason");
        output.Should().Contain("Severity,Title,Detail,App,Pid,EventTime");
    }

    // ─── RFC-4180 escape correctness ───────────────────────────────────────

    [Theory]
    [InlineData("plain",            "plain")]
    [InlineData("with,comma",       "\"with,comma\"")]
    [InlineData("with\"quote",      "\"with\"\"quote\"")]
    [InlineData("with\nnewline",    "\"with\nnewline\"")]
    [InlineData("with\rreturn",     "\"with\rreturn\"")]
    [InlineData("",                 "")]
    public void Escape_AppliesRfc4180Rules(string input, string expected)
    {
        DailyReportCsvWriter.Escape(input).Should().Be(expected);
    }

    // ─── Stream overload: UTF-8 BOM contract ───────────────────────────────

    [Fact]
    public void StreamOverload_WritesUtf8Bom()
    {
        using var stream = new MemoryStream();
        _writer.Write(MinimalReport(), stream);

        var bytes = stream.ToArray();
        bytes.Length.Should().BeGreaterThan(3);
        // UTF-8 BOM = 0xEF 0xBB 0xBF
        bytes[0].Should().Be(0xEF);
        bytes[1].Should().Be(0xBB);
        bytes[2].Should().Be(0xBF);

        // And the content after the BOM should decode as the same text the
        // TextWriter overload produces.
        var decoded = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        decoded.Should().Contain("# ZenVizor daily report");
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private string WriteToString(DailyReportResult report)
    {
        var sw = new StringWriter();
        _writer.Write(report, sw);
        return sw.ToString();
    }

    private static DailyReportResult MinimalReport() => new(
        Date:               ReportDate,
        Anchor:             AnchorMode.Avg7d,
        AnchorSpecificDate: null,
        Hero:               new DailyReportHero(0, 0, 0, 0, 0, 0, 0, 0),
        HourlyTraffic:      Array.Empty<DailyReportHourPoint>(),
        TopApps:            Array.Empty<DailyReportAppRow>(),
        UncommonTalkers:    Array.Empty<DailyReportTalker>(),
        Notable:            Array.Empty<DailyReportNotable>());
}
