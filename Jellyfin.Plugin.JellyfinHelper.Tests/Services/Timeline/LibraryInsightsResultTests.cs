using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

/// <summary>
///     Tests the null-coalescing setters on <see cref="LibraryInsightsResult"/>.
///     Three collection properties (<see cref="LibraryInsightsResult.Largest"/>,
///     <see cref="LibraryInsightsResult.Recent"/>, <see cref="LibraryInsightsResult.LibrarySizes"/>)
///     each guard against <c>null</c> assignment by falling back to an empty collection.
///     <para>
///         Motivation: <see cref="LibraryInsightsResult"/> is serialised into JSON and cached on
///         disk. A cache file that predates a schema addition (or was hand-edited) can produce
///         <c>null</c> for any of these fields during deserialisation. Any consumer that iterates
///         with <c>foreach</c> or calls <c>.Count</c>/<c>.Any()</c> would then throw NRE. The
///         guards keep the failure mode benign; these tests pin both branches of each guard.
///     </para>
/// </summary>
public sealed class LibraryInsightsResultTests
{
    [Fact]
    public void Largest_NullAssignment_CoalescedToEmpty()
    {
        var sut = new LibraryInsightsResult { Largest = null! };
        Assert.NotNull(sut.Largest);
        Assert.Empty(sut.Largest);
    }

    [Fact]
    public void Largest_NonNullAssignment_PreservesReference()
    {
        LibraryInsightEntry[] input =
        [
            new LibraryInsightEntry { Name = "big", Size = 42 }
        ];
        var sut = new LibraryInsightsResult { Largest = input };
        Assert.Same(input, sut.Largest);
    }

    [Fact]
    public void Recent_NullAssignment_CoalescedToEmpty()
    {
        var sut = new LibraryInsightsResult { Recent = null! };
        Assert.NotNull(sut.Recent);
        Assert.Empty(sut.Recent);
    }

    [Fact]
    public void Recent_NonNullAssignment_PreservesReference()
    {
        LibraryInsightEntry[] input =
        [
            new LibraryInsightEntry { Name = "new", Size = 7 }
        ];
        var sut = new LibraryInsightsResult { Recent = input };
        Assert.Same(input, sut.Recent);
    }

    [Fact]
    public void LibrarySizes_NullAssignment_CoalescedToEmpty()
    {
        var sut = new LibraryInsightsResult { LibrarySizes = null! };
        Assert.NotNull(sut.LibrarySizes);
        Assert.Empty(sut.LibrarySizes);
    }

    [Fact]
    public void LibrarySizes_NonNullAssignment_PreservesReference()
    {
        var input = new Dictionary<string, long> { ["Movies"] = 1024 };
        var sut = new LibraryInsightsResult { LibrarySizes = input };
        Assert.Same(input, sut.LibrarySizes);
    }

    [Fact]
    public void Defaults_AllCollectionsAreEmptyNotNull()
    {
        // Freshly constructed results must be safe to enumerate immediately.
        var sut = new LibraryInsightsResult();
        Assert.NotNull(sut.Largest);
        Assert.Empty(sut.Largest);
        Assert.NotNull(sut.Recent);
        Assert.Empty(sut.Recent);
        Assert.NotNull(sut.LibrarySizes);
        Assert.Empty(sut.LibrarySizes);
        Assert.Equal(0L, sut.LargestTotalSize);
        Assert.Equal(0, sut.RecentTotalCount);
        Assert.Equal(default(DateTime), sut.ComputedAtUtc);
    }

    [Fact]
    public void Reassignment_FromNonNullToNull_ReplacesWithEmpty()
    {
        // Regression guard: the setter must actively replace the backing field on every
        // assignment. A "stale-non-null" bug would leak the previous list to callers who
        // expected the empty coalesced result after clearing.
        var sut = new LibraryInsightsResult
        {
            Largest = [new LibraryInsightEntry { Name = "old", Size = 1 }]
        };
        Assert.Single(sut.Largest);

        sut.Largest = null!;

        Assert.Empty(sut.Largest);
    }
}