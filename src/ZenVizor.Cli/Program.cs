using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

[assembly: SupportedOSPlatform("windows")]

// Exit codes (also surfaced in the source comments where they're set):
//   0 — success
//   1 — generic CLI error (parse failure, unexpected exception)
//   2 — IPC version mismatch (negotiation rejected, or envelope SchemaVersion floor failed)
//   3 — service unreachable (named-pipe connect timed out)
//
// IMPORTANT: handlers MUST set context.ExitCode via the InvocationContext
// overload of SetHandler. Setting Environment.ExitCode here does NOT
// propagate to RootCommand.InvokeAsync's return value — every "handled
// failure" exits 0 instead. The previous wiring had this bug.

var root = new RootCommand("zvctl — ZenVizor CLI client.");

var pingCommand = new Command("ping", "Round-trip a ping over the named-pipe IPC.");
pingCommand.SetHandler(async ctx => ctx.ExitCode = await RunPingAsync());
root.AddCommand(pingCommand);

var statusCommand = new Command("status", "Print the service status from the IPC handshake.");
statusCommand.SetHandler(async ctx => ctx.ExitCode = await RunStatusAsync());
root.AddCommand(statusCommand);

var statsCommand = new Command("stats", "Print capture-pipeline observation counters.");
statsCommand.SetHandler(async ctx => ctx.ExitCode = await RunStatsAsync());
root.AddCommand(statsCommand);

var snapshotCommand = new Command("snapshot", "Print the current per-app activity snapshot.");
var allOption = new Option<bool>(
    aliases: new[] { "--all", "-a" },
    description: "Show every app with non-zero bytes, not just the top 10.");
var jsonOption = new Option<bool>(
    aliases: new[] { "--json", "-j" },
    description: "Emit the raw IpcEnvelope<ActivitySnapshot> as JSON.");
snapshotCommand.AddOption(allOption);
snapshotCommand.AddOption(jsonOption);
snapshotCommand.SetHandler(async ctx =>
{
    var all = ctx.ParseResult.GetValueForOption(allOption);
    var json = ctx.ParseResult.GetValueForOption(jsonOption);
    ctx.ExitCode = await RunSnapshotAsync(all, json);
});
root.AddCommand(snapshotCommand);

// ---- Phase 4 history/query subcommands ----

var windowOption = new Option<string>(
    aliases: new[] { "--window", "-w" },
    description: "Window: 1h, 24h, 7d, 30d, 90d, 1y, or 'from=UNIXMS,to=UNIXMS'. Default: 24h.",
    getDefaultValue: () => "24h");
var topOption = new Option<int>(
    aliases: new[] { "--top", "-n" },
    description: "Show only the top N rows.",
    getDefaultValue: () => 10);
// Reject negative --top at parse time so a typo doesn't silently become
// "show no rows". Zero stays valid: means "show none, just totals".
topOption.AddValidator(result =>
{
    if (result.GetValueOrDefault<int>() < 0)
    {
        result.ErrorMessage = "--top must be zero or positive.";
    }
});
var grainOption = new Option<string>(
    aliases: new[] { "--grain", "-g" },
    description: "Grain: auto | samples | hourly | daily.",
    getDefaultValue: () => "auto");
var jsonOption2 = new Option<bool>(
    aliases: new[] { "--json", "-j" },
    description: "Emit raw IpcEnvelope JSON.");

var appsCommand = new Command("apps", "List apps with traffic in the window.");
appsCommand.AddOption(windowOption);
appsCommand.AddOption(topOption);
appsCommand.AddOption(jsonOption2);
appsCommand.SetHandler(async ctx =>
{
    var w = ctx.ParseResult.GetValueForOption(windowOption)!;
    var n = ctx.ParseResult.GetValueForOption(topOption);
    var j = ctx.ParseResult.GetValueForOption(jsonOption2);
    ctx.ExitCode = await RunAppsAsync(w, n, j);
});
root.AddCommand(appsCommand);

var appIdArg = new Argument<int>("appId", "App id (see `zvctl apps`).");

var appCommand = new Command("app", "Show detail for one app (summary, sessions, time series).");
appCommand.AddArgument(appIdArg);
appCommand.AddOption(windowOption);
appCommand.AddOption(grainOption);
appCommand.AddOption(jsonOption2);
appCommand.SetHandler(async ctx =>
{
    var id = ctx.ParseResult.GetValueForArgument(appIdArg);
    var w = ctx.ParseResult.GetValueForOption(windowOption)!;
    var g = ctx.ParseResult.GetValueForOption(grainOption)!;
    var j = ctx.ParseResult.GetValueForOption(jsonOption2);
    ctx.ExitCode = await RunAppDetailAsync(id, w, g, j);
});
root.AddCommand(appCommand);

