using System.Text.RegularExpressions;
using FluentAssertions;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Phase 5d — exercises <see cref="DailyReportHtmlWriter"/>. The most
/// important contracts here are the "zero remote refs" guarantee
/// (brief §15) and HTML escaping correctness. Visual fidelity is
/// validated manually in a browser.
/// </summary>
public sealed class DailyReportHtmlWriterTests
{
    private static readonly DateOnly ReportDate = new(2026, 6, 8);
    private readonly DailyReportHtmlWriter _writer = new();

    // ─── Zero-remote-ref contract (brief §15) ──────────────────────────────

    [Fact]
    public void NoRemoteReferences_NoHttpUrls()
    {
        var output = WriteToString(FullReport());
        // Match http:// or https:// URLs. Self-hosted relative paths (none in
        // this writer) would not trip this check.
        var urls = Regex.Matches(output, @"https?:\/\/[^\s""'<>]+");
        urls.Should().BeEmpty(because: "the export must not reference any remote resource");
    }

    [Fact]
    public void NoRemoteReferences_NoFontFace()
    {
        var output = WriteToString(FullReport());
        output.Should().NotContain("@font-face",
            because: "@font-face directives load font binaries which would fire requests");
    }

    [Fact]
    public void NoRemoteReferences_NoExternalLinksInHead()
    {
        var output = WriteToString(FullReport());
        // <link rel="stylesheet" href="..."> or <link rel="..." href="..."> with remote URLs.
        output.Should().NotMatch("*<link*href=\"http*",
            because: "remote stylesheets are forbidden");
        output.Should().NotContain("<script src=",
            because: "no external script sources");
    }

    [Fact]
    public void Header_CarriesGeneratedLocallyCallout()
    {
        var output = WriteToString(FullReport());
        output.Should().Contain("Generated locally");
        output.Should().Contain("No network used");
    }

    // ─── HTML escape correctness ──────────────────────────────────────────

    [Theory]
    [InlineData("plain",            "plain")]
    [InlineData("a&b",              "a&amp;b")]
    [InlineData("<script>",         "&lt;script&gt;")]
    [InlineData("\"quoted\"",       "&quot;quoted&quot;")]
    [InlineData("it's",             "it&#39;s")]
    [InlineData("",                 "")]
    public void Html_EscapesSpecials(string input, string expected)
    {
        DailyReportHtmlWriter.Html(input).Should().Be(expected);
    }

    [Fact]
    public void Publisher_WithAmpersand_RendersEscaped()
    {
        var report = MinimalReport() with
        {
            TopApps = new[]
            {
                new DailyReportAppRow(1, "x.exe", "p", "Brown & Co.", "Signed", false, 0, 0, false),
            },
        };
        var output = WriteToString(report);
        output.Should().Contain("Brown &amp; Co.");
        output.Should().NotContain("Brown & Co."); // raw form must not leak
    }

    // ─── Document shape ───────────────────────────────────────────────────

    [Fact]
    public void Document_HasDoctypeAndHtmlLang()
    {
        var output = WriteToString(MinimalReport());
        output.Should().StartWith("<!DOCTYPE html>");
        output.Should().Contain("<html lang=\"en\">");
    }

    [Fact]
    public void Document_HasInlineStyleBlock()
    {
        var output = WriteToString(MinimalReport());
        output.Should().Contain("<style>");
        output.Should().Contain("</style>");
        // Token marker — if this disappears, the writer dropped its CSS.
        output.Should().Contain("--surface-card");
        output.Should().Contain("--status-critical");
    }

    [Fact]
    public void Document_HasAllFourSurfaceSections()
    {
        var output = WriteToString(FullReport());
        output.Should().Contain(">Summary<");
        output.Should().Contain(">Top apps<");
        output.Should().Contain(">Uncommon talkers<");
        output.Should().Contain(">Notable today<");
    }

    [Fact]
    public void Summary_RendersHeroNumerics()
    {
        var hero = new DailyReportHero(
            TotalUpBytes:   1_200_000_000L,
            TotalDownBytes: 8_700_000_000L,
            WanRatio:       0.73,
            LocalRatio:     0.27,
            TotalDeltaPct:  -3.0,
            UpDeltaPct:     18.0,
            DownDeltaPct:   -6.0);
        var output = WriteToString(MinimalReport() with { Hero = hero });

        // Total = 9.9e9 bytes ≈ 9.2 GB; up ≈ 1.1 GB; down ≈ 8.1 GB.
        output.Should().Contain("Total traffic");
        output.Should().Contain("Uploaded");
        output.Should().Contain("Downloaded");
        output.Should().Contain("9.2");      // total
        output.Should().Contain("1.1");      // up
        output.Should().Contain("8.1");      // down
        output.Should().Contain("73%");
        output.Should().Contain("27%");
    }

