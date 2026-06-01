using FluentAssertions;
using TitaniRun.Capture;

namespace TitaniRun.Core.Tests;

/// <summary>
/// Regression for the Phase 1 bug where EtwCaptureSource crashed on the first
/// observation because TraceEvent emits DateTime with Kind=Local, but the
/// conversion used new DateTimeOffset(dt, TimeSpan.Zero) which requires Kind=Utc.
/// </summary>
public sealed class EtwTimestampConversionTests
{
    [Fact]
    public void ToUnixTimeMs_AcceptsUtcKind()
    {
        var utc = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var expected = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

        EtwCaptureSource.ToUnixTimeMs(utc).Should().Be(expected);
    }

    [Fact]
    public void ToUnixTimeMs_AcceptsLocalKind()
    {
        // The TraceEvent path always hits this — Kind=Local.
        var local = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Local);
        var expected = new DateTimeOffset(local.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();

        EtwCaptureSource.ToUnixTimeMs(local).Should().Be(expected);
    }

    [Fact]
    public void ToUnixTimeMs_AcceptsUnspecifiedKind()
    {
        var unspecified = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Unspecified);

        var act = () => EtwCaptureSource.ToUnixTimeMs(unspecified);
        act.Should().NotThrow();
    }
}