var connectionsCommand = new Command("connections", "List endpoints an app talked to in the window.");
connectionsCommand.AddArgument(appIdArg);
connectionsCommand.AddOption(windowOption);
connectionsCommand.AddOption(topOption);
connectionsCommand.AddOption(jsonOption2);
connectionsCommand.SetHandler(async ctx =>
{
    var id = ctx.ParseResult.GetValueForArgument(appIdArg);
    var w = ctx.ParseResult.GetValueForOption(windowOption)!;
    var n = ctx.ParseResult.GetValueForOption(topOption);
    var j = ctx.ParseResult.GetValueForOption(jsonOption2);
    ctx.ExitCode = await RunConnectionsAsync(id, w, n, j);
});
root.AddCommand(connectionsCommand);

var historyCommand = new Command("history", "Aggregate traffic time series across all apps.");
historyCommand.AddOption(windowOption);
historyCommand.AddOption(grainOption);
historyCommand.AddOption(jsonOption2);
historyCommand.SetHandler(async ctx =>
{
    var w = ctx.ParseResult.GetValueForOption(windowOption)!;
    var g = ctx.ParseResult.GetValueForOption(grainOption)!;
    var j = ctx.ParseResult.GetValueForOption(jsonOption2);
    ctx.ExitCode = await RunHistoryAsync(w, g, j);
});
root.AddCommand(historyCommand);

// ---- Phase 5 report subcommand ----

var reportDateOption = new Option<string>(
    aliases: new[] { "--date", "-d" },
    description: "Report date in yyyy-MM-dd (user-local). Required.")
{
    IsRequired = true,
};
var reportAnchorOption = new Option<string>(
    aliases: new[] { "--anchor", "-A" },
    description: "Comparison baseline: avg7d | avg30d | avg90d. Default: avg7d.",
    getDefaultValue: () => "avg7d");
var reportJsonOption = new Option<bool>(
    aliases: new[] { "--json", "-j" },
    description: "Emit raw IpcEnvelope<DailyReportResult> as JSON.");

var reportCommand = new Command("report", "Fetch the Phase-5 daily report for one date.");
reportCommand.AddOption(reportDateOption);
reportCommand.AddOption(reportAnchorOption);
reportCommand.AddOption(reportJsonOption);
reportCommand.SetHandler(async ctx =>
{
    var dateText = ctx.ParseResult.GetValueForOption(reportDateOption)!;
    var anchorText = ctx.ParseResult.GetValueForOption(reportAnchorOption)!;
    var j = ctx.ParseResult.GetValueForOption(reportJsonOption);
    ctx.ExitCode = await RunReportAsync(dateText, anchorText, j);
});
root.AddCommand(reportCommand);

// ---- Phase 6.6 alerts subcommands ----
//
// Nested group: `zvctl alerts <list|dismiss|catalog>`. The Alerts feed
// is paged by neither time window nor rank (per the discovery-over-
// ranking principle), so `list` prints every returned row and surfaces
// the server's HasMore signal verbatim rather than capping with --top.

var alertsCommand = new Command("alerts", "Read and dismiss alerts; print the catalog of types.");

var alertsStateOption = new Option<string>(
    aliases: new[] { "--state", "-s" },
    description: "Server filter: active | dismissed | all. Default: active.",
    getDefaultValue: () => "active");
var alertsSeverityOption = new Option<string?>(
    aliases: new[] { "--severity" },
    description: "Client-side filter on severity: critical | warning | info.",
    getDefaultValue: () => null);
var alertsTypeOption = new Option<string?>(
    aliases: new[] { "--type", "-t" },
    description: "Client-side filter on AlertType enum name (case-insensitive; see `zvctl alerts catalog`).",
    getDefaultValue: () => null);
var alertsMaxRowsOption = new Option<int>(
    aliases: new[] { "--max-rows" },
    description: "Server transport cap. Default 500 (matches AlertsFilter default). Increase if HasMore flagged.",
    getDefaultValue: () => 500);
var alertsListJsonOption = new Option<bool>(
    aliases: new[] { "--json", "-j" },
    description: "Emit raw IpcEnvelope<AlertsResult> JSON.");
alertsMaxRowsOption.AddValidator(result =>
{
    if (result.GetValueOrDefault<int>() <= 0)
    {
        result.ErrorMessage = "--max-rows must be a positive integer.";
    }
});

var alertsListCommand = new Command("list", "List alerts matching the filter (newest first).");
alertsListCommand.AddOption(alertsStateOption);
alertsListCommand.AddOption(alertsSeverityOption);
alertsListCommand.AddOption(alertsTypeOption);
alertsListCommand.AddOption(alertsMaxRowsOption);
alertsListCommand.AddOption(alertsListJsonOption);
alertsListCommand.SetHandler(async ctx =>
{
    var state = ctx.ParseResult.GetValueForOption(alertsStateOption)!;
    var sev = ctx.ParseResult.GetValueForOption(alertsSeverityOption);
    var typ = ctx.ParseResult.GetValueForOption(alertsTypeOption);
    var max = ctx.ParseResult.GetValueForOption(alertsMaxRowsOption);
    var j = ctx.ParseResult.GetValueForOption(alertsListJsonOption);
    ctx.ExitCode = await RunAlertsListAsync(state, sev, typ, max, j);
});
alertsCommand.AddCommand(alertsListCommand);

