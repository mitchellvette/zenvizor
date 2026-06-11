using System.Globalization;
using System.IO;
using System.Text;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Phase 5d — serializes a <see cref="DailyReportResult"/> as a
/// self-contained HTML document. Opens in any browser, archives to disk,
/// pastes into incident docs. Per the brief §15 + §17 / mockup page 11:
///
/// <list type="bullet">
///   <item>All CSS inlined in a single &lt;style&gt; block — token values
///   projected from <c>docs/design/colors_and_type.css</c>. Light theme
///   only (mockup hand-off page 11 is light); a future polish round can
///   add prefers-color-scheme support if requested.</item>
///   <item><b>Zero remote refs.</b> No CDN, no Google Fonts, no analytics,
///   no embedded images. Font-family falls back to system-ui stacks
///   (Urbanist → Segoe UI Variable → Segoe UI → system-ui →
///   -apple-system → sans-serif) — preserves the brand look on a machine
///   with Urbanist installed, degrades gracefully otherwise. This is the
///   brief's hard contract verified via DevTools' network panel: zero
///   requests fired when the HTML loads.</item>
///   <item>Top-right "Generated locally · No network used" callout —
///   visible affirmation to the user that the export is offline-safe.</item>
///   <item>Print-friendly via a basic @media print rule (page breaks
///   avoided inside cards, background colors preserved).</item>
///   <item>Target size &lt;500 KB; typical payload (10 apps + few talkers +
///   few notable) lands ~15-20 KB.</item>
/// </list>
/// </summary>
public sealed class DailyReportHtmlWriter
{
    public void Write(DailyReportResult report, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true);
        Write(report, writer);
    }

    public void Write(DailyReportResult report, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("<!DOCTYPE html>");
        writer.WriteLine("<html lang=\"en\">");
        WriteHead(writer, report);
        writer.WriteLine("<body>");
        writer.WriteLine("  <div class=\"page\">");
        WriteHeader(writer, report);
        WriteSummary(writer, report.Hero);
        WriteProportion(writer, report.Hero);
        WriteTopApps(writer, report.TopApps);
        WriteUncommonTalkers(writer, report.UncommonTalkers);
        WriteNotable(writer, report.Notable);
        writer.WriteLine("  </div>");
        writer.WriteLine("</body>");
        writer.WriteLine("</html>");
        writer.Flush();
    }

    // ─── <head> + inline CSS ───────────────────────────────────────────────

    private static void WriteHead(TextWriter writer, DailyReportResult r)
    {
        writer.WriteLine("<head>");
        writer.WriteLine("  <meta charset=\"utf-8\">");
        writer.WriteLine($"  <title>{Html("ZenVizor daily report: " + r.Date.ToString("yyyy-MM-dd"))}</title>");
        writer.WriteLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        writer.WriteLine("  <meta name=\"generator\" content=\"ZenVizor\">");
        writer.WriteLine("  <style>");
        writer.Write(InlineCss);
        writer.WriteLine("  </style>");
        writer.WriteLine("</head>");
    }

    // Inline CSS — light-theme token values copied from
    // docs/design/colors_and_type.css. Keep this minimal: only what the
    // export actually paints. When the design tokens change, update this
    // block AND the crosswalk in colors_and_type.css to keep the two
    // surfaces aligned.
    private const string InlineCss = """
    :root {
      --surface-background: #f8f9fc;
      --surface-card: #ffffff;
      --text-primary: #1a1c26;
      --text-secondary: #565d72;
      --text-tertiary: #8b90a4;
      --border-card: rgba(20,24,46,0.08);
      --border-subtle: rgba(20,24,46,0.07);
      --accent-text: #561fb0;
      --accent-subtle: rgba(109,63,209,0.10);
      --chart-up-series: #6d3fd1;
      --chart-down-series: #20b6c6;
      --chart-wan: #0072B2;
      --chart-local: #009E73;
      --status-success: #06b6a3;
      --status-success-bg: rgba(6,182,163,0.14);
      --status-caution: #ec9a0b;
      --status-caution-bg: rgba(236,154,11,0.16);
      --status-caution-text: #8a5a00;
      --status-critical: #d62b62;
      --status-critical-bg: rgba(214,43,98,0.12);
      --status-neutral: #4d5fd0;
      --status-neutral-bg: rgba(77,95,208,0.13);
      --font-display: 'Urbanist', 'Segoe UI Variable', 'Segoe UI', system-ui, -apple-system, sans-serif;
      --font-mono: 'Overpass Mono', 'Cascadia Code', ui-monospace, 'SF Mono', Menlo, monospace;
    }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; background: var(--surface-background); color: var(--text-primary); font-family: var(--font-display); -webkit-font-smoothing: antialiased; }
    .page { max-width: 900px; margin: 0 auto; padding: 40px 24px 64px; }
    .doc-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 24px; padding-bottom: 16px; border-bottom: 1px solid var(--border-subtle); }
    .doc-title { font-weight: 600; font-size: 24px; line-height: 1.2; margin: 0; }
    .doc-subtitle { font-size: 14px; color: var(--text-secondary); margin: 6px 0 0; }
    .doc-callout { text-align: right; font-size: 12px; color: var(--text-tertiary); line-height: 1.4; }
    .doc-callout strong { color: var(--text-secondary); font-weight: 600; }
    .section { margin-top: 40px; }
    .section-header { font-weight: 600; font-size: 18px; margin: 0 0 12px; }
    .summary { display: flex; gap: 48px; padding: 20px 24px; background: var(--surface-card); border: 1px solid var(--border-card); border-radius: 10px; }
    .summary-item { display: flex; flex-direction: column; gap: 4px; }
    .summary-label { font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-secondary); }
    .summary-value { font-family: var(--font-mono); font-size: 28px; font-weight: 600; line-height: 1.1; }
    .summary-value.up { color: var(--chart-up-series); }
    .summary-value.down { color: var(--chart-down-series); }
    .summary-value .unit { font-size: 16px; color: var(--text-secondary); font-weight: 400; margin-left: 6px; }
    .proportion-bar { display: flex; height: 10px; border-radius: 5px; overflow: hidden; margin-top: 16px; }
    .proportion-wan { background: var(--chart-wan); }
    .proportion-local { background: var(--chart-local); }
    .proportion-legend { display: flex; gap: 32px; margin-top: 12px; font-size: 13px; color: var(--text-secondary); }
    .proportion-legend .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; vertical-align: middle; margin-right: 8px; }
    .proportion-legend .dot.wan { background: var(--chart-wan); }
    .proportion-legend .dot.local { background: var(--chart-local); }
    .proportion-legend .value { font-family: var(--font-mono); font-weight: 600; color: var(--text-primary); margin-left: 8px; }
    table.topapps { width: 100%; border-collapse: collapse; background: var(--surface-card); border: 1px solid var(--border-card); border-radius: 10px; overflow: hidden; }
    table.topapps th, table.topapps td { padding: 10px 14px; text-align: left; }
    table.topapps thead { background: rgba(20,24,46,0.03); }
    table.topapps th { font-size: 12px; font-weight: 600; color: var(--text-secondary); text-transform: uppercase; letter-spacing: 0.04em; border-bottom: 1px solid var(--border-subtle); }
    table.topapps td { font-size: 14px; border-bottom: 1px solid var(--border-subtle); }
    table.topapps tbody tr:last-child td { border-bottom: none; }
    table.topapps td.mono { font-family: var(--font-mono); text-align: right; }
    .sig-pill { display: inline-flex; align-items: center; padding: 3px 10px; border-radius: 6px; font-size: 12px; font-weight: 600; border: 1px solid; }
    .sig-Signed   { background: var(--status-success-bg); color: var(--status-success); border-color: var(--status-success); }
    .sig-Unchecked { background: var(--status-neutral-bg); color: var(--status-neutral); border-color: var(--status-neutral); }
    .sig-Unsigned, .sig-Invalid { background: var(--status-critical-bg); color: var(--status-critical); border-color: var(--status-critical); }
    .talker-categories { display: grid; grid-template-columns: 1fr; gap: 16px; }
    .talker-category-header { display: flex; align-items: center; gap: 10px; margin: 8px 0 6px; }
    .talker-category-header .glyph { display: inline-block; width: 18px; height: 18px; border-radius: 4px; }
    .talker-category-header.NewToday .glyph      { background: var(--status-neutral-bg);  border: 1px solid var(--status-neutral); }
    .talker-category-header.UnusualVolume .glyph { background: var(--status-caution-bg);  border: 1px solid var(--status-caution); }
    .talker-category-header.RiskyPaths .glyph    { background: var(--status-critical-bg); border: 1px solid var(--status-critical); }
    .talker-category-header h3 { margin: 0; font-size: 15px; font-weight: 600; }
    .talker-category-header .count { color: var(--text-secondary); font-size: 13px; }
    .talker-card { background: var(--surface-card); border: 1px solid var(--border-card); border-radius: 10px; padding: 14px 16px; margin-bottom: 8px; }
    .talker-card-title { font-weight: 600; font-size: 14px; margin: 0 0 4px; }
    .talker-card-sub { color: var(--text-secondary); font-size: 12px; margin: 0; }
    .talker-card-reason { color: var(--text-secondary); font-size: 12px; margin: 6px 0 0; }
    .notable-section { margin-bottom: 18px; }
    .notable-section-header { display: flex; align-items: center; gap: 10px; margin: 0 0 8px; }
    .notable-section-header .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; }
    .notable-section.Critical .dot { background: var(--status-critical); }
    .notable-section.Warning .dot  { background: var(--status-caution); }
    .notable-section.Info .dot     { background: var(--status-neutral); }
    .notable-section-header h3 { margin: 0; font-size: 14px; font-weight: 600; }
    .notable-section-header .count { color: var(--text-secondary); font-size: 13px; }
    .notable-card { display: flex; gap: 12px; background: var(--surface-card); border: 1px solid var(--border-card); border-radius: 10px; padding: 14px 16px; margin-bottom: 8px; position: relative; overflow: hidden; }
    .notable-card::before { content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 3px; }
    .notable-card.Critical::before { background: var(--status-critical); }
    .notable-card.Warning::before  { background: var(--status-caution); }
    .notable-card.Info::before     { background: var(--status-neutral); }
    .notable-card-body { flex: 1; padding-left: 8px; }
    .notable-card-title { font-weight: 600; font-size: 14px; margin: 0 0 4px; }
    .notable-card-detail { color: var(--text-secondary); font-size: 13px; margin: 0; }
    .notable-card-entity { font-family: var(--font-mono); font-size: 12px; color: var(--text-tertiary); margin: 6px 0 0; }
    .empty-note { color: var(--text-secondary); font-size: 13px; padding: 12px 16px; background: var(--surface-card); border: 1px solid var(--border-card); border-radius: 10px; text-align: center; }
    @media print {
      body { background: #fff; }
      .doc-callout { color: #565d72; }
      .summary, table.topapps, .talker-card, .notable-card, .empty-note { break-inside: avoid; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
    }

    """;

    // ─── Body sections ─────────────────────────────────────────────────────

    private static void WriteHeader(TextWriter writer, DailyReportResult r)
    {
        var dateLong   = r.Date.ToString("ddd, MMM d yyyy", CultureInfo.InvariantCulture);
        var anchorText = FormatAnchor(r.Anchor, r.AnchorSpecificDate);
        writer.WriteLine("    <header class=\"doc-header\">");
        writer.WriteLine("      <div>");
        writer.WriteLine("        <h1 class=\"doc-title\">ZenVizor Daily Report</h1>");
        writer.WriteLine($"        <p class=\"doc-subtitle\">{Html(dateLong)} · {Html(anchorText)}</p>");
        writer.WriteLine("      </div>");
        writer.WriteLine("      <div class=\"doc-callout\">");
        writer.WriteLine("        <strong>Generated locally</strong><br>No network used");
        writer.WriteLine("      </div>");
        writer.WriteLine("    </header>");
    }

    private static void WriteSummary(TextWriter writer, DailyReportHero h)
    {
        var total = h.TotalUpBytes + h.TotalDownBytes;
        var (totalV, totalU) = FormatBytesPair(total);
        var (upV, upU) = FormatBytesPair(h.TotalUpBytes);
        var (downV, downU) = FormatBytesPair(h.TotalDownBytes);

        writer.WriteLine("    <section class=\"section\">");
        writer.WriteLine("      <h2 class=\"section-header\">Summary</h2>");
        writer.WriteLine("      <div class=\"summary\">");
        writer.WriteLine($"        <div class=\"summary-item\"><span class=\"summary-label\">Total traffic</span><span class=\"summary-value\">{Html(totalV)}<span class=\"unit\">{Html(totalU)}</span></span></div>");
        writer.WriteLine($"        <div class=\"summary-item\"><span class=\"summary-label\">Uploaded</span><span class=\"summary-value up\">{Html(upV)}<span class=\"unit\">{Html(upU)}</span></span></div>");
        writer.WriteLine($"        <div class=\"summary-item\"><span class=\"summary-label\">Downloaded</span><span class=\"summary-value down\">{Html(downV)}<span class=\"unit\">{Html(downU)}</span></span></div>");
        writer.WriteLine("      </div>");
        writer.WriteLine("    </section>");
    }

    private static void WriteProportion(TextWriter writer, DailyReportHero h)
    {
        var wanPct   = (int)Math.Round(Math.Clamp(h.WanRatio,   0, 1) * 100);
        var localPct = Math.Max(0, 100 - wanPct);
        // Use percent widths directly; both values total to 100.
        writer.WriteLine("    <section class=\"section\">");
        writer.WriteLine("      <h2 class=\"section-header\">WAN vs Local</h2>");
        writer.WriteLine("      <div class=\"proportion-bar\">");
        writer.WriteLine($"        <div class=\"proportion-wan\" style=\"width: {wanPct.ToString(CultureInfo.InvariantCulture)}%\"></div>");
        writer.WriteLine($"        <div class=\"proportion-local\" style=\"width: {localPct.ToString(CultureInfo.InvariantCulture)}%\"></div>");
        writer.WriteLine("      </div>");
        writer.WriteLine("      <div class=\"proportion-legend\">");
        writer.WriteLine($"        <span><span class=\"dot wan\"></span>WAN<span class=\"value\">{wanPct.ToString(CultureInfo.InvariantCulture)}%</span></span>");
        writer.WriteLine($"        <span><span class=\"dot local\"></span>Local<span class=\"value\">{localPct.ToString(CultureInfo.InvariantCulture)}%</span></span>");
        writer.WriteLine("      </div>");
        writer.WriteLine("    </section>");
    }

    private static void WriteTopApps(TextWriter writer, IReadOnlyList<DailyReportAppRow> rows)
    {
        writer.WriteLine("    <section class=\"section\">");
        writer.WriteLine("      <h2 class=\"section-header\">Top apps</h2>");
        if (rows.Count == 0)
        {
            writer.WriteLine("      <div class=\"empty-note\">No apps recorded for this date.</div>");
            writer.WriteLine("    </section>");
            return;
        }
        writer.WriteLine("      <table class=\"topapps\">");
        writer.WriteLine("        <thead><tr><th>App</th><th>Publisher</th><th>Signature</th><th style=\"text-align:right\">Up</th><th style=\"text-align:right\">Down</th></tr></thead>");
        writer.WriteLine("        <tbody>");
        foreach (var r in rows)
        {
            // Sanitize the signature value before it lands inside both the
            // class attribute and the cell text — defends against future
            // schema additions that might carry whitespace or unexpected
            // characters from the apps table.
            var sigCls = SafeCssIdent(r.SignatureStatus);
            writer.WriteLine("          <tr>");
            writer.WriteLine($"            <td>{Html(r.ImageName)}</td>");
            writer.WriteLine($"            <td>{Html(r.Publisher ?? "(unknown)")}</td>");
            writer.WriteLine($"            <td><span class=\"sig-pill sig-{Html(sigCls)}\">{Html(r.SignatureStatus)}</span></td>");
            writer.WriteLine($"            <td class=\"mono\">{Html(FormatBytes(r.BytesUp))}</td>");
            writer.WriteLine($"            <td class=\"mono\">{Html(FormatBytes(r.BytesDown))}</td>");
            writer.WriteLine("          </tr>");
        }
        writer.WriteLine("        </tbody>");
        writer.WriteLine("      </table>");
        writer.WriteLine("    </section>");
    }

    private static void WriteUncommonTalkers(TextWriter writer, IReadOnlyList<DailyReportTalker> rows)
    {
        writer.WriteLine("    <section class=\"section\">");
        writer.WriteLine("      <h2 class=\"section-header\">Uncommon talkers</h2>");
        if (rows.Count == 0)
        {
            writer.WriteLine("      <div class=\"empty-note\">A deliberately quiet day. No app strayed from its usual pattern.</div>");
            writer.WriteLine("    </section>");
            return;
        }
        writer.WriteLine("      <div class=\"talker-categories\">");
        WriteTalkerCategory(writer, "NewToday",      "New today",      rows);
        WriteTalkerCategory(writer, "UnusualVolume", "Unusual volume", rows);
        WriteTalkerCategory(writer, "RiskyPaths",    "Risky paths",    rows);
        writer.WriteLine("      </div>");
        writer.WriteLine("    </section>");
    }

    private static void WriteTalkerCategory(TextWriter writer, string categoryKey, string categoryLabel,
        IReadOnlyList<DailyReportTalker> allRows)
    {
        var filter = categoryKey switch
        {
            "NewToday"      => UncommonCategory.NewToday,
            "UnusualVolume" => UncommonCategory.UnusualVolume,
            "RiskyPaths"    => UncommonCategory.RiskyPaths,
            _ => (UncommonCategory)(-1),
        };
        var rows = allRows.Where(t => t.Category == filter).ToList();
        if (rows.Count == 0) return;
        writer.WriteLine("        <div>");
        writer.WriteLine($"          <div class=\"talker-category-header {categoryKey}\"><span class=\"glyph\"></span><h3>{Html(categoryLabel)}</h3><span class=\"count\">{rows.Count.ToString(CultureInfo.InvariantCulture)}</span></div>");
        foreach (var t in rows)
        {
            var publisher = string.IsNullOrEmpty(t.Publisher) ? "(unknown)" : t.Publisher;
            writer.WriteLine("          <div class=\"talker-card\">");
            writer.WriteLine($"            <p class=\"talker-card-title\">{Html(t.ImageName)}</p>");
            writer.WriteLine($"            <p class=\"talker-card-sub\">{Html(publisher)} · {Html(t.SignatureStatus)}</p>");
            writer.WriteLine($"            <p class=\"talker-card-reason\">{Html(t.Reason)}</p>");
            writer.WriteLine("          </div>");
        }
        writer.WriteLine("        </div>");
    }

    private static void WriteNotable(TextWriter writer, IReadOnlyList<DailyReportNotable> rows)
    {
        writer.WriteLine("    <section class=\"section\">");
        writer.WriteLine("      <h2 class=\"section-header\">Notable today</h2>");
        if (rows.Count == 0)
        {
            writer.WriteLine("      <div class=\"empty-note\">Nothing notable today.</div>");
            writer.WriteLine("    </section>");
            return;
        }
        // Group by severity, render Critical → Warning → Info per mockup Q7a lock.
        WriteNotableSection(writer, "Critical", NotableSeverity.Critical, rows);
        WriteNotableSection(writer, "Warning",  NotableSeverity.Warning,  rows);
        WriteNotableSection(writer, "Info",     NotableSeverity.Info,     rows);
        writer.WriteLine("    </section>");
    }

    private static void WriteNotableSection(TextWriter writer, string severityKey, NotableSeverity severity,
        IReadOnlyList<DailyReportNotable> allRows)
    {
        var rows = allRows.Where(n => n.Severity == severity).ToList();
        if (rows.Count == 0) return;
        writer.WriteLine($"      <div class=\"notable-section {severityKey}\">");
        writer.WriteLine($"        <div class=\"notable-section-header\"><span class=\"dot\"></span><h3>{Html(severityKey)}</h3><span class=\"count\">{rows.Count.ToString(CultureInfo.InvariantCulture)}</span></div>");
        foreach (var n in rows)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(n.EventTimeUnixMs)
                .ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            writer.WriteLine($"        <div class=\"notable-card {severityKey}\">");
            writer.WriteLine("          <div class=\"notable-card-body\">");
            writer.WriteLine($"            <p class=\"notable-card-title\">{Html(n.Title)}</p>");
            writer.WriteLine($"            <p class=\"notable-card-detail\">{Html(n.Detail)}</p>");
            writer.WriteLine($"            <p class=\"notable-card-entity\">App · {Html(n.ImageName)} · pid {n.Pid.ToString(CultureInfo.InvariantCulture)} · {Html(time)}</p>");
            writer.WriteLine("          </div>");
            writer.WriteLine("        </div>");
        }
        writer.WriteLine("      </div>");
    }

    // ─── Formatting helpers ────────────────────────────────────────────────

    // HTML escape. Defends against any future DTO field carrying HTML-special
    // characters (publisher names like "Microsoft & Co.", reason text from
    // future heuristics with angle-bracketed hostnames, etc.).
    internal static string Html(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&':  sb.Append("&amp;");  break;
                case '<':  sb.Append("&lt;");   break;
                case '>':  sb.Append("&gt;");   break;
                case '"':  sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;");  break;
                default:   sb.Append(ch);       break;
            }
        }
        return sb.ToString();
    }

    // CSS class identifiers must be safe — letters / digits / hyphens.
    // A defensive transform of SignatureStatus avoids breaking the
    // <span class="sig-Foo"> mapping if a future status value carries
    // whitespace or punctuation. Existing values (Signed / Unchecked /
    // Unsigned / Invalid) pass through unchanged.
    internal static string SafeCssIdent(string value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-');
        }
        return sb.ToString();
    }

    internal static (string Value, string Unit) FormatBytesPair(long bytes)
    {
        if (bytes <= 0) return ("0", "B");
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024.0 && u < units.Length - 1) { v /= 1024.0; u++; }
        return (v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture), units[u]);
    }

    internal static string FormatBytes(long bytes)
    {
        var (v, u) = FormatBytesPair(bytes);
        return v + " " + u;
    }

    internal static string FormatAnchor(AnchorMode anchor, DateOnly? specificDate) => anchor switch
    {
        AnchorMode.SpecificDate => $"compared to {specificDate?.ToString("ddd, MMM d yyyy") ?? "(none)"}",
        AnchorMode.Avg7d        => "compared to 7-day average",
        AnchorMode.Avg30d       => "compared to 30-day average",
        AnchorMode.Avg90d       => "compared to 90-day average",
        _                       => anchor.ToString(),
    };
}
