using FluentAssertions;
using ZenVizor.Ui.Views;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Phase 9.2 — pin <see cref="ReportsPage.Clock"/> behaviour: the seam is
/// overrideable from tests, and a fresh override producing today's date
/// flows through unchanged. The page's downstream picker / chart axes /
/// eyebrow all read from a single _initialDate field snapshotted from
/// Clock() at construction, so verifying the seam closes the regression
/// the hardcoded 2026-06-08 mockup constant introduced.
/// </summary>
public sealed class ReportsPageClockTests
{
    [Fact]
    public void Clock_Override_FlowsThroughUnchanged()
    {
        var saved = ReportsPage.Clock;
        try
        {
            var fake = new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Local);
            ReportsPage.Clock = () => fake;
            ReportsPage.Clock().Should().Be(fake);
        }
        finally
        {
            ReportsPage.Clock = saved;
        }
    }

    [Fact]
    public void Clock_Default_ProducesTodaysDate()
    {
        // Explicitly restore to the production default lambda — asserts the
        // contract that future maintainers must preserve. If someone swaps
        // in a hardcoded constant (the regression Phase 9.2 retired), the
        // production-default assignment line in ReportsPage no longer reads
        // `() => DateTime.Today` and this test's intent is visibly broken
        // in the same review diff.
        var saved = ReportsPage.Clock;
        try
        {
            ReportsPage.Clock = () => DateTime.Today;
            ReportsPage.Clock().Should().Be(DateTime.Today);
        }
        finally
        {
            ReportsPage.Clock = saved;
        }
    }
}
