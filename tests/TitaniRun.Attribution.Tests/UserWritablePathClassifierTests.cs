using FluentAssertions;
using TitaniRun.Attribution.Paths;

namespace TitaniRun.Attribution.Tests;

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
}