var alertIdArg = new Argument<long>(
    "alertId",
    "Numeric alert id (from `zvctl alerts list`).");

var alertsDismissCommand = new Command("dismiss", "Mark an alert as dismissed. Idempotent — already-dismissed and unknown ids succeed silently on the wire; CLI echoes confirmation either way.");
alertsDismissCommand.AddArgument(alertIdArg);
alertsDismissCommand.SetHandler(async ctx =>
{
    var id = ctx.ParseResult.GetValueForArgument(alertIdArg);
    ctx.ExitCode = await RunAlertsDismissAsync(id);
});
alertsCommand.AddCommand(alertsDismissCommand);

var alertsCatalogJsonOption = new Option<bool>(
    aliases: new[] { "--json", "-j" },
    description: "Emit the catalog as JSON.");

var alertsCatalogCommand = new Command("catalog", "Print the AlertType catalog: locked severity, source monitor, producer-wired status, one-line description.");
alertsCatalogCommand.AddOption(alertsCatalogJsonOption);
alertsCatalogCommand.SetHandler(ctx =>
{
    var j = ctx.ParseResult.GetValueForOption(alertsCatalogJsonOption);
    ctx.ExitCode = RunAlertsCatalog(j);
});
alertsCommand.AddCommand(alertsCatalogCommand);

root.AddCommand(alertsCommand);

return await root.InvokeAsync(args);

