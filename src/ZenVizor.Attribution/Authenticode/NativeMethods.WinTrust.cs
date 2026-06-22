// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace ZenVizor.Attribution.Authenticode;

/// <summary>
/// Minimal P/Invoke surface for offline Authenticode verification via
/// <c>WinVerifyTrust</c>. CLAUDE.md invariant #1 — <c>WTD_REVOKE_NONE</c>
/// is the only revocation-check value we ever pass.
/// </summary>
internal static class NativeMethods
{
    public const uint WTD_UI_NONE                  = 2;
    public const uint WTD_REVOKE_NONE              = 0;
    public const uint WTD_CHOICE_FILE              = 1;
    public const uint WTD_STATEACTION_VERIFY       = 1;
    public const uint WTD_STATEACTION_CLOSE        = 2;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    public const uint WTD_REVOCATION_CHECK_NONE    = 0x10;

    public const uint TRUST_E_NOSIGNATURE = 0x800B0100;

    // {00AAC56B-CD44-11d0-8CC2-00C04FC295EE}
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    public static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid pgActionID,
        ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA
    {
        public uint   cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint   dwUIChoice;
        public uint   fdwRevocationChecks;
        public uint   dwUnionChoice;
        public IntPtr pFile; // pointer to WINTRUST_FILE_INFO
        public uint   dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint   dwProvFlags;
        public uint   dwUIContext;
    }
}
