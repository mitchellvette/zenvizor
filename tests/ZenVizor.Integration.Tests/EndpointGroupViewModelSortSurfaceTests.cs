// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Core.Aggregation;
using ZenVizor.Ui.Views;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Pin the raw-byte sort surface on <see cref="EndpointGroupViewModel"/>.
/// The XAML Up / Down columns on the App Detail Connections grid bind
/// display to <c>UpText</c> / <c>DownText</c> but <c>SortMemberPath</c>
/// to <c>BytesUp</c> / <c>BytesDown</c>; without the raw <c>long</c>
/// properties on the view model, header-click sort falls back to a
/// lexicographic comparison of the formatted string ("300 KB" outranks
/// "200 MB" because '3' &gt; '2'). The properties are referenced by
/// name here so any future rewrite that drops them trips CI rather than
/// surfacing as a user-visible regression.
/// </summary>
public sealed class EndpointGroupViewModelSortSurfaceTests
{
    [Fact]
    public void BytesUp_And_BytesDown_Match_Source_Group()
    {
        var group = new EndpointGroup(
            Identity:          "example.test",
            ResolvedHost:      "example.test",
            Addresses:         new[] { "203.0.113.10" },
            RemoteClass:       "Wan",
            BytesUp:           300_000,
            BytesDown:         200_000_000,
            ConnectionCount:   1,
            DistinctPortCount: 1,
            FirstSeenUnixMs:   0,
            LastSeenUnixMs:    1,
            Ports:             Array.Empty<EndpointPortChild>());

        var vm = EndpointGroupViewModel.From(group);

        vm.BytesUp.Should().Be(300_000L);
        vm.BytesDown.Should().Be(200_000_000L);
    }

    [Fact]
    public void Raw_Bytes_Sort_Numerically_Where_Display_String_Would_Not()
    {
        // The bug this test firewalls: lexicographic sort of "300 KB" vs
        // "200 MB" puts the smaller value first. Sort by raw bytes inverts
        // that to the correct order. Asserting on the raw properties — the
        // surface SortMemberPath targets — proves the fix is in place.
        var smallByValueLargeByLeadingDigit = EndpointGroupViewModel.From(
            new EndpointGroup(
                Identity:          "small.test",
                ResolvedHost:      null,
                Addresses:         new[] { "203.0.113.1" },
                RemoteClass:       "Wan",
                BytesUp:           0,
                BytesDown:         300_000,
                ConnectionCount:   1,
                DistinctPortCount: 1,
                FirstSeenUnixMs:   0,
                LastSeenUnixMs:    1,
                Ports:             Array.Empty<EndpointPortChild>()));

        var largeByValueSmallByLeadingDigit = EndpointGroupViewModel.From(
            new EndpointGroup(
                Identity:          "large.test",
                ResolvedHost:      null,
                Addresses:         new[] { "203.0.113.2" },
                RemoteClass:       "Wan",
                BytesUp:           0,
                BytesDown:         200_000_000,
                ConnectionCount:   1,
                DistinctPortCount: 1,
                FirstSeenUnixMs:   0,
                LastSeenUnixMs:    1,
                Ports:             Array.Empty<EndpointPortChild>()));

        largeByValueSmallByLeadingDigit.BytesDown.Should().BeGreaterThan(
            smallByValueLargeByLeadingDigit.BytesDown);

        string.CompareOrdinal(
            largeByValueSmallByLeadingDigit.DownText,
            smallByValueLargeByLeadingDigit.DownText).Should().BeLessThan(0,
            "the formatted display string sorts the larger value FIRST under "
          + "lexicographic comparison — proves why SortMemberPath must point "
          + "at the raw long, not the formatted text");
    }
}
