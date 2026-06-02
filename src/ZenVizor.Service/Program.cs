using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;
using ZenVizor.Service;
using ZenVizor.Storage;

[assembly: SupportedOSPlatform("windows")]

var dataDir = StorageConstants.DefaultDataDirectory;
var logDir = Path.Combine(dataDir, "logs");
Directory.CreateDirectory(logDir);

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.File(
        path: Path.Combine(logDir, "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true);

// Try to wire EventLog; creating the source the first time needs admin.
// If we can't, fall back silently — file sink still captures startup events.
try
{
    if (!EventLog.SourceExists(ServiceConstants.EventLogSource))
    {
        EventLog.CreateEventSource(ServiceConstants.EventLogSource, ServiceConstants.EventLogName);
    }

    loggerConfig = loggerConfig.WriteTo.EventLog(
        source: ServiceConstants.EventLogSource,
        logName: ServiceConstants.EventLogName,
        manageEventSource: false,
        restrictedToMinimumLevel: LogEventLevel.Information);
}
catch
{
    // EventLog optional — file sink is the source of truth.
}

Log.Logger = loggerConfig.CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = ServiceConstants.ServiceName;
    });

    builder.Services.AddSerilog();
    builder.Services.AddHostedService<ZenVizorHostedService>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ZenVizor service terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
