// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZenVizor.Attribution.Services;

/// <summary>
/// Minimal P/Invoke surface for read-only enumeration of Windows services via
/// the Service Control Manager. Phase 2 Q3 — native SCM only; no WMI.
/// </summary>
internal static class NativeMethods
{
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const uint SERVICE_WIN32                = 0x00000030; // OWN + SHARE
    public const uint SERVICE_STATE_ALL            = 0x00000003;
    public const int  SC_ENUM_PROCESS_INFO         = 0;
    public const int  ERROR_MORE_DATA              = 234;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = false, SetLastError = true)]
    public static extern SafeScmHandle OpenSCManagerW(
        string? lpMachineName,
        string? lpDatabaseName,
        uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = false, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumServicesStatusExW(
        SafeScmHandle hSCManager,
        int InfoLevel,
        uint dwServiceType,
        uint dwServiceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    /// <summary>
    /// Deterministically closes an SCM handle via <c>CloseServiceHandle</c>.
    /// Wrapping the raw <c>IntPtr</c> in a <see cref="SafeHandle"/> means an
    /// exception between <c>OpenSCManager</c> and the manual close path can
    /// no longer leak the handle.
    /// </summary>
    public sealed class SafeScmHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeScmHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }
    }
}