static async Task<int> RunPingAsync()
{
    try
    {
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pong = await client.Proxy.PingAsync();
        sw.Stop();

        Console.WriteLine($"pong  ({sw.ElapsedMilliseconds} ms)  server-ts {pong.ServerTimestampUnixMs}");
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static async Task<int> RunStatusAsync()
{
    try
    {
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var status = await client.Proxy.GetServiceStatusAsync();

        Console.WriteLine($"Service         : {status.ServiceName}");
        Console.WriteLine($"Version         : {status.Version}");
        Console.WriteLine($"Protocol        : {status.ProtocolVersion}");
        Console.WriteLine($"Started (unix-ms): {status.StartedAtUnixMs}");
        Console.WriteLine($"Uptime (ms)     : {status.UptimeMs}");
        Console.WriteLine($"DB path         : {status.DbPath}");
        Console.WriteLine($"Capture active  : {status.CaptureActive}");
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static async Task<int> RunStatsAsync()
{
    try
    {
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetCaptureStatsAsync();
        var s = envelope.UnwrapWithSchemaCheck(nameof(CaptureStats), IpcSchemaVersion.CaptureStats);
        var attributed = s.ObservationsSeen - s.ObservationsUnattributed;
        var rate = s.ObservationsSeen > 0
            ? (double)attributed / s.ObservationsSeen
            : 1.0;

        Console.WriteLine($"CapturedAt (unix-ms)        : {s.CapturedAtUnixMs}");
        Console.WriteLine($"Observations seen           : {s.ObservationsSeen}");
        Console.WriteLine($"Observations attributed     : {attributed}");
        Console.WriteLine($"Observations unattributed   : {s.ObservationsUnattributed}");
        Console.WriteLine($"Attribution rate            : {rate * 100.0:0.000} %");
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static async Task<int> RunSnapshotAsync(bool all, bool json)
{
    try
    {
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetCurrentActivitySnapshotAsync();

        if (json)
        {
            var serialized = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            Console.WriteLine(serialized);
            return 0;
        }

        var snap = envelope.UnwrapWithSchemaCheck(nameof(ActivitySnapshot), IpcSchemaVersion.ActivitySnapshot);
        PrintSnapshot(snap, all);
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static void PrintSnapshot(ActivitySnapshot snap, bool all)
{
    if (snap.WindowSeconds <= 0 || snap.Apps.Count == 0)
    {
        Console.WriteLine("(warming up — no completed flush bucket yet, try again in ~5 s)");
        return;
    }

    // Sort by total bytes (Up+Down) descending — top talkers first.
    var ordered = snap.Apps
        .OrderByDescending(a => a.BytesUpTotal + a.BytesDownTotal)
        .ThenBy(a => a.ImageName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var rows = all ? ordered : ordered.Take(10).ToList();

    var winLabel = $"{snap.WindowSeconds:0.0}s";
    const int appCol = 38, pubCol = 24, sigCol = 9, rateCol = 12;

    Console.WriteLine(
        $"{Pad("App", appCol)} {Pad("Publisher", pubCol)} {Pad("Sig", sigCol)} " +
        $"{RPad("Up/s", rateCol)} {RPad("Dn/s", rateCol)}  Window");
    Console.WriteLine(new string('-', appCol + pubCol + sigCol + rateCol * 2 + 12));

    foreach (var app in rows)
    {
        var appLabel = string.IsNullOrEmpty(app.HostedServices)
            ? app.ImageName
            : $"{app.ImageName} [{app.HostedServices}]";
        var pub = string.IsNullOrEmpty(app.Publisher) ? "(unknown)" : app.Publisher;

        Console.WriteLine(
            $"{Pad(appLabel, appCol)} {Pad(pub, pubCol)} {Pad(app.SignatureStatus, sigCol)} " +
            $"{RPad(FormatRate(app.BytesUpPerSec), rateCol)} " +
            $"{RPad(FormatRate(app.BytesDownPerSec), rateCol)}  {winLabel}");
    }

    if (!all && ordered.Count > rows.Count)
    {
        Console.WriteLine();
        Console.WriteLine($"… {ordered.Count - rows.Count} more apps; pass --all to show.");
    }

    // WAN vs LOCAL breakdown — same window. Bytes (not rates) so the print
    // doesn't need to divide by WindowSeconds; total is sanity-check info.
    var b = snap.WanLocalBreakdown;
    var totalWan = b.WanBytesUp + b.WanBytesDown;
    var totalLocal = b.LocalBytesUp + b.LocalBytesDown;
    var grand = totalWan + totalLocal;
    Console.WriteLine();
    if (grand == 0)
    {
        Console.WriteLine("WAN/LOCAL: (no classified bytes in window)");
    }
    else
    {
        var wanPct = 100.0 * totalWan / grand;
        var localPct = 100.0 * totalLocal / grand;
        Console.WriteLine(
            $"WAN/LOCAL: {wanPct.ToString("0.0", CultureInfo.InvariantCulture)}% WAN " +
            $"({FormatBytes(totalWan)}) · " +
            $"{localPct.ToString("0.0", CultureInfo.InvariantCulture)}% Local " +
            $"({FormatBytes(totalLocal)})");
    }
}

static string FormatRate(double bytesPerSec)
{
    if (bytesPerSec <= 0) return "0 B/s";
    string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
    var value = bytesPerSec;
    var unit = 0;
    while (value >= 1024.0 && unit < units.Length - 1)
    {
        value /= 1024.0;
        unit++;
    }
    return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
}

static string Pad(string s, int width)
{
    if (s.Length >= width) return s.Substring(0, width);
    return s.PadRight(width);
}

static string RPad(string s, int width)
{
    if (s.Length >= width) return s.Substring(0, width);
    return s.PadLeft(width);
}

// ---- Phase 4 query handlers ----

static QueryWindow ParseWindow(string spec)
{
    if (string.IsNullOrWhiteSpace(spec)) spec = "24h";
    spec = spec.Trim();

    // Custom form: "from=UNIXMS,to=UNIXMS". Validation is verbose because the
    // previous version surfaced FormatException with no hint — users hit that
    // and assume the CLI is broken.
    if (spec.StartsWith("from=", StringComparison.OrdinalIgnoreCase))
    {
        var parts = spec.Split(',', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException(
                "Custom window must be 'from=UNIXMS,to=UNIXMS' (comma-separated). " +
                "Example: --window from=1730000000000,to=1730086400000");
        }
        if (!TryParseLong(parts[0]["from=".Length..], out var from))
        {
            throw new ArgumentException(
                $"--window: 'from=' value '{parts[0]["from=".Length..]}' is not a Unix-ms integer.");
        }
        if (!parts[1].StartsWith("to=", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"--window: second part must start with 'to=', got '{parts[1]}'.");
        }
        if (!TryParseLong(parts[1]["to=".Length..], out var to))
        {
            throw new ArgumentException(
                $"--window: 'to=' value '{parts[1]["to=".Length..]}' is not a Unix-ms integer.");
        }
        if (to < from)
        {
            throw new ArgumentException(
                $"--window: 'to' ({to}) must be greater than or equal to 'from' ({from}).");
        }
        return new QueryWindow(from, to);
    }

    if (spec.Length < 2)
    {
        throw new ArgumentException(
            $"--window '{spec}' is too short. Use one of: 1h, 24h, 7d, 30d, 90d, 1y, or from=...,to=...");
    }
    var unit = char.ToLowerInvariant(spec[^1]);
    if (!TryParseInt(spec[..^1], out var n) || n <= 0)
    {
        throw new ArgumentException(
            $"--window '{spec}': numeric prefix must be a positive integer (got '{spec[..^1]}').");
    }
    var ms = unit switch
    {
        'h' => n * 3_600_000L,
        'd' => n * 86_400_000L,
        'y' => n * 86_400_000L * 365L,
        _   => throw new ArgumentException(
            $"--window '{spec}': unit '{unit}' is not recognized. Use h, d, or y."),
    };
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    return new QueryWindow(nowMs - ms, nowMs);
}

static bool TryParseLong(string s, out long value) =>
    long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

static bool TryParseInt(string s, out int value) =>
    int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

static TrafficGrain ParseGrain(string spec) =>
    spec?.ToLowerInvariant() switch
    {
        "auto"    or null or "" => TrafficGrain.Auto,
        "samples" or "s"        => TrafficGrain.Samples,
        "hourly"  or "h"        => TrafficGrain.Hourly,
        "daily"   or "d"        => TrafficGrain.Daily,
        _ => throw new ArgumentException($"--grain '{spec}': use auto | samples | hourly | daily."),
    };

static AnchorMode ParseAnchor(string spec) =>
    spec?.ToLowerInvariant() switch
    {
        "avg7d"  or null or "" => AnchorMode.Avg7d,
        "avg30d"               => AnchorMode.Avg30d,
        "avg90d"               => AnchorMode.Avg90d,
        _ => throw new ArgumentException(
            $"--anchor '{spec}': use avg7d | avg30d | avg90d. " +
            "(SpecificDate anchor requires a date and is not exposed via zvctl yet.)"),
    };

static async Task<int> RunAppsAsync(string windowSpec, int top, bool json)
{
    try
    {
        var window = ParseWindow(windowSpec);
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetAppListAsync(window);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var payload = envelope.UnwrapWithSchemaCheck(nameof(AppListResult), IpcSchemaVersion.Query);
        var apps = payload.Apps;
        if (apps.Count == 0)
        {
            Console.WriteLine("(no apps with traffic in the window)");
            return 0;
        }

        var rows = apps.Count > top ? apps.Take(top).ToList() : (IReadOnlyList<AppListEntry>)apps;
        const int idCol = 6, appCol = 32, pubCol = 28, sigCol = 11, byteCol = 14, lastCol = 19;
        Console.WriteLine(
            $"{Pad("Id", idCol)} {Pad("App", appCol)} {Pad("Publisher", pubCol)} " +
            $"{Pad("Sig", sigCol)} {RPad("Up", byteCol)} {RPad("Down", byteCol)} {Pad("Last seen", lastCol)}");
        Console.WriteLine(new string('-', idCol + appCol + pubCol + sigCol + byteCol * 2 + lastCol + 7));
        foreach (var a in rows)
        {
            var pub = string.IsNullOrEmpty(a.Publisher) ? "(unknown)" : a.Publisher;
            Console.WriteLine(
                $"{RPad(a.AppId.ToString(CultureInfo.InvariantCulture), idCol)} " +
                $"{Pad(a.ImageName, appCol)} {Pad(pub, pubCol)} {Pad(a.SignatureStatus, sigCol)} " +
                $"{RPad(FormatBytes(a.BytesUp), byteCol)} {RPad(FormatBytes(a.BytesDown), byteCol)} " +
                $"{Pad(FormatTime(a.LastSeenUnixMs), lastCol)}");
        }
        if (apps.Count > rows.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"… {apps.Count - rows.Count} more apps; pass --top {apps.Count} to show all.");
        }
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static async Task<int> RunAppDetailAsync(int appId, string windowSpec, string grainSpec, bool json)
{
    try
    {
        var window = ParseWindow(windowSpec);
        var grain = ParseGrain(grainSpec);
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetAppDetailAsync(appId, window, grain);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var d = envelope.UnwrapWithSchemaCheck(nameof(AppDetailResult), IpcSchemaVersion.Query);
        Console.WriteLine($"App      : {d.Summary.ImageName}  (id {d.Summary.AppId})");
        Console.WriteLine($"Path     : {d.Summary.ImagePath}");
        Console.WriteLine($"Publisher: {(string.IsNullOrEmpty(d.Summary.Publisher) ? "(unknown)" : d.Summary.Publisher)}");
        Console.WriteLine($"Signature: {d.Summary.SignatureStatus}{(d.Summary.IsUserWritablePath ? "  [user-writable path]" : "")}");
        Console.WriteLine($"Window   : {FormatWindow(d.Window)}  (grain={d.GrainUsed})");
        Console.WriteLine($"Totals   : Up={FormatBytes(d.Summary.BytesUp)}  Down={FormatBytes(d.Summary.BytesDown)}");
        Console.WriteLine($"Seen     : First={FormatTime(d.Summary.FirstSeenUnixMs)}  Last={FormatTime(d.Summary.LastSeenUnixMs)}");
        Console.WriteLine();
        Console.WriteLine($"Recent sessions ({d.RecentSessions.Count}):");
        foreach (var s in d.RecentSessions.Take(20))
        {
            var end = s.EndTimeUnixMs is long e ? FormatTime(e) : "(running)";
            var hosted = string.IsNullOrEmpty(s.HostedServices) ? "" : $"  [{s.HostedServices}]";
            Console.WriteLine($"  #{s.SessionId} pid={s.Pid}  {FormatTime(s.StartTimeUnixMs)} → {end}{hosted}");
        }
        Console.WriteLine();
        Console.WriteLine($"Series ({d.Series.Count} points):");
        foreach (var p in d.Series.Take(50))
        {
            Console.WriteLine($"  {FormatTime(p.BucketStartUnixMs)}  {Pad(p.RemoteClass, 6)} up={RPad(FormatBytes(p.BytesUp), 12)} dn={RPad(FormatBytes(p.BytesDown), 12)}");
        }
        if (d.Series.Count > 50) Console.WriteLine($"  … {d.Series.Count - 50} more points");
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static async Task<int> RunConnectionsAsync(int appId, string windowSpec, int top, bool json)
{
    try
    {
        var window = ParseWindow(windowSpec);
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetConnectionsAsync(appId, window);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var payload = envelope.UnwrapWithSchemaCheck(nameof(ConnectionListResult), IpcSchemaVersion.Query);
        var rows = payload.Connections;
        if (rows.Count == 0)
        {
            Console.WriteLine("(no endpoints in window)");
            return 0;
        }
        var shown = rows.Count > top ? rows.Take(top).ToList() : (IReadOnlyList<ConnectionRow>)rows;
        const int protoCol = 6, addrCol = 42, portCol = 7, clsCol = 7, byteCol = 12, lastCol = 19;
        Console.WriteLine(
            $"{Pad("Proto", protoCol)} {Pad("Remote", addrCol)} {RPad("Port", portCol)} " +
            $"{Pad("Class", clsCol)} {RPad("Up", byteCol)} {RPad("Down", byteCol)} {Pad("Last seen", lastCol)}");
        Console.WriteLine(new string('-', protoCol + addrCol + portCol + clsCol + byteCol * 2 + lastCol + 7));
        foreach (var c in shown)
        {
            Console.WriteLine(
                $"{Pad(c.Protocol, protoCol)} {Pad(c.RemoteAddress, addrCol)} " +
                $"{RPad(c.RemotePort.ToString(CultureInfo.InvariantCulture), portCol)} " +
                $"{Pad(c.RemoteClass, clsCol)} {RPad(FormatBytes(c.BytesUp), byteCol)} " +
                $"{RPad(FormatBytes(c.BytesDown), byteCol)} {Pad(FormatTime(c.LastSeenUnixMs), lastCol)}");
        }
        if (rows.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"… {rows.Count - shown.Count} more endpoints; pass --top {rows.Count} to show all.");
        }
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static async Task<int> RunHistoryAsync(string windowSpec, string grainSpec, bool json)
{
    try
    {
        var window = ParseWindow(windowSpec);
        var grain = ParseGrain(grainSpec);
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetTrafficHistoryAsync(window, grain);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var h = envelope.UnwrapWithSchemaCheck(nameof(TrafficHistoryResult), IpcSchemaVersion.Query);
        Console.WriteLine($"Window   : {FormatWindow(h.Window)}  (grain={h.GrainUsed})");
        Console.WriteLine($"Buckets  : {h.Series.Count}");
        var totalUp = h.Series.Sum(p => p.BytesUp);
        var totalDn = h.Series.Sum(p => p.BytesDown);
        Console.WriteLine($"Totals   : Up={FormatBytes(totalUp)}  Down={FormatBytes(totalDn)}");
        Console.WriteLine();
        foreach (var p in h.Series.Take(100))
        {
            Console.WriteLine($"  {FormatTime(p.BucketStartUnixMs)}  {Pad(p.RemoteClass, 6)} up={RPad(FormatBytes(p.BytesUp), 12)} dn={RPad(FormatBytes(p.BytesDown), 12)}");
        }
        if (h.Series.Count > 100) Console.WriteLine($"  … {h.Series.Count - 100} more points; use --json for raw");
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

// ---- Phase 5 report handler ----

static async Task<int> RunReportAsync(string dateText, string anchorText, bool json)
{
    try
    {
        if (!DateOnly.TryParseExact(
                dateText, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ArgumentException(
                $"--date '{dateText}' is not a valid yyyy-MM-dd date.");
        }
        var anchor = ParseAnchor(anchorText);

        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetDailyReportAsync(date, anchor, anchorSpecificDate: null);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var r = envelope.UnwrapWithSchemaCheck(nameof(DailyReportResult), IpcSchemaVersion.DailyReport);
        Console.WriteLine($"Date         : {r.Date:yyyy-MM-dd}");
        Console.WriteLine($"Anchor       : {r.Anchor}");
        Console.WriteLine();
        Console.WriteLine("Hero:");
        Console.WriteLine($"  Up         : {FormatBytes(r.Hero.TotalUpBytes)}  ({FormatDelta(r.Hero.UpDeltaPct)} vs anchor)");
        Console.WriteLine($"  Down       : {FormatBytes(r.Hero.TotalDownBytes)}  ({FormatDelta(r.Hero.DownDeltaPct)} vs anchor)");
        Console.WriteLine($"  Total      : {FormatDelta(r.Hero.TotalDeltaPct)} vs anchor");
        Console.WriteLine($"  WAN ratio  : {r.Hero.WanRatio * 100:0.0} %");
        Console.WriteLine($"  Local ratio: {r.Hero.LocalRatio * 100:0.0} %");
        Console.WriteLine();
        Console.WriteLine($"Hourly buckets : {r.HourlyTraffic.Count}");
        Console.WriteLine($"Top apps       : {r.TopApps.Count}");
        Console.WriteLine($"Uncommon talkers: {r.UncommonTalkers.Count}");
        Console.WriteLine($"Notable items  : {r.Notable.Count}");
        if (r.TopApps.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Top apps:");
            foreach (var a in r.TopApps.Take(10))
            {
                var pub = string.IsNullOrEmpty(a.Publisher) ? "(unknown)" : a.Publisher;
                Console.WriteLine(
                    $"  #{a.AppId,-4} {Pad(a.ImageName, 28)} {Pad(pub, 24)} " +
                    $"up={RPad(FormatBytes(a.BytesUp), 10)} dn={RPad(FormatBytes(a.BytesDown), 10)}");
            }
        }
        if (r.Notable.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notable:");
            foreach (var n in r.Notable.Take(10))
            {
                Console.WriteLine($"  [{n.Severity}] {n.Title} (pid={n.Pid}, alert#{n.AlertId})");
                Console.WriteLine($"      {n.Detail}");
            }
        }
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static string FormatDelta(double pct) =>
    pct >= 0
        ? "+" + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%"
        : pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";

// ---- Phase 6.6 alerts handlers ----

static AlertState ParseAlertState(string spec) =>
    spec?.ToLowerInvariant() switch
    {
        "active"    or null or "" => AlertState.Active,
        "dismissed"               => AlertState.Dismissed,
        "all"                     => AlertState.All,
        _ => throw new ArgumentException(
            $"--state '{spec}': use active | dismissed | all."),
    };

static NotableSeverity? ParseSeverityFilter(string? spec)
{
    if (string.IsNullOrWhiteSpace(spec)) return null;
    return spec.ToLowerInvariant() switch
    {
        "critical" => NotableSeverity.Critical,
        "warning"  => NotableSeverity.Warning,
        "info"     => NotableSeverity.Info,
        _ => throw new ArgumentException(
            $"--severity '{spec}': use critical | warning | info."),
    };
}

static AlertType? ParseAlertTypeFilter(string? spec)
{
    if (string.IsNullOrWhiteSpace(spec)) return null;
    // Case-insensitive exact match against the AlertType enum names. The
    // brief deliberately ships no short aliases — users discover the names
    // via `zvctl alerts catalog`, which prints them in the same form.
    if (Enum.TryParse<AlertType>(spec, ignoreCase: true, out var t))
    {
        return t;
    }
    var allowed = string.Join(" | ", Enum.GetNames<AlertType>());
    throw new ArgumentException(
        $"--type '{spec}': not a known AlertType. Allowed: {allowed}.");
}

static async Task<int> RunAlertsListAsync(
    string stateText, string? severityText, string? typeText, int maxRows, bool json)
{
    try
    {
        var state = ParseAlertState(stateText);
        var severityFilter = ParseSeverityFilter(severityText);
        var typeFilter = ParseAlertTypeFilter(typeText);
        var filter = new AlertsFilter(state, MaxRows: maxRows);

        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetAlertsAsync(filter);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var payload = envelope.UnwrapWithSchemaCheck(nameof(AlertsResult), IpcSchemaVersion.Alerts);
        PrintAlertsList(payload, severityFilter, typeFilter);
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static void PrintAlertsList(
    AlertsResult payload, NotableSeverity? severityFilter, AlertType? typeFilter)
{
    IEnumerable<AlertDto> rows = payload.Alerts;
    if (severityFilter is { } sev) rows = rows.Where(a => a.Severity == sev);
    if (typeFilter is { } typ)     rows = rows.Where(a => a.Type == typ);
    var list = rows.ToList();

    if (list.Count == 0)
    {
        Console.WriteLine("(no alerts matched the filter)");
        return;
    }

    const int idCol = 6, sevCol = 10, typeCol = 24, createdCol = 19, entityCol = 18;
    Console.WriteLine(
        $"{Pad("Id", idCol)} {Pad("Severity", sevCol)} {Pad("Type", typeCol)} " +
        $"{Pad("Created", createdCol)} {Pad("Entity", entityCol)} Title");
    Console.WriteLine(new string('-', idCol + sevCol + typeCol + createdCol + entityCol + 12));

    foreach (var a in list)
    {
        var sevLabel = a.AcknowledgedAtUnixMs.HasValue
            ? $"{a.Severity} (dismissed)"
            : a.Severity.ToString();
        var entityLabel = $"{a.EntityKind}:{a.EntityRef}";
        Console.WriteLine(
            $"{RPad(a.AlertId.ToString(CultureInfo.InvariantCulture), idCol)} " +
            $"{Pad(sevLabel, sevCol)} {Pad(a.Type.ToString(), typeCol)} " +
            $"{Pad(FormatTime(a.CreatedAtUnixMs), createdCol)} {Pad(entityLabel, entityCol)} {a.Title}");
        if (!string.IsNullOrEmpty(a.Detail))
        {
            Console.WriteLine($"      {a.Detail}");
        }
    }

    var activeCount = list.Count(a => !a.AcknowledgedAtUnixMs.HasValue);
    var dismissedCount = list.Count - activeCount;
    Console.WriteLine();
    Console.WriteLine($"{list.Count} alerts ({activeCount} active, {dismissedCount} dismissed).");
    if (payload.HasMore)
    {
        Console.WriteLine(
            $"NOTE  server truncated at MaxRows={payload.Filter.MaxRows}. " +
            "Pass --max-rows N to widen.");
    }
}

static async Task<int> RunAlertsDismissAsync(long alertId)
{
    try
    {
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        await client.Proxy.DismissAlertAsync(alertId);
        // DismissAlertAsync is idempotent server-side — already-dismissed
        // or unknown ids succeed silently. CLI echoes confirmation either
        // way so a script gets one consistent signal on exit 0.
        Console.WriteLine($"Dismissed alert #{alertId}.");
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

/// <summary>
/// Locked metadata for each <see cref="AlertType"/> — severity (catalog §1.4),
/// source monitor, producer-wired flag, and the one-line description that
/// already lives in <see cref="AlertType"/>'s XML doc summary. Centralized
/// here so `zvctl alerts catalog` is the contract surface a scripting
/// consumer reads instead of grepping the enum file.
/// </summary>
static (NotableSeverity Severity, SourceMonitor Source, bool ProducerWired, string Description) GetCatalogEntry(AlertType type) => type switch
{
    AlertType.UnsignedFromUserPath => (NotableSeverity.Critical, SourceMonitor.Capture, true,
        "Unsigned binary from a user-writable folder making network connections."),
    AlertType.InvalidSignature => (NotableSeverity.Critical, SourceMonitor.Capture, true,
        "Signed binary whose signature does not verify."),
    AlertType.FirstRunWanTalker => (NotableSeverity.Info, SourceMonitor.Capture, true,
        "A newly-created app reached the network within seconds of first-seen."),
    AlertType.UnusualDailyVolume => (NotableSeverity.Warning, SourceMonitor.Rollup, false,
        "An app's daily bytes are robustly above its 14-day baseline (median + MAD)."),
    AlertType.LargeDownload => (NotableSeverity.Info, SourceMonitor.Capture, true,
        "A single connection pulled down a large download in a short window."),
    AlertType.OutboundHeavy => (NotableSeverity.Warning, SourceMonitor.Capture, true,
        "An app's outbound bytes dominate inbound by a configured ratio over the absolute floor."),
    _ => (NotableSeverity.Info, SourceMonitor.Capture, false, "(unknown type)"),
};

static int RunAlertsCatalog(bool json)
{
    try
    {
        var types = Enum.GetValues<AlertType>();

        if (json)
        {
            var rows = types.Select(t =>
            {
                var (sev, src, wired, desc) = GetCatalogEntry(t);
                return new
                {
                    Type = t.ToString(),
                    Severity = sev.ToString(),
                    Source = src.ToString(),
                    ProducerWired = wired,
                    Description = desc,
                };
            });
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        const int typeCol = 24, sevCol = 10, srcCol = 10, wiredCol = 10;
        Console.WriteLine(
            $"{Pad("Type", typeCol)} {Pad("Severity", sevCol)} {Pad("Source", srcCol)} " +
            $"{Pad("Producer", wiredCol)} Description");
        Console.WriteLine(new string('-', typeCol + sevCol + srcCol + wiredCol + 30));
        foreach (var t in types)
        {
            var (sev, src, wired, desc) = GetCatalogEntry(t);
            var wiredLabel = wired ? "wired" : "(none)";
            Console.WriteLine(
                $"{Pad(t.ToString(), typeCol)} {Pad(sev.ToString(), sevCol)} {Pad(src.ToString(), srcCol)} " +
                $"{Pad(wiredLabel, wiredCol)} {desc}");
        }
        Console.WriteLine();
        Console.WriteLine(
            $"{types.Length} alert types. " +
            $"{types.Count(t => GetCatalogEntry(t).ProducerWired)} producer-wired in this build; " +
            "the rest are vocabulary placeholders for post-MVP rules.");
        return 0;
    }
    catch (Exception ex) { return ReportError(ex); }
}

static int ReportError(Exception ex)
{
    if (ex is IpcVersionMismatchException vex)
    {
        Console.Error.WriteLine($"ERROR  version mismatch: client {vex.ClientVersion}, server {vex.ServerVersion}");
        return 2;
    }
    if (ex is IpcSchemaVersionException sex)
    {
        Console.Error.WriteLine(
            $"ERROR  schema version too old for {sex.PayloadName}: " +
            $"client expects >= v{sex.ExpectedMinSchemaVersion}, server returned v{sex.ActualSchemaVersion}.");
        return 2;
    }
    if (ex is TimeoutException)
    {
        Console.Error.WriteLine("ERROR  service is not running (pipe connect timed out).");
        return 3;
    }
    if (ex is ArgumentException aex)
    {
        // Invalid CLI input (e.g. bad --window): surface the message directly
        // so the user sees the explanation, not the type name.
        Console.Error.WriteLine($"ERROR  {aex.Message}");
        return 1;
    }
    Console.Error.WriteLine($"ERROR  {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static string FormatBytes(long bytes)
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
    return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
}

static string FormatTime(long unixMs) =>
    unixMs <= 0
        ? "(never)"
        : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

static string FormatWindow(QueryWindow w) =>
    $"{FormatTime(w.FromUnixMs)} → {FormatTime(w.ToUnixMs)}";
