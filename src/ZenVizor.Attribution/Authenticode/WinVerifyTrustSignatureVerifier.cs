// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;
using static ZenVizor.Attribution.Authenticode.NativeMethods;

namespace ZenVizor.Attribution.Authenticode;

/// <summary>
/// <see cref="ISignatureVerifier"/> backed by <c>WinVerifyTrust</c> with
/// <c>WTD_REVOKE_NONE</c> + <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c>. Fully offline —
/// no revocation checks, no timestamp-server lookups. (CLAUDE.md invariant #1.)
/// </summary>
/// <remarks>
/// <para>
/// Phase 2 scope: embedded Authenticode signatures only. Catalog-signed Windows
/// system binaries (e.g. svchost.exe, explorer.exe) will report as
/// <c>"Unsigned"</c> until a catalog-aware verification path is added. This is
/// documented as a known boundary: those binaries live in <c>C:\Windows</c> and
/// the <c>is_user_writable_path</c> flag is <c>false</c> for them, so the
/// Phase 6 alert combination cannot misfire.
/// </para>
/// <para>
/// Publisher = Subject CN of the embedded signing certificate. Extracted via
/// <see cref="X509Certificate.CreateFromSignedFile(string)"/> after the
/// verification step succeeds.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinVerifyTrustSignatureVerifier : ISignatureVerifier
{
    private readonly ILogger _logger;

    public WinVerifyTrustSignatureVerifier(ILogger<WinVerifyTrustSignatureVerifier>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public SignatureVerificationResult Verify(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            return SignatureVerificationResult.Unchecked;
        }

        var hr = VerifyEmbedded(imagePath);
        if (hr == 0)
        {
            var publisher = TryExtractPublisher(imagePath);
            return new SignatureVerificationResult("Signed", publisher);
        }

        if ((uint)hr == TRUST_E_NOSIGNATURE)
        {
            return new SignatureVerificationResult("Unsigned", null);
        }

        _logger.LogDebug(
            "WinVerifyTrust returned 0x{Hr:X8} for {Path} — classifying as Invalid.",
            hr, imagePath);
        return new SignatureVerificationResult("Invalid", null);
    }

    private static int VerifyEmbedded(string imagePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = imagePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData      = IntPtr.Zero,
                dwUIChoice          = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice       = WTD_CHOICE_FILE,
                pFile               = fileInfoPtr,
                dwStateAction       = WTD_STATEACTION_VERIFY,
                hWVTStateData       = IntPtr.Zero,
                pwszURLReference    = IntPtr.Zero,
                dwProvFlags         = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE,
                dwUIContext         = 0,
            };

            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // Always release the state; ignore the result of the close.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return hr;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private string? TryExtractPublisher(string imagePath)
    {
        try
        {
            // X509Certificate.CreateFromSignedFile is the only managed API that pulls
            // the signing certificate directly out of a signed PE. The .NET 10
            // X509CertificateLoader replacement targets DER/PEM/PKCS#12 files, not
            // Authenticode-embedded certs, so there is no like-for-like substitute.
            // Suppressing SYSLIB0057 locally rather than reimplementing the PE
            // WIN_CERTIFICATE walk + SignedCms parse for a publisher string.
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(imagePath));
#pragma warning restore SYSLIB0057
            return ExtractSubjectCommonName(cert.Subject);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract publisher from {Path}.", imagePath);
            return null;
        }
    }

    /// <summary>
    /// Pulls <c>CN=</c> out of an X.500 distinguished name string. The X509 stack
    /// returns the subject in a comma-separated form like
    /// <c>"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, ..."</c>.
    /// Commas inside quoted values are preserved by the framework's escaping;
    /// we split on top-level commas with a tiny state machine.
    /// </summary>
    internal static string? ExtractSubjectCommonName(string subject)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var components = SplitDistinguishedName(subject);
        foreach (var component in components)
        {
            var trimmed = component.TrimStart();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[3..].Trim();
                if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                {
                    value = value[1..^1];
                }
                return value;
            }
        }
        return null;
    }

    private static List<string> SplitDistinguishedName(string subject)
    {
        var result = new List<string>();
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < subject.Length; i++)
        {
            var ch = subject[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(subject[start..i]);
                start = i + 1;
            }
        }
        result.Add(subject[start..]);
        return result;
    }
}
