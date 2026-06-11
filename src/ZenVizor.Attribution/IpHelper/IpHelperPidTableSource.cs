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
    /// <summary>
    /// Cap on retry attempts when the table grows between the size probe
    /// and the actual call. In practice the table stabilizes after one
    /// retry; the cap is a defensive bound against pathological churn.
    /// </summary>
    private const int MaxResizeRetries = 5;

    private const int HeaderSize = 4;   // DWORD dwNumEntries

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
        var (buffer, bufferSize) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedTcpTable(ptr, ref len, false,
                NativeMethods.AF_INET, NativeMethods.TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
            var rowCount = ClampedRowCount(buffer, bufferSize, rowSize);
            var rowPtr = IntPtr.Add(buffer, HeaderSize);
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
    }

    private static void CaptureTcpV6(List<PidTableEntry> entries)
    {
        var (buffer, bufferSize) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedTcpTable(ptr, ref len, false,
                NativeMethods.AF_INET6, NativeMethods.TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
            var rowCount = ClampedRowCount(buffer, bufferSize, rowSize);
            var rowPtr = IntPtr.Add(buffer, HeaderSize);
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
        var (buffer, bufferSize) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedUdpTable(ptr, ref len, false,
                NativeMethods.AF_INET, NativeMethods.UdpTableClass.UDP_TABLE_OWNER_PID, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_UDPROW_OWNER_PID>();
            var rowCount = ClampedRowCount(buffer, bufferSize, rowSize);
            var rowPtr = IntPtr.Add(buffer, HeaderSize);
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
        var (buffer, bufferSize) = AllocAndCall((IntPtr ptr, ref int len) =>
            NativeMethods.GetExtendedUdpTable(ptr, ref len, false,
                NativeMethods.AF_INET6, NativeMethods.UdpTableClass.UDP_TABLE_OWNER_PID, 0));
        if (buffer == IntPtr.Zero) return;
        try
        {
            var rowSize = Marshal.SizeOf<NativeMethods.MIB_UDP6ROW_OWNER_PID>();
            var rowCount = ClampedRowCount(buffer, bufferSize, rowSize);
            var rowPtr = IntPtr.Add(buffer, HeaderSize);
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

    /// <summary>
    /// Allocate-and-call helper with bounded retry. The IP Helper table can
    /// grow between the size probe and the actual call; when that happens,
    /// the call returns <c>ERROR_INSUFFICIENT_BUFFER</c> and writes the new
    /// required size back into <paramref name="query"/>'s size parameter.
    /// Without a retry, the snapshot for that tier silently goes empty
    /// (the original implementation threw on the second call, swallowed
    /// upstream by the catch in <see cref="Capture"/>).
    /// </summary>
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

        for (var attempt = 0; attempt < MaxResizeRetries; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            var freeBuffer = true;
            try
            {
                var allocatedSize = size;
                result = query(buffer, ref size);
                if (result == NativeMethods.NO_ERROR)
                {
                    freeBuffer = false;
                    return (buffer, allocatedSize);
                }
                if (result == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                {
                    // Table grew. `size` is now the new required size — retry
                    // with a fresh, larger buffer.
                    continue;
                }
                throw new InvalidOperationException(
                    $"IP Helper table call failed with Win32 error {result}.");
            }
            finally
            {
                if (freeBuffer) Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException(
            $"IP Helper table did not stabilize after {MaxResizeRetries} retries.");
    }

    /// <summary>
    /// Clamp the on-wire row count against the actual buffer size. The kernel
    /// is trustworthy here, but defending against a buffer the wrong size
    /// (rebound truncation, future SDK drift) is cheap insurance: a buffer
    /// overread that reads garbage row bytes would produce attribution to
    /// a fictitious PID.
    /// </summary>
    private static int ClampedRowCount(IntPtr buffer, int bufferSize, int rowSize)
    {
        if (bufferSize <= HeaderSize || rowSize <= 0) return 0;
        var declared = Marshal.ReadInt32(buffer);
        if (declared <= 0) return 0;
        var maxRows = (bufferSize - HeaderSize) / rowSize;
        return declared > maxRows ? maxRows : declared;
    }

    /// <summary>Port fields in IP Helper rows are network-byte-order in the low 16 bits.</summary>
    private static int NetworkPort(uint raw) =>
        ((int)((raw & 0xFF) << 8)) | ((int)((raw >> 8) & 0xFF));
}
