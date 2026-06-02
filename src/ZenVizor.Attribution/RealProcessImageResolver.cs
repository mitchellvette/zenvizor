using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;

namespace ZenVizor.Attribution;

/// <summary>
/// PID → process identity via <see cref="Process.GetProcessById(int)"/>.
/// Cached per PID + start-time pair, since the underlying lookup is the hot
/// path's most expensive non-IPC call.
/// </summary>
/// <remarks>
/// PID 4 (System) is synthesized rather than queried — kernel-mode processes
/// often expose no image path to user-mode callers. CLAUDE.md invariant #5:
/// kernel-attributed traffic is an honest boundary, surfaced as "System".
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RealProcessImageResolver : IProcessImageResolver
{
    private const int SystemPid = 4;

    private static readonly ProcessImageInfo SystemImage = new(
        Pid: SystemPid,
        ImagePath: "(kernel)",
        ImageName: "System",
        StartTimeUnixMs: 0);

    private readonly ILogger _logger;
    private readonly Dictionary<int, CachedImage> _cache = new();
    private readonly object _gate = new();

    public RealProcessImageResolver(ILogger<RealProcessImageResolver>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public ProcessImageInfo? Resolve(int pid)
    {
        if (pid == SystemPid)
        {
            return SystemImage;
        }
        if (pid <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(pid, out var cached) && cached.IsFresh)
            {
                return cached.Image;
            }
        }

        ProcessImageInfo? info = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            var startMs = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)
                .ToUnixTimeMilliseconds();

            string imagePath;
            try
            {
                // MainModule.FileName requires the same bitness or admin.
                // Service runs as LocalSystem so this should usually succeed.
                imagePath = process.MainModule?.FileName ?? process.ProcessName;
            }
            catch
            {
                // Protected processes (some antivirus, system services) deny access.
                imagePath = process.ProcessName;
            }

            info = new ProcessImageInfo(
                Pid: pid,
                ImagePath: imagePath,
                ImageName: Path.GetFileName(imagePath),
                StartTimeUnixMs: startMs);
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Process.GetProcessById({Pid}) failed.", pid);
        }

        lock (_gate)
        {
            if (info is null)
            {
                _cache.Remove(pid);
            }
            else
            {
                _cache[pid] = new CachedImage(info);
            }
        }

        return info;
    }

    private readonly record struct CachedImage(ProcessImageInfo Image)
    {
        public bool IsFresh => true;  // Phase 1: cache is invalidated only on PID reuse.
    }
}
