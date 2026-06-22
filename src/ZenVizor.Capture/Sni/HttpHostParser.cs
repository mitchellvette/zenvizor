// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace ZenVizor.Capture.Sni;

/// <summary>
/// Phase 8.6 — pull the Host header out of a plaintext HTTP/1.1 request
/// (TCP 80). Tiny coverage on the modern web but near-free once the capture
/// substrate exists. Same robustness contract as the TLS/QUIC parsers: false /
/// empty on anything that isn't a well-formed request with a Host header, never
/// throws.
/// </summary>
internal static class HttpHostParser
{
    private static readonly string[] Methods =
        ["GET ", "POST ", "HEAD ", "PUT ", "DELETE ", "OPTIONS ", "PATCH ", "CONNECT "];

    public static bool TryParse(ReadOnlySpan<byte> payload, out string host)
    {
        host = string.Empty;
        if (payload.Length < 16) return false;

        // Cheap method sniff so we don't decode arbitrary binary as text.
        var head = Encoding.ASCII.GetString(payload[..Math.Min(8, payload.Length)]);
        if (!Methods.Any(m => head.StartsWith(m, StringComparison.Ordinal))) return false;

        var text = Encoding.ASCII.GetString(payload);
        foreach (var rawLine in text.Split("\r\n"))
        {
            if (rawLine.Length == 0) break; // end of headers
            if (rawLine.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                var value = rawLine[5..].Trim();
                var colon = value.IndexOf(':'); // strip :port
                if (colon >= 0) value = value[..colon];
                if (value.Length is > 0 and <= 253 && value.Contains('.'))
                {
                    host = value;
                    return true;
                }
                return false;
            }
        }
        return false;
    }
}
