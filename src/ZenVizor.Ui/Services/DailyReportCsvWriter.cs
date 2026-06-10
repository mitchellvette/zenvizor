using System.Globalization;
using System.IO;
using System.Text;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Phase 5c — serializes a <see cref="DailyReportResult"/> to CSV. The
/// output is sectioned (one block per surface — Hero / Top Apps / Uncommon
/// talkers / Notable today), with section-delimiter comment lines so a
/// human reading the raw file can navigate it, while still loading cleanly
/// into Excel / LibreOffice (both treat <c>#</c>-prefixed lines as
/// regular rows, which is acceptable — the column headers within each
/// section disambiguate).
/// </summary>
/// <remarks>
/// Filename convention (brief §17): <c>zenvizor-report-YYYY-MM-DD.csv</c>.
/// Encoding: UTF-8 with BOM, so Excel auto-detects encoding (the BOM is
/// what lets `Anthropic, PBC` survive as-is instead of getting mangled to
/// `Anthropic, PBC` on Windows-1252 misdetection).
///
/// Brief §15 + §17: the report is self-contained and locally generated.
/// The header block carries the <c>Generated locally · No network used</c>
/// callout for both CSV and HTML serializers so the user reads the same
/// affirmation in either artifact.
/// </remarks>
public sealed class DailyReportCsvWriter
{
    // Public Stream overload — applies the UTF-8 BOM encoding so Excel
    // opens the file with the right code page on Windows. The TextWriter
    // overload is what tests target; it accepts any writer (e.g. a
    // StringWriter) without prescribing an encoding.
    public void Write(DailyReportResult report, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        // Leave the underlying stream open after Dispose so the caller can
        // control its own lifetime — File.Create + Write pattern in
        // OnExportCsvClick wraps this in a using-statement of its own.
        using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 4096,
            leaveOpen: true);
        Write(report, writer);
    }

    public void Write(DailyReportResult report, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        WriteHeader(writer, report);
        writer.WriteLine();
        WriteHero(writer, report.Hero);
        writer.WriteLine();
        WriteTopApps(writer, report.TopApps);
        writer.WriteLine();
        WriteUncommonTalkers(writer, report.UncommonTalkers);
        writer.WriteLine();
        WriteNotable(writer, report.Notable);
        writer.Flush();
    }

    private static void WriteHeader(TextWriter writer, DailyReportResult r)
    {
        writer.WriteLine("# ZenVizor daily report");
        writer.WriteLine($"# Date: {r.Date:yyyy-MM-dd}");
        writer.WriteLine($"# Anchor: {FormatAnchor(r.Anchor, r.AnchorSpecificDate)}");
        writer.WriteLine("# Generated locally · No network used");
    }

    private static void WriteHero(TextWriter writer, DailyReportHero h)
    {
        var totalBytes = h.TotalUpBytes + h.TotalDownBytes;
        writer.WriteLine("# Hero");
        writer.WriteLine("Metric,Value");
        writer.WriteLine($"TotalBytes,{FormatBytes(totalBytes)}");
        writer.WriteLine($"UploadedBytes,{FormatBytes(h.TotalUpBytes)}");
        writer.WriteLine($"DownloadedBytes,{FormatBytes(h.TotalDownBytes)}");
        writer.WriteLine($"WANPercent,{FormatPercent(h.WanRatio)}");
        writer.WriteLine($"LocalPercent,{FormatPercent(h.LocalRatio)}");
        writer.WriteLine($"TotalDeltaVsAnchorPct,{FormatSignedPct(h.TotalDeltaPct)}");
        writer.WriteLine($"UpDeltaVsAnchorPct,{FormatSignedPct(h.UpDeltaPct)}");
        writer.WriteLine($"DownDeltaVsAnchorPct,{FormatSignedPct(h.DownDeltaPct)}");
    }

    private static void WriteTopApps(TextWriter writer, IReadOnlyList<DailyReportAppRow> rows)
    {
        writer.WriteLine("# Top apps (ranked by total bytes)");
        writer.WriteLine("App,Publisher,Signature,UploadedBytes,DownloadedBytes,UserWritablePath");
        foreach (var r in rows)
        {
            writer.WriteLine(string.Join(",", new[]
            {
                Escape(r.ImageName),
                Escape(r.Publisher ?? ""),
                Escape(r.SignatureStatus),
                Escape(FormatBytes(r.BytesUp)),
                Escape(FormatBytes(r.BytesDown)),
                r.IsUserWritablePath ? "true" : "false",
            }));
        }
    }

    private static void WriteUncommonTalkers(TextWriter writer, IReadOnlyList<DailyReportTalker> rows)
    {
        writer.WriteLine("# Uncommon talkers");
        writer.WriteLine("Category,App,Publisher,Signature,Reason");
        foreach (var t in rows)
        {
            writer.WriteLine(string.Join(",", new[]
            {
                t.Category.ToString(),
                Escape(t.ImageName),
                Escape(t.Publisher ?? ""),
                Escape(t.SignatureStatus),
                Escape(t.Reason),
            }));
        }
    }

    private static void WriteNotable(TextWriter writer, IReadOnlyList<DailyReportNotable> rows)
    {
        writer.WriteLine("# Notable today");
        writer.WriteLine("Severity,Title,Detail,App,Pid,EventTime");
        foreach (var n in rows)
        {
            var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(n.EventTimeUnixMs)
                .ToLocalTime()
                .ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(",", new[]
            {
                n.Severity.ToString(),
                Escape(n.Title),
                Escape(n.Detail),
                Escape(n.ImageName),
                n.Pid.ToString(CultureInfo.InvariantCulture),
                eventTime,
            }));
        }
    }

    // ─── Formatting helpers ────────────────────────────────────────────────

    // RFC 4180 escaping. Quote-wrap a field if it contains a comma, double-
    // quote, CR, or LF; internal double-quotes are doubled.
    internal static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needsQuotes =
            value.IndexOf(',') >= 0 ||
            value.IndexOf('"') >= 0 ||
            value.IndexOf('\n') >= 0 ||
            value.IndexOf('\r') >= 0;
        if (!needsQuotes) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)
             + " " + units[unit];
    }

    internal static string FormatPercent(double ratio01)
    {
        var pct = (int)Math.Round(Math.Clamp(ratio01, 0, 1) * 100);
        return pct.ToString(CultureInfo.InvariantCulture) + "%";
    }

    // Signed decimal percent (no glyph). Negative numbers render as e.g.
    // "-3.0"; positive as "18.0"; zero as "0.0". The DTO delta values are
    // already signed.
    internal static string FormatSignedPct(double pct) =>
        pct.ToString("0.0", CultureInfo.InvariantCulture);

    internal static string FormatAnchor(AnchorMode anchor, DateOnly? specificDate) => anchor switch
    {
        AnchorMode.SpecificDate => $"Specific date: {specificDate?.ToString("yyyy-MM-dd") ?? "(none)"}",
        AnchorMode.Avg7d        => "7-day average",
        AnchorMode.Avg30d       => "30-day average",
        AnchorMode.Avg90d       => "90-day average",
        _                       => anchor.ToString(),
    };
}
