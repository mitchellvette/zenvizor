using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;

namespace ZenVizor.Attribution.IpHelper;

/// <summary>
/// Real IP Helper-backed <see cref="IPidTableSnapshotSource"/>. Wraps
/// <c>GetExtendedTcpTable</c> / <c>GetExtendedUdpTable</c> for both IPv4 and IPv6.
/// Snapshot is cached and refreshed at most once per <see cref="PollIntervalMs"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpHelperPidTableSource : IPidTableSnapshotSource
{
    private readonly long _pollIntervalMs;
    private readonly ILogger _logger;
    private readonly Func<long> _nowUnixMs;
    private readonly object _gate = new();
    private PidTableSnapshot _current;

    public IpHelperPidTableSource(
        long pollIntervalMs = 1000,
        ILogger<IpHelperPidTableSource>? logger = null,
        Func<long>? nowUnixMs = null)
    {
        if (pollIntervalMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pollIntervalMs));
        }
        _pollIntervalMs = pollIntervalMs;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _current = PidTableSnapshot.Empty(_nowUnixMs());
    }

    public long PollIntervalMs => _pollIntervalMs;

    public PidTableSnapshot CurrentSnapshot
    {
        get
        {
            var now = _nowUnixMs();
            lock (_gate)
            {
                if (now - _current.TakenAtUnixMs >= _pollIntervalMs)
                {
                    _current = Capture(now);
                }
                return _current;
            }
        }
    }

    /// <summary>Force a refresh on the next caller, e.g. after a known process exit.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _current = PidTableSnapshot.Empty(0);
        }
    }

    private PidTableSnapshot Capture(long nowUnixMs)
    {
        var entries = new List<PidTableEntry>(capacity: 256);
        try
        {
            CaptureTcpV4(entries);
            CaptureTcpV6(entries);
            CaptureUdpV4(entries);
            CaptureUdpV6(entries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IP Helper table capture failed; returning empty snapshot.");
            return PidTableSnapshot.Empty(nowUnixMs);
        }

        return new PidTableSnapshot(nowUnixMs, entries);
    }

    private static void CaptureTcpV4(List<PidTableEntry> entries)
    {
        var (buffer, size) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedTcpTable(ptr, ref len, false,
                NativeMethods.AF_INET, NativeMethods.TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
            var rowPtr = IntPtr.Add(buffer, IntPtr.Size); // dwNumEntries (DWORD) — but padded; account properly
            // The MIB_TCPTABLE_OWNER_PID layout is: DWORD dwNumEntries; MIB_TCPROW_OWNER_PID table[ANY_SIZE];
            // dwNumEntries is 4 bytes; on x64 the array follows immediately (no padding because rows are 4-byte aligned).
            rowPtr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
                var local = new IPEndPoint(new IPAddress(row.localAddr), NetworkPort(row.localPort));
                entries.Add(new PidTableEntry(Protocol.Tcp, local, (int)row.owningPid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        _ = size;
    }

    private static void CaptureTcpV6(List<PidTableEntry> entries)
    {
        var (buffer, _) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedTcpTable(ptr, ref len, false,
                NativeMethods.AF_INET6, NativeMethods.TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
            var rowPtr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
                var local = new IPEndPoint(new IPAddress(row.localAddr, row.localScopeId), NetworkPort(row.localPort));
                entries.Add(new PidTableEntry(Protocol.Tcp, local, (int)row.owningPid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CaptureUdpV4(List<PidTableEntry> entries)
    {
        var (buffer, _) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedUdpTable(ptr, ref len, false,
                NativeMethods.AF_INET, NativeMethods.UdpTableClass.UDP_TABLE_OWNER_PID, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_UDPROW_OWNER_PID>();
            var rowPtr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_UDPROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
                var local = new IPEndPoint(new IPAddress(row.localAddr), NetworkPort(row.localPort));
                entries.Add(new PidTableEntry(Protocol.Udp, local, (int)row.owningPid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CaptureUdpV6(List<PidTableEntry> entries)
    {
        var (buffer, _) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedUdpTable(ptr, ref len, false,
                NativeMethods.AF_INET6, NativeMethods.UdpTableClass.UDP_TABLE_OWNER_PID, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_UDP6ROW_OWNER_PID>();
            var rowPtr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_UDP6ROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
                var local = new IPEndPoint(new IPAddress(row.localAddr, row.localScopeId), NetworkPort(row.localPort));
                entries.Add(new PidTableEntry(Protocol.Udp, local, (int)row.owningPid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate uint TableQuery(IntPtr buffer, ref int size);

    private static (IntPtr Buffer, int Size) AllocAndCall(TableQuery query)
    {
        var size = 0;
        var result = query(IntPtr.Zero, ref size);
        if (result == NativeMethods.NO_ERROR && size == 0)
        {
            return (IntPtr.Zero, 0);
        }
        if (result != NativeMethods.ERROR_INSUFFICIENT_BUFFER && result != NativeMethods.NO_ERROR)
        {
            throw new InvalidOperationException(
                $"IP Helper size-probe call failed with Win32 error {result}.");
        }
        if (size == 0)
        {
            return (IntPtr.Zero, 0);
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = query(buffer, ref size);
            if (result != NativeMethods.NO_ERROR)
            {
                Marshal.FreeHGlobal(buffer);
                throw new InvalidOperationException(
                    $"IP Helper table call failed with Win32 error {result}.");
            }
            return (buffer, size);
        }
        catch
        {
            Marshal.FreeHGlobal(buffer);
            throw;
        }
    }

    /// <summary>Port fields in IP Helper rows are network-byte-order in the low 16 bits.</summary>
    private static int NetworkPort(uint raw) =>
        ((int)((raw & 0xFF) << 8)) | ((int)((raw >> 8) & 0xFF));
}
