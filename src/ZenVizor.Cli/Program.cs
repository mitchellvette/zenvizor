using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

[assembly: SupportedOSPlatform("windows")]

var root = new RootCommand("zvctl — ZenVizor CLI client.");

var pingCommand = new Command("ping", "Round-trip a ping over the named-pipe IPC.");
pingCommand.SetHandler(async () => Environment.ExitCode = await RunPingAsync());
root.AddCommand(pingCommand);

var statusCommand = new Command("status", "Print the service status from the IPC handshake.");
statusCommand.SetHandler(async () => Environment.ExitCode = await RunStatusAsync());
root.AddCommand(statusCommand);

var statsCommand = new Command("stats", "Print capture-pipeline observation counters.");
statsCommand.SetHandler(async () => Environment.ExitCode = await RunStatsAsync());
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
snapshotCommand.SetHandler(
    async (bool all, bool json) => Environment.ExitCode = await RunSnapshotAsync(all, json),
    allOption, jsonOption);
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
appsCommand.SetHandler(
    async (string w, int n, bool j) => Environment.ExitCode = await RunAppsAsync(w, n, j),
    windowOption, topOption, jsonOption2);
root.AddCommand(appsCommand);

var appIdArg = new Argument<int>("appId", "App id (see `zvctl apps`).");

var appCommand = new Command("app", "Show detail for one app (summary, sessions, time series).");
appCommand.AddArgument(appIdArg);
appCommand.AddOption(windowOption);
appCommand.AddOption(grainOption);
appCommand.AddOption(jsonOption2);
appCommand.SetHandler(
    async (int id, string w, string g, bool j) => Environment.ExitCode = await RunAppDetailAsync(id, w, g, j),
    appIdArg, windowOption, grainOption, jsonOption2);
root.AddCommand(appCommand);

var connectionsCommand = new Command("connections", "List endpoints an app talked to in the window.");
connectionsCommand.AddArgument(appIdArg);
connectionsCommand.AddOption(windowOption);
connectionsCommand.AddOption(topOption);
connectionsCommand.AddOption(jsonOption2);
connectionsCommand.SetHandler(
    async (int id, string w, int n, bool j) => Environment.ExitCode = await RunConnectionsAsync(id, w, n, j),
    appIdArg, windowOption, topOption, jsonOption2);
root.AddCommand(connectionsCommand);

var historyCommand = new Command("history", "Aggregate traffic time series across all apps.");
historyCommand.AddOption(windowOption);
historyCommand.AddOption(grainOption);
historyCommand.AddOption(jsonOption2);
historyCommand.SetHandler(
    async (string w, string g, bool j) => Environment.ExitCode = await RunHistoryAsync(w, g, j),
    windowOption, grainOption, jsonOption2);