    [Fact]
    public void TopApps_SignaturePillUsesStatusClass()
    {
        var report = MinimalReport() with
        {
            TopApps = new[]
            {
                new DailyReportAppRow(1, "signed.exe",    "p", "p", "Signed",   false, 0, 0, false),
                new DailyReportAppRow(2, "unchecked.exe", "p", "p", "Unchecked",false, 0, 0, false),
                new DailyReportAppRow(3, "unsigned.exe",  "p", "p", "Unsigned", true,  0, 0, false),
            },
        };
        var output = WriteToString(report);

        output.Should().Contain("class=\"sig-pill sig-Signed\"");
        output.Should().Contain("class=\"sig-pill sig-Unchecked\"");
        output.Should().Contain("class=\"sig-pill sig-Unsigned\"");
    }

    [Fact]
    public void Notable_GroupsBySeverity_WithSeverityClass()
    {
        var t = new DateTimeOffset(2026, 6, 8, 14, 22, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var report = MinimalReport() with
        {
            Notable = new[]
            {
                new DailyReportNotable(NotableSeverity.Critical, "Critical title", "Detail", 1, "a.exe", 1, t, 1),
                new DailyReportNotable(NotableSeverity.Warning,  "Warning title",  "Detail", 2, "b.exe", 2, t, 2),
                new DailyReportNotable(NotableSeverity.Info,     "Info title",     "Detail", 3, "c.exe", 3, t, 3),
            },
        };
        var output = WriteToString(report);

        output.Should().Contain("notable-section Critical");
        output.Should().Contain("notable-section Warning");
        output.Should().Contain("notable-section Info");
        output.Should().Contain("Critical title");
        output.Should().Contain("Warning title");
        output.Should().Contain("Info title");
    }

    [Fact]
    public void UncommonTalkers_RendersCategoriesPresentOnly()
    {
        var report = MinimalReport() with
        {
            UncommonTalkers = new[]
            {
                new DailyReportTalker(UncommonCategory.NewToday, 1, "fresh.exe", "Pub", "Signed", "First WAN connection.", false),
            },
        };
        var output = WriteToString(report);

        output.Should().Contain(">New today<");
        output.Should().Contain("fresh.exe");
        output.Should().Contain("First WAN connection.");
        // Empty categories don't render their headers.
        output.Should().NotContain(">Unusual volume<");
        output.Should().NotContain(">Risky paths<");
    }

    [Fact]
    public void EmptyDay_RendersFriendlyPlaceholders()
    {
        var output = WriteToString(MinimalReport());
        output.Should().Contain("No apps recorded for this date.");
        output.Should().Contain("Nothing notable today.");
        output.Should().Contain("A deliberately quiet day. No app strayed from its usual pattern.");
    }

    // ─── File size budget (brief §10) ─────────────────────────────────────

    [Fact]
    public void FileSize_RealisticPayloadStaysWellUnder500Kb()
    {
        using var stream = new MemoryStream();
        _writer.Write(FullReport(), stream);
        stream.Length.Should().BeLessThan(100 * 1024, because: "brief §10 caps the export at <500 KB; a realistic day fits comfortably under 100 KB");
    }

    // ─── helpers ──────────────────────────────────────────────────────────

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
        Hero:               new DailyReportHero(0, 0, 0, 0, 0, 0, 0),
        HourlyTraffic:      Array.Empty<DailyReportHourPoint>(),
        TopApps:            Array.Empty<DailyReportAppRow>(),
        UncommonTalkers:    Array.Empty<DailyReportTalker>(),
        Notable:            Array.Empty<DailyReportNotable>());

    private static DailyReportResult FullReport()
    {
        var t = new DateTimeOffset(2026, 6, 8, 14, 22, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return new DailyReportResult(
            Date:               ReportDate,
            Anchor:             AnchorMode.Avg7d,
            AnchorSpecificDate: null,
            Hero:               new DailyReportHero(1_200_000_000L, 8_700_000_000L, 0.73, 0.27, -3, 18, -6),
            HourlyTraffic:      Array.Empty<DailyReportHourPoint>(),
            TopApps: new[]
            {
                new DailyReportAppRow(1, "claude.exe",  @"C:\bin\claude.exe", "Anthropic, PBC",           "Signed",   false, 142_000_000L, 3_100_000_000L, false),
                new DailyReportAppRow(2, "msedge.exe",  @"C:\bin\msedge.exe", "Microsoft",                "Signed",   false,  88_000_000L, 2_200_000_000L, false),
                new DailyReportAppRow(3, "updater.exe", @"C:\Users\m\Temp\updater.exe", null,             "Unsigned", true,    1_000_000L,    2_000_000L, true),
            },
            UncommonTalkers: new[]
            {
                new DailyReportTalker(UncommonCategory.NewToday,      1, "fresh.exe",   "Globex", "Signed",   "First publisher seen on this machine.",          false),
                new DailyReportTalker(UncommonCategory.UnusualVolume, 2, "svchost.exe", "Microsoft", "Signed", "Uploaded 412 MB; 3.1× the 7-day median.",       false),
                new DailyReportTalker(UncommonCategory.RiskyPaths,    3, "updater.exe", null, "Unsigned",     "Unsigned, running from %TEMP%.",                false),
            },
            Notable: new[]
            {
                new DailyReportNotable(NotableSeverity.Critical, "Unsigned binary from a user-writable path",
                    "updater.exe is unsigned and runs from %TEMP%.", 3, "updater.exe", 8841, t, 1),
            });
    }
}
