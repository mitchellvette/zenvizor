using System.CommandLine;
using System.Runtime.Versioning;
using ZenVizor.Ipc.Client;

[assembly: SupportedOSPlatform("windows")]

var root = new RootCommand("zvctl — ZenVizor CLI client.");

var pingCommand = new Command("ping", "Round-trip a ping over the named-pipe IPC.");
pingCommand.SetHandler(async () => Environment.ExitCode = await RunPingAsync());
root.AddCommand(pingCommand);

var statusCommand = new Command("status", "Print the service status from the IPC handshake.");
statusCommand.SetHandler(async () => Environment.ExitCode = await RunStatusAsync());
root.AddCommand(statusCommand);

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