root.AddCommand(historyCommand);

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
    catch (IpcVersionMismatchException ex)
    {
        Console.Error.WriteLine($"ERROR  version mismatch: client {ex.ClientVersion}, server {ex.ServerVersion}");
        return 2;
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("ERROR  service is not running (pipe connect timed out).");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR  {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
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
    catch (IpcVersionMismatchException ex)
    {
        Console.Error.WriteLine($"ERROR  version mismatch: client {ex.ClientVersion}, server {ex.ServerVersion}");
        return 2;
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("ERROR  service is not running (pipe connect timed out).");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR  {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunStatsAsync()
{
    try
    {
        await using var client = await ZenVizorPipeClient.ConnectAsync();
        var envelope = await client.Proxy.GetCaptureStatsAsync();
        var s = envelope.Payload;
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
    catch (IpcVersionMismatchException ex)
    {
        Console.Error.WriteLine($"ERROR  version mismatch: client {ex.ClientVersion}, server {ex.ServerVersion}");
        return 2;
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("ERROR  service is not running (pipe connect timed out).");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR  {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
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

        PrintSnapshot(envelope, all);
        return 0;
    }
    catch (IpcVersionMismatchException ex)
    {
        Console.Error.WriteLine($"ERROR  version mismatch: client {ex.ClientVersion}, server {ex.ServerVersion}");
        return 2;
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("ERROR  service is not running (pipe connect timed out).");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR  {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

static void PrintSnapshot(IpcEnvelope<ActivitySnapshot> envelope, bool all)
{
    var snap = envelope.Payload;

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

    if (spec.StartsWith("from=", StringComparison.OrdinalIgnoreCase))
    {
        var parts = spec.Split(',', 2);
        if (parts.Length != 2) throw new ArgumentException("Custom window: from=UNIXMS,to=UNIXMS");
        var from = long.Parse(parts[0]["from=".Length..], CultureInfo.InvariantCulture);
        var to   = long.Parse(parts[1].StartsWith("to=", StringComparison.OrdinalIgnoreCase)
                              ? parts[1]["to=".Length..]
                              : parts[1], CultureInfo.InvariantCulture);
        return new QueryWindow(from, to);
    }

    if (spec.Length < 2) throw new ArgumentException($"Unrecognized window '{spec}'.");
    var unit = char.ToLowerInvariant(spec[^1]);
    var n = int.Parse(spec[..^1], CultureInfo.InvariantCulture);
    var ms = unit switch
    {
        'h' => n * 3_600_000L,
        'd' => n * 86_400_000L,
        'y' => n * 86_400_000L * 365L,
        _   => throw new ArgumentException($"Unrecognized window unit in '{spec}'. Use h, d, or y."),
    };
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    return new QueryWindow(nowMs - ms, nowMs);
}

static TrafficGrain ParseGrain(string spec) =>
    spec?.ToLowerInvariant() switch
    {
        "auto"    or null or "" => TrafficGrain.Auto,
        "samples" or "s"        => TrafficGrain.Samples,
        "hourly"  or "h"        => TrafficGrain.Hourly,
        "daily"   or "d"        => TrafficGrain.Daily,
        _ => throw new ArgumentException($"Unrecognized grain '{spec}'. Use auto|samples|hourly|daily."),
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

        var apps = envelope.Payload.Apps;
        if (apps.Count == 0)
        {
            Console.WriteLine("(no apps with traffic in the window)");
            return 0;
        }

        var rows = apps.Count > top ? apps.Take(top).ToList() : (IReadOnlyList<AppListEntry>)apps;
        const int idCol = 6, appCol = 32, pubCol = 28, sigCol = 11, byteCol = 14;
        Console.WriteLine($"{Pad("Id", idCol)} {Pad("App", appCol)} {Pad("Publisher", pubCol)} {Pad("Sig", sigCol)} {RPad("Up", byteCol)} {RPad("Down", byteCol)}");
        Console.WriteLine(new string('-', idCol + appCol + pubCol + sigCol + byteCol * 2 + 6));
        foreach (var a in rows)
        {
            var pub = string.IsNullOrEmpty(a.Publisher) ? "(unknown)" : a.Publisher;
            Console.WriteLine(
                $"{RPad(a.AppId.ToString(CultureInfo.InvariantCulture), idCol)} " +
                $"{Pad(a.ImageName, appCol)} {Pad(pub, pubCol)} {Pad(a.SignatureStatus, sigCol)} " +
                $"{RPad(FormatBytes(a.BytesUp), byteCol)} {RPad(FormatBytes(a.BytesDown), byteCol)}");
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

        var d = envelope.Payload;
        Console.WriteLine($"App      : {d.Summary.ImageName}  (id {d.Summary.AppId})");
        Console.WriteLine($"Path     : {d.Summary.ImagePath}");
        Console.WriteLine($"Publisher: {(string.IsNullOrEmpty(d.Summary.Publisher) ? "(unknown)" : d.Summary.Publisher)}");
        Console.WriteLine($"Signature: {d.Summary.SignatureStatus}{(d.Summary.IsUserWritablePath ? "  [user-writable path]" : "")}");
        Console.WriteLine($"Window   : {FormatWindow(d.Window)}  (grain={d.GrainUsed})");
        Console.WriteLine($"Totals   : Up={FormatBytes(d.Summary.BytesUp)}  Down={FormatBytes(d.Summary.BytesDown)}");
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

        var rows = envelope.Payload.Connections;
        if (rows.Count == 0)
        {
            Console.WriteLine("(no endpoints in window)");
            return 0;
        }
        var shown = rows.Count > top ? rows.Take(top).ToList() : (IReadOnlyList<ConnectionRow>)rows;
        const int protoCol = 6, addrCol = 42, portCol = 7, clsCol = 7, byteCol = 12;
        Console.WriteLine($"{Pad("Proto", protoCol)} {Pad("Remote", addrCol)} {RPad("Port", portCol)} {Pad("Class", clsCol)} {RPad("Up", byteCol)} {RPad("Down", byteCol)}");
        Console.WriteLine(new string('-', protoCol + addrCol + portCol + clsCol + byteCol * 2 + 6));
        foreach (var c in shown)
        {
            Console.WriteLine(
                $"{Pad(c.Protocol, protoCol)} {Pad(c.RemoteAddress, addrCol)} {RPad(c.RemotePort.ToString(CultureInfo.InvariantCulture), portCol)} {Pad(c.RemoteClass, clsCol)} {RPad(FormatBytes(c.BytesUp), byteCol)} {RPad(FormatBytes(c.BytesDown), byteCol)}");
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

        var h = envelope.Payload;
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

static int ReportError(Exception ex)
{
    if (ex is IpcVersionMismatchException vex)
    {
        Console.Error.WriteLine($"ERROR  version mismatch: client {vex.ClientVersion}, server {vex.ServerVersion}");
        return 2;
    }
    if (ex is TimeoutException)
    {
        Console.Error.WriteLine("ERROR  service is not running (pipe connect timed out).");
        return 3;
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
    DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

static string FormatWindow(QueryWindow w) =>
    $"{FormatTime(w.FromUnixMs)} → {FormatTime(w.ToUnixMs)}";
