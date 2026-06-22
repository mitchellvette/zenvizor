// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Phase 9.b drift firewall: every <c>new Command("name", "description")</c>
/// literal in <c>src/ZenVizor.Cli/Program.cs</c> must have its description
/// mirrored verbatim in <c>installer/Resources/README.txt</c>. Trips when
/// a CLI verb is added, removed, or has its help text edited without the
/// installed README being updated in lockstep.
///
/// Source of truth is Program.cs (what users actually invoke). The README
/// is the mirror. Failure mode of this test = README is stale.
/// </summary>
public sealed class ReadMeCliDriftTests
{
    [Fact]
    public void Every_command_description_appears_in_installed_readme()
    {
        var repoRoot = FindRepoRoot();
        var programCsPath = Path.Combine(repoRoot, "src", "ZenVizor.Cli", "Program.cs");
        var readMePath = Path.Combine(repoRoot, "installer", "Resources", "README.txt");

        File.Exists(programCsPath).Should().BeTrue($"Program.cs must exist at {programCsPath}");
        File.Exists(readMePath).Should().BeTrue($"README.txt must exist at {readMePath}");

        var programSource = File.ReadAllText(programCsPath);
        var readMe = File.ReadAllText(readMePath);

        // Match both single-line and multi-line `new Command("name", "description")`
        // call sites. \s* spans newlines; [^"]+ is enough because no description in
        // Program.cs contains an embedded double-quote (verified at commit time).
        var pattern = new Regex(
            @"new\s+Command\s*\(\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\)",
            RegexOptions.Compiled);

        var matches = pattern.Matches(programSource);
        matches.Count.Should().BeGreaterThan(0, "Program.cs must declare at least one Command");

        var missing = new List<string>();
        foreach (Match m in matches)
        {
            var name = m.Groups[1].Value;
            var description = m.Groups[2].Value;

            if (!readMe.Contains(description, StringComparison.Ordinal))
            {
                missing.Add($"  zvctl …{name}…  -> description not found in README.txt: \"{description}\"");
            }
        }

        missing.Should().BeEmpty(
            "every zvctl command description in Program.cs must appear verbatim in " +
            "installer/Resources/README.txt — add or update the entry there when " +
            "you change a CLI command's help text. Missing entries:\n" +
            string.Join("\n", missing));
    }

    /// <summary>
    /// Walks up from the test source file's compile-time path looking for
    /// <c>ZenVizor.slnx</c>. Works in CI because the test assembly and the
    /// repo are built on the same machine.
    /// </summary>
    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ZenVizor.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Could not locate ZenVizor.slnx walking up from {callerFilePath}");
    }
}
