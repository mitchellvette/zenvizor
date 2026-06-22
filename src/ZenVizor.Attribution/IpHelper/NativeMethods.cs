// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZenVizor.Attribution.IpHelper;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const int AF_INET  = 2;
    internal const int AF_INET6 = 23;

    // ERROR_INSUFFICIENT_BUFFER — call again with a larger buffer.
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int NO_ERROR = 0;

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    internal static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        TcpTableClass TableClass,
        uint Reserved);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    internal static partial uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        UdpTableClass TableClass,
        uint Reserved);

    internal enum TcpTableClass
    {
        TCP_TABLE_OWNER_PID_ALL = 5,
    }

    internal enum UdpTableClass
    {
        UDP_TABLE_OWNER_PID = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;     // big-endian; only low 16 bits used
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }
}
