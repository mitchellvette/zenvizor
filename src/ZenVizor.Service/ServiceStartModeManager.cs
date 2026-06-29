// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Service;

/// <summary>
/// Reads and writes the SCM start-mode of the ZenVizor service via the
/// Win32 Service Control Manager API. The service is its own caller —
/// the LocalSystem account it runs under has SERVICE_CHANGE_CONFIG on
/// its own registration, so no UAC elevation prompt is involved. The UI
/// never touches SCM directly; it issues an
/// <see cref="Ipc.Contracts.IZenVizorIpc.UpdateSettingsAsync"/> call and
/// this class makes the change from the elevated context.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceStartModeManager
{
    private const uint SC_MANAGER_CONNECT       = 0x0001;
    private const uint SERVICE_QUERY_CONFIG     = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG    = 0x0002;

    private const uint SERVICE_NO_CHANGE        = 0xFFFFFFFF;

    private const uint SERVICE_AUTO_START       = 0x00000002;
    private const uint SERVICE_DEMAND_START     = 0x00000003;
    private const uint SERVICE_DISABLED         = 0x00000004;

    private readonly string _serviceName;
    private readonly ILogger _logger;

    public ServiceStartModeManager(string serviceName, ILogger<ServiceStartModeManager>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _serviceName = serviceName;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Returns the live SCM start-mode. On failure (service not installed,
    /// SCM unreachable, query failed) logs at Error and returns
    /// <see cref="ServiceStartMode.Manual"/> as the conservative fallback —
    /// the Settings UI displays "Start with Windows: OFF" which is honest
    /// about the uncertainty. A subsequent toggle-on by the user routes
    /// through <see cref="Set"/>, which surfaces the underlying SCM
    /// failure as a Win32Exception → caution banner, instead of the
    /// silent Automatic-fallback lying about the actual state.
    /// </summary>
    public ServiceStartMode Get()
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            _logger.LogError("OpenSCManager failed: {Error}", Marshal.GetLastWin32Error());
            return ServiceStartMode.Manual;
        }
        try
        {
            var svc = OpenService(scm, _serviceName, SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero)
            {
                _logger.LogError(
                    "OpenService('{ServiceName}') failed: {Error}",
                    _serviceName, Marshal.GetLastWin32Error());
                return ServiceStartMode.Manual;
            }
            try
            {
                // Two-call pattern: first call with zero buffer returns the
                // required size in cbBytesNeeded; second call provides it.
                QueryServiceConfig(svc, IntPtr.Zero, 0, out var needed);
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!QueryServiceConfig(svc, buffer, needed, out _))
                    {
                        _logger.LogError(
                            "QueryServiceConfig failed: {Error}",
                            Marshal.GetLastWin32Error());
                        return ServiceStartMode.Manual;
                    }
                    var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer);
                    return config.dwStartType switch
                    {
                        SERVICE_AUTO_START   => ServiceStartMode.Automatic,
                        SERVICE_DEMAND_START => ServiceStartMode.Manual,
                        SERVICE_DISABLED     => ServiceStartMode.Disabled,
                        _                    => ServiceStartMode.Manual,
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Applies <paramref name="mode"/> via <c>ChangeServiceConfig</c>.
    /// Throws <see cref="Win32Exception"/> on failure so the IPC handler
    /// can surface the underlying SCM error to the caller rather than
    /// silently dropping the change.
    /// </summary>
    public void Set(ServiceStartMode mode)
    {
        var startType = mode switch
        {
            ServiceStartMode.Automatic => SERVICE_AUTO_START,
            ServiceStartMode.Manual    => SERVICE_DEMAND_START,
            ServiceStartMode.Disabled  => SERVICE_DISABLED,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");
        }
        try
        {
            var svc = OpenService(scm, _serviceName, SERVICE_CHANGE_CONFIG);
            if (svc == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"OpenService('{_serviceName}') failed.");
            }
            try
            {
                var ok = ChangeServiceConfig(
                    svc,
                    SERVICE_NO_CHANGE,    // dwServiceType
                    startType,            // dwStartType
                    SERVICE_NO_CHANGE,    // dwErrorControl
                    null, null, IntPtr.Zero, null, null, null, null);
                if (!ok)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "ChangeServiceConfig failed.");
                }
                _logger.LogInformation(
                    "Service start mode set to {Mode} ({StartType}).",
                    mode, startType);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        IntPtr hService,
        IntPtr lpServiceConfig,
        uint cbBufSize,
        out uint pcbBytesNeeded);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }
}
