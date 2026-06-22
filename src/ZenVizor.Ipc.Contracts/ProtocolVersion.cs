// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts;

/// <summary>
/// IPC wire-protocol version. Bump on incompatible changes.
/// The server rejects connecting clients whose Major differs.
/// </summary>
public static class ProtocolVersion
{
    public const int Major = 1;
    public const int Minor = 0;

    public const string Current = "1.0";

    public static bool IsCompatible(string clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            return false;
        }

        var parts = clientVersion.Split('.');
        if (parts.Length < 1 || !int.TryParse(parts[0], out var clientMajor))
        {
            return false;
        }

        return clientMajor == Major;
    }
}
