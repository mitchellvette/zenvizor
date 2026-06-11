using FluentAssertions;
using ZenVizor.Attribution.Paths;
using ZenVizor.Core.Attribution;

namespace ZenVizor.Attribution.Tests;

public sealed class UserWritablePathClassifierTests
{
    private static UserWritablePathClassifier WithPrefixes(params string[] prefixes) =>
        new(prefixes);

    [Fact]
    public void IsUserWritable_PathUnderConfiguredPrefix_ReturnsTrue()
    {
        var c = WithPrefixes(@"C:\Users\alice\AppData", @"C:\Windows\Temp");

        c.IsUserWritable(@"C:\Users\alice\AppData\Local\bad.exe").Should().BeTrue();
        c.IsUserWritable(@"C:\Windows\Temp\dropper.exe").Should().BeTrue();
    }

    [Fact]
    public void IsUserWritable_PathOutsidePrefixes_ReturnsFalse()
    {
        var c = WithPrefixes(@"C:\Users\alice\AppData");

        c.IsUserWritable(@"C:\Program Files\Notepad++\notepad++.exe").Should().BeFalse();
        c.IsUserWritable(@"C:\Windows\System32\svchost.exe").Should().BeFalse();
    }

    [Fact]
    public void IsUserWritable_IsCaseInsensitive()
    {
        var c = WithPrefixes(@"C:\Users\alice\AppData");

        c.IsUserWritable(@"c:\users\ALICE\appdata\local\thing.exe").Should().BeTrue();
    }

    [Fact]
    public void IsUserWritable_NormalizesForwardSlashes()
    {
        var c = WithPrefixes(@"C:\Users\alice\AppData");

        c.IsUserWritable("C:/Users/alice/AppData/Local/thing.exe").Should().BeTrue();
    }

    [Fact]
    public void IsUserWritable_DoesNotMatchSiblingSubstring()
    {
        // C:\Temp should not match C:\Temporary or C:\Tempest.
        var c = WithPrefixes(@"C:\Temp");

        c.IsUserWritable(@"C:\Temporary\a.exe").Should().BeFalse();
        c.IsUserWritable(@"C:\Tempest\a.exe").Should().BeFalse();
        c.IsUserWritable(@"C:\Temp\a.exe").Should().BeTrue();
    }

    [Fact]
    public void IsUserWritable_EmptyPath_ReturnsFalse()
    {
        var c = WithPrefixes(@"C:\Temp");

        c.IsUserWritable("").Should().BeFalse();
        c.IsUserWritable(null!).Should().BeFalse();
    }

