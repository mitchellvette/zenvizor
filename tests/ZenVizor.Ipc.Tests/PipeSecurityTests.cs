// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using ZenVizor.Ipc.Server;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// Asserts the ACL the pipe server stamps onto every <c>ZenVizor.Ipc.v1</c>
/// listener instance. The named pipe is the only authorized IPC surface, so
/// the ACE set is part of the security contract — INTERACTIVE must NOT have
/// <see cref="PipeAccessRights.CreateNewInstance"/>, which would otherwise let
/// a non-elevated local user pre-create a listener instance and impersonate
/// the service to the next connecting client (local pipe-instance squatting).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeSecurityTests
{
    [Fact]
    public void InteractiveAce_GrantsReadWriteButNotCreateNewInstance()
    {
        var security = ZenVizorPipeServer.BuildPipeSecurity();
        var interactiveAce = GetAceFor(security, WellKnownSidType.InteractiveSid);

        interactiveAce.AccessControlType.Should().Be(AccessControlType.Allow);

        const PipeAccessRights expected =
            PipeAccessRights.ReadData |
            PipeAccessRights.WriteData;

        // ReadWrite is a composite (ReadData | WriteData | a handful of
        // sub-rights). What matters is the negative: CreateNewInstance must
        // NOT be in the mask. Assert both halves explicitly.
        (interactiveAce.PipeAccessRights & expected)
            .Should().Be(expected, "INTERACTIVE needs Read/Write to talk to the server");

        (interactiveAce.PipeAccessRights & PipeAccessRights.CreateNewInstance)
            .Should().Be((PipeAccessRights)0,
                "INTERACTIVE must not be able to stand up new listener instances " +
                "on the pipe name (pipe-instance squatting hole)");
    }

    [Fact]
    public void SystemAndAdminAces_HaveFullControl()
    {
        var security = ZenVizorPipeServer.BuildPipeSecurity();

        var system = GetAceFor(security, WellKnownSidType.LocalSystemSid);
        system.AccessControlType.Should().Be(AccessControlType.Allow);
        system.PipeAccessRights.Should().Be(PipeAccessRights.FullControl);

        var admins = GetAceFor(security, WellKnownSidType.BuiltinAdministratorsSid);
        admins.AccessControlType.Should().Be(AccessControlType.Allow);
        admins.PipeAccessRights.Should().Be(PipeAccessRights.FullControl);
    }

    [Fact]
    public void Acl_ContainsNoOtherPrincipals()
    {
        var security = ZenVizorPipeServer.BuildPipeSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        rules.Count.Should().Be(3, "exactly SYSTEM, BUILTIN\\Administrators, INTERACTIVE");

        var expectedSids = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null).Value,
        };

        foreach (PipeAccessRule rule in rules)
        {
            expectedSids.Should().Contain(((SecurityIdentifier)rule.IdentityReference).Value);
        }
    }

    private static PipeAccessRule GetAceFor(PipeSecurity security, WellKnownSidType sidType)
    {
        var sid = new SecurityIdentifier(sidType, null);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (PipeAccessRule rule in rules)
        {
            if (rule.IdentityReference is SecurityIdentifier ruleSid && ruleSid.Equals(sid))
            {
                return rule;
            }
        }
        throw new InvalidOperationException($"No ACE for {sidType} in pipe security.");
    }
}
