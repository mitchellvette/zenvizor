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