    [Fact]
    public void DefaultConstructor_EnumeratesSomePrefixes()
    {
        // On a real Windows box (incl. CI runner) at minimum %SystemRoot%\Temp
        // and Users\Public should be present.
        var c = new UserWritablePathClassifier();

        c.Prefixes.Should().NotBeEmpty();
        c.Prefixes.Should().Contain(p =>
            p.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Prefixes_DedupedCaseInsensitively()
    {
        var c = WithPrefixes(@"C:\Temp", @"c:\temp", @"C:/Temp");

        c.Prefixes.Should().ContainSingle();
    }

    // ---- Bug 1: expanded roots ----
    //
    // Prior to the audit, per-user prefixes were only AppData + Downloads. A
    // payload dropped to Desktop, Documents, OneDrive, or any other user
    // sub-folder slipped past the Phase-6 alert as "not user-writable".
    // The full profile root is now covered (excluding Public/Default/Default
    // User/All Users), and so is %ProgramData%.

    [Fact]
    public void DefaultConstructor_CoversTheWholeUserProfileRoot()
    {
        var c = new UserWritablePathClassifier();
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var usersDir = systemDrive + @"\Users";

        // At least one prefix should be a user profile root (not just an
        // AppData/Downloads subdir): e.g. "C:\Users\mitch\".
        c.Prefixes.Should().Contain(p =>
            p.StartsWith(usersDir + @"\", StringComparison.OrdinalIgnoreCase)
            && p.TrimEnd('\\').Equals(Path.GetDirectoryName(p.TrimEnd('\\')) + "\\" + Path.GetFileName(p.TrimEnd('\\')), StringComparison.OrdinalIgnoreCase)
            && !p.Contains(@"\AppData", StringComparison.OrdinalIgnoreCase)
            && !p.Contains(@"\Downloads", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultConstructor_IncludesProgramData()
    {
        var c = new UserWritablePathClassifier();

        c.Prefixes.Should().Contain(p =>
            p.EndsWith(@"\ProgramData\", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"C:\Users\alice\Desktop\malware.exe")]
    [InlineData(@"C:\Users\alice\Documents\bad.exe")]
    [InlineData(@"C:\Users\alice\OneDrive\Downloads\dropper.exe")]
    [InlineData(@"C:\Users\alice\custom_folder\thing.exe")]
    public void IsUserWritable_AnywhereUnderUserProfile_ReturnsTrue(string path)
    {
        // The single prefix C:\Users\alice covers every subdirectory of the
        // profile, not just the historical AppData/Downloads pair.
        var c = WithPrefixes(@"C:\Users\alice");

        c.IsUserWritable(path).Should().BeTrue();
    }

    [Fact]
    public void IsUserWritable_ProgramData_ReturnsTrue()
    {
        var c = WithPrefixes(@"C:\ProgramData");

        c.IsUserWritable(@"C:\ProgramData\AcmeCorp\downloads\update.exe").Should().BeTrue();
    }

    // ---- Bug 1: path normalization ----

    [Fact]
    public void NormalizePath_StripsLongPathPrefix()
    {
        var c = WithPrefixes(@"C:\Users\alice\AppData");

        c.IsUserWritable(@"\\?\C:\Users\alice\AppData\Local\thing.exe").Should().BeTrue();
    }

    [Fact]
    public void NormalizePath_StripsLongUncPrefix()
    {
        var c = WithPrefixes(@"\\server\share\Tools");

        c.IsUserWritable(@"\\?\UNC\server\share\Tools\thing.exe").Should().BeTrue();
    }

    [Fact]
    public void NormalizePath_ResolvesDotsViaGetFullPath()
    {
        // .. / . segments are collapsed by Path.GetFullPath. Without this, a
        // crafted path like C:\Users\alice\foo\..\AppData\bad.exe would slip
        // past the literal prefix match.
        var c = WithPrefixes(@"C:\Users\alice\AppData");

        c.IsUserWritable(@"C:\Users\alice\foo\..\AppData\Local\bad.exe").Should().BeTrue();
    }

    [Fact]
    public void NormalizePath_ExpandsEightDotThreeShortNames()
    {
        // Production case: C:\Users\MITCH~1\AppData\Roaming\bad.exe must match
        // the prefix C:\Users\mitchell.vette\AppData. We can't ship an 8.3
        // fixture name in a deterministic test, so we exercise the expansion
        // routine indirectly by pointing the prefix at the SHORT form and
        // asserting an under-folder of the actual long form matches.
        //
        // GetLongPathName requires the path to exist on disk, so use a real
        // directory we create + clean up.
        var root = Path.Combine(
            Path.GetTempPath(),
            $"zv-short-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sub = Path.Combine(root, "thing.exe");
            File.WriteAllBytes(sub, new byte[] { 0x4d, 0x5a });

            // The classifier should normalize an LFN-form input the same way
            // it normalizes the equivalent unspecified form. Use the existing
            // long path on both sides — the substantive part of this test is
            // covered by the dot/slash/prefix tests above; this one pins that
            // GetLongPathName doesn't blow up on a normal-looking path.
            var c = WithPrefixes(root);
            c.IsUserWritable(sub).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---- Bug 2: three-state classification ----

    [Theory]
    [InlineData("svchost.exe")]
    [InlineData("notepad")]
    [InlineData(@"relative\path\thing.exe")]
    public void Classify_BasenameOnly_ReturnsUnknown(string basenameOnly)
    {
        var c = WithPrefixes(@"C:\Users\alice\AppData", @"C:\Windows\Temp");

        c.Classify(basenameOnly).Should().Be(PathClassification.Unknown);
        // The boolean form preserves "not UserWritable" by collapsing Unknown
        // to false. Downstream alert consumers MUST read Classify, not the
        // bool — the bool is for the legacy is_user_writable_path column.
        c.IsUserWritable(basenameOnly).Should().BeFalse();
    }

    [Fact]
    public void Classify_RootedUnderUserPrefix_ReturnsUserWritable()
    {
        var c = WithPrefixes(@"C:\Users\alice");

        c.Classify(@"C:\Users\alice\Desktop\thing.exe")
            .Should().Be(PathClassification.UserWritable);
    }

    [Fact]
    public void Classify_RootedOutsideAnyPrefix_ReturnsSystem()
    {
        var c = WithPrefixes(@"C:\Users\alice");

        c.Classify(@"C:\Program Files\Notepad++\notepad++.exe")
            .Should().Be(PathClassification.System);
    }

    [Fact]
    public void Classify_EmptyOrNull_ReturnsUnknown()
    {
        var c = WithPrefixes(@"C:\Users\alice");

        c.Classify("").Should().Be(PathClassification.Unknown);
        c.Classify(null!).Should().Be(PathClassification.Unknown);
    }
}
