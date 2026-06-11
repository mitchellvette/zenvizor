using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;
using static ZenVizor.Attribution.Services.NativeMethods;

namespace ZenVizor.Attribution.Services;

/// <summary>
/// PID → hosted-service-name list, via SCM <c>EnumServicesStatusEx</c> +
/// <c>SC_ENUM_PROCESS_INFO</c>. Phase 2 Q3 chose native SCM over WMI because
/// WMI's first-query cost is enough on its own to break the &lt; 1% idle-CPU
/// budget.
/// </summary>
/// <remarks>
/// <para>
/// A short TTL cache absorbs bursts of session-opens during a flush tick. Per
/// Phase 2 Q2, the resolver is called exactly once per session-open per PID;
/// the cache exists only so a single flush that opens 20 svchost sessions does
/// not enumerate SCM 20 times.
/// </para>
/// <para>
/// Returns <c>null</c> when the PID hosts no services. Bytes are NOT split
/// across services (CLAUDE.md invariant #5).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ScmServiceHostResolver : IServiceHostResolver
{
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _cacheTtl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private DateTimeOffset _snapshotTakenAt = DateTimeOffset.MinValue;
    private Dictionary<int, IReadOnlyList<string>> _snapshot = new();

    public ScmServiceHostResolver(
        ILogger<ScmServiceHostResolver>? logger = null,
        TimeSpan? cacheTtl = null,
        Func<DateTimeOffset>? clock = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<string>? ResolveHostedServices(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        var now = _clock();
        Dictionary<int, IReadOnlyList<string>> snapshot;
        lock (_gate)
        {
            if (now - _snapshotTakenAt > _cacheTtl)
            {
                try
                {
                    _snapshot = EnumerateServiceHosts();
                    _snapshotTakenAt = now;
                }
                catch (Win32Exception ex)
                {
                    _logger.LogWarning(ex,
                        "EnumServicesStatusEx failed (error {Code}); returning empty snapshot.",
                        ex.NativeErrorCode);
                    _snapshot = new Dictionary<int, IReadOnlyList<string>>();
                    _snapshotTakenAt = now;
                }
            }
            snapshot = _snapshot;
        }

        return snapshot.TryGetValue(pid, out var services) ? services : null;
    }

    private static Dictionary<int, IReadOnlyList<string>> EnumerateServiceHosts()
    {
        using var scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");
        }

        // Aggregate results across paginated calls. SCM returns ERROR_MORE_DATA
        // when its buffer fills before draining the snapshot; we resume from
        // lpResumeHandle until the last call returns success. Previously the
        // resolver discarded the partial first page on the second probe call,
        // losing services for high-PID machines (many concurrent services).
        var result = new Dictionary<int, List<string>>();

        uint bufferSize = 0;
        IntPtr buffer = IntPtr.Zero;
        uint resumeHandle = 0;
        try
        {
            while (true)
            {
                uint bytesNeeded = 0;
                uint servicesReturned = 0;
                var ok = EnumServicesStatusExW(
                    scm,
                    SC_ENUM_PROCESS_INFO,
                    SERVICE_WIN32,
                    SERVICE_STATE_ALL,
                    buffer,
                    bufferSize,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                var lastError = Marshal.GetLastWin32Error();

                if (servicesReturned > 0)
                {
                    AppendPage(result, buffer, (int)servicesReturned);
                }

                if (ok)
                {
                    // Drained: no more entries.
                    break;
                }

                if (lastError == ERROR_MORE_DATA)
                {
                    // More entries waiting. Grow the buffer if SCM is telling
                    // us this page needs more space (bytesNeeded > current
                    // bufferSize); otherwise keep the same buffer and let the
                    // resume handle move us forward.
                    if (bytesNeeded > bufferSize)
                    {
                        if (buffer != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(buffer);
                            buffer = IntPtr.Zero;
                        }
                        bufferSize = bytesNeeded;
                        buffer = Marshal.AllocHGlobal((int)bufferSize);
                    }
                    continue;
                }

                throw new Win32Exception(lastError, "EnumServicesStatusEx enumeration failed.");
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        var sealed_ = new Dictionary<int, IReadOnlyList<string>>(result.Count);
        foreach (var (pid, list) in result)
        {
            list.Sort(StringComparer.Ordinal);
            sealed_[pid] = list;
        }
        return sealed_;
    }

    private static void AppendPage(Dictionary<int, List<string>> result, IntPtr buffer, int count)
    {
        if (buffer == IntPtr.Zero || count <= 0) return;

        var entrySize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
        for (var i = 0; i < count; i++)
        {
            var entryPtr = IntPtr.Add(buffer, i * entrySize);
            var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(entryPtr);

            var pid = (int)entry.ServiceStatusProcess.dwProcessId;
            if (pid <= 0)
            {
                continue; // service not running
            }

            var name = entry.lpServiceName == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(entry.lpServiceName);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!result.TryGetValue(pid, out var list))
            {
                list = new List<string>(capacity: 1);
                result[pid] = list;
            }
            list.Add(name);
        }
    }
}
