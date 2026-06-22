// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using ZenVizor.Core.Observations;

namespace ZenVizor.Core.Classification;

/// <summary>
/// Pure-function classifier mapping a remote IP address to <see cref="RemoteClass.Local"/>
/// or <see cref="RemoteClass.Wan"/>. Covers IPv4 and IPv6; never touches the network.
/// </summary>
/// <remarks>
/// Allocation-free: <see cref="IPAddress.TryWriteBytes"/> writes into a stack-allocated
/// span instead of <see cref="IPAddress.GetAddressBytes"/>'s per-call <c>byte[]</c>.
/// Called once per network observation on the hot path, so this directly affects the
/// project's idle-CPU and working-set budgets.
/// </remarks>
public static class RemoteAddressClassifier
{
    public static RemoteClass Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return RemoteClass.Local;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ClassifyV4(address),
            AddressFamily.InterNetworkV6 => ClassifyV6(address),
            _ => RemoteClass.Wan,
        };
    }

    private static RemoteClass ClassifyV4(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out var written) || written != 4)
        {
            return RemoteClass.Wan;
        }

        // 10.0.0.0/8
        if (bytes[0] == 10)
        {
            return RemoteClass.Local;
        }

        // 172.16.0.0/12
        if (bytes[0] == 172 && (bytes[1] & 0xF0) == 0x10)
        {
            return RemoteClass.Local;
        }

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return RemoteClass.Local;
        }

        // 169.254.0.0/16 — link-local
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return RemoteClass.Local;
        }

        // 127.0.0.0/8 — already handled by IsLoopback, but defensive
        if (bytes[0] == 127)
        {
            return RemoteClass.Local;
        }

        return RemoteClass.Wan;
    }

    private static RemoteClass ClassifyV6(IPAddress address)
    {
        // IsIPv6LinkLocal covers fe80::/10. IsIPv6SiteLocal covers the deprecated
        // fec0::/10 — keep it Local for safety. ULA fc00::/7 (and the more-specific
        // fd00::/8) is not covered by the BCL helper; check manually.
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return RemoteClass.Local;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written) || written != 16)
        {
            return RemoteClass.Wan;
        }

        // fc00::/7 — unique local addresses. High 7 bits are 1111 110x.
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return RemoteClass.Local;
        }

        // IPv4-mapped (::ffff:0:0/96) — defer to v4 logic on the embedded address.
        if (address.IsIPv4MappedToIPv6)
        {
            return ClassifyV4(address.MapToIPv4());
        }

        return RemoteClass.Wan;
    }
}
