// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ZenVizor.Service;

/// <summary>
/// Owns the on-disk layout under <c>%ProgramData%\ZenVizor\</c>:
/// creates the directory and applies an ACL granting full control to
/// SYSTEM + BUILTIN\Administrators only (CLAUDE.md invariant #3 — the
/// DB is sensitive data; standard users must not be able to read it).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProgramDataAcl
{
    public static void EnsureDirectoryWithAcl(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var info = Directory.CreateDirectory(directory);
        var security = info.GetAccessControl();

        // Drop inherited ACEs so standard users don't pick up read access
        // from %ProgramData%.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var inheritAll = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inheritAll, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, inheritAll, PropagationFlags.None, AccessControlType.Allow));

        info.SetAccessControl(security);
    }
}
