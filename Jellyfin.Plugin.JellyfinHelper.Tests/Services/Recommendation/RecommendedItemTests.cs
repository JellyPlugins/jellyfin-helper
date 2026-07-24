using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

/// <summary>
///     Tests the setter guards on <see cref="RecommendedItem"/>.
///     The class exposes SEVEN collection properties (<see cref="RecommendedItem.Genres"/>,
///     <see cref="RecommendedItem.PeopleNames"/>, <see cref="RecommendedItem.Studios"/>,
///     <see cref="RecommendedItem.Tags"/>, <see cref="RecommendedItem.AudioLanguages"/>,
///     <see cref="RecommendedItem.SubtitleLanguages"/>, and <see cref="RecommendedItem.BoxSetIds"/>)
///     whose setters MUST coalesce <c>null</c> to an empty list. Without this coalescing:
///     <list type="bullet">
///         <item>Cache round-trips through <c>JsonSerializer</c> could set the field to <c>null</c>
///               when the persisted JSON omits or explicitly nulls the array.</item>
///         <item>Downstream callers (<c>TrainingDataBuilder</c>, <c>ScoringHelper</c>, etc.) iterate
///               these lists directly with <c>foreach</c> or <c>Contains</c> — a <c>null</c> would
///               throw <c>NullReferenceException</c> deep inside a scheduled task.</item>
///     </list>
///     <para>
///         Every collection setter therefore has TWO branches — "value was null" and
///         "value was non-null". Only the non-null branch is exercised by "happy path"
///         test suites elsewhere. These tests pin the null branch for each property so
///         a regression removing the <c>?? []</c> coalescing surfaces immediately.
///     </para>
/// </summary>
public sealed class RecommendedItemTests
{
    // ---------------- Genres ----------------

    [Fact]
    public void Genres_NullAssignment_CoalescedToEmpty()
    {
        var sut = new RecommendedItem { Genres = null! };
        Assert.NotNull(sut.Genres);
        Assert.Empty(sut.Genres);
    }

    [Fact]
    public void Genres_NonNullAssignment_PreservesReference()
    {
        string[] input = ["Action", "Drama"];
        var sut = new RecommendedItem { Genres = input };
        Assert.Same(input, sut.Genres);
    }

    // ---------------- PeopleNames ----------------

    [Fact]
    public void PeopleNames_NullAssignment_CoalescedToEmpty()
    {
        var sut = new RecommendedItem { PeopleNames = null! };
        Assert.NotNull(sut.PeopleNames);
        Assert.Empty(sut.PeopleNames);
    }

    [Fact]
    public void PeopleNames_NonNullAssignment_PreservesReference()
    {
        string[] input = ["Actor One", "Director X"];
        var sut = new RecommendedItem { PeopleNames = input };
        Assert.Same(input, sut.PeopleNames);
    }

    // ---------------- Studios ----------------

    [Fact]
    public void Studios_NullAssignment_CoalescedToEmpty()
    {
        var sut = new RecommendedItem { Studios = null! };
        Assert.NotNull(sut.Studios);
        Assert.Empty(sut.Studios);
    }

    [Fact]
    public void Studios_NonNullAssignment_PreservesReference()
    {
        string[] input = ["A24"];
        var sut = new RecommendedItem { Studios = input };
        Assert.Same(input, sut.Studios);
    }

    // ---------------- Tags ----------------

    [Fact]
    public void Tags_NullAssignment_CoalescedToEmpty()
    {
        var sut = new RecommendedItem { Tags = null! };
        Assert.NotNull(sut.Tags);
        Assert.Empty(sut.Tags);
    }

    [Fact]
    public void Tags_NonNullAssignment_PreservesReference()
    {
        string[] input = ["dark", "noir"];
        var sut = new RecommendedItem { Tags = input };
        Assert.Same(input, sut.Tags);
    }

    // ---------------- AudioLanguages ----------------

    [Fact]
    public void AudioLanguages_NullAssignment_CoalescedToEmpty()
    {
        var sut = new RecommendedItem { AudioLanguages = null! };
        Assert.NotNull(sut.AudioLanguages);
        Assert.Empty(sut.AudioLanguages);
    }

    [Fact]
    public void AudioLanguages_NonNullAssignment_PreservesReference()
    {
        string[] input = ["eng", "deu"];
        var sut = new RecommendedItem { AudioLanguages = input };
        Assert.Same(input, sut.AudioLanguages);
    }

    // ---------------- SubtitleLanguages ----------------

    [Fact]
    public void SubtitleLanguages_NullAssignment_CoalescedToEmpty()
    {
        var sut = new RecommendedItem { SubtitleLanguages = null! };
        Assert.NotNull(sut.SubtitleLanguages);
        Assert.Empty(sut.SubtitleLanguages);
    }

    [Fact]
    public void SubtitleLanguages_NonNullAssignment_PreservesReference()
    {
        string[] input = ["eng"];
        var sut = new RecommendedItem { SubtitleLanguages = input };
        Assert.Same(input, sut.SubtitleLanguages);
    }

    // ---------------- BoxSetIds ----------------

    [Fact]
    public void BoxSetIds_NullAssignment_CoalescedToEmpty()
    {
        var sut = new RecommendedItem { BoxSetIds = null! };
        Assert.NotNull(sut.BoxSetIds);
        Assert.Empty(sut.BoxSetIds);
    }

    [Fact]
    public void BoxSetIds_NonNullAssignment_PreservesReference()
    {
        Guid[] input = [Guid.NewGuid(), Guid.NewGuid()];
        var sut = new RecommendedItem { BoxSetIds = input };
        Assert.Same(input, sut.BoxSetIds);
    }

    // ---------------- Reassignment semantics ----------------

    [Fact]
    public void Collections_ReassignFromNonNullToNull_ReplacesWithEmpty()
    {
        // An earlier draft used init-only assignment and silently
        // dropped subsequent nulls. The current setter must actively replace the
        // backing field so a caller that re-nulls a previously populated collection
        // observes the coalesced empty list, not the stale non-null value.
        var sut = new RecommendedItem { Genres = ["Action"] };
        Assert.Single(sut.Genres);

        sut.Genres = null!;

        Assert.NotNull(sut.Genres);
        Assert.Empty(sut.Genres);
    }

    // ---------------- Defaults ----------------

    [Fact]
    public void Defaults_AllCollectionsAreEmptyNotNull()
    {
        // Every collection property must default to a NON-NULL empty list so that
        // fresh instances behave the same as post-setter instances w.r.t. null-safety.
        var sut = new RecommendedItem();

        Assert.NotNull(sut.Genres);
        Assert.Empty(sut.Genres);
        Assert.NotNull(sut.PeopleNames);
        Assert.Empty(sut.PeopleNames);
        Assert.NotNull(sut.Studios);
        Assert.Empty(sut.Studios);
        Assert.NotNull(sut.Tags);
        Assert.Empty(sut.Tags);
        Assert.NotNull(sut.AudioLanguages);
        Assert.Empty(sut.AudioLanguages);
        Assert.NotNull(sut.SubtitleLanguages);
        Assert.Empty(sut.SubtitleLanguages);
        Assert.NotNull(sut.BoxSetIds);
        Assert.Empty(sut.BoxSetIds);
    }

    [Fact]
    public void Defaults_AllScalarsHaveDocumentedDefaults()
    {
        var sut = new RecommendedItem();
        Assert.Equal(Guid.Empty, sut.ItemId);
        Assert.Equal(string.Empty, sut.Name);
        Assert.Equal(string.Empty, sut.ItemType);
        Assert.Equal(0.0, sut.Score);
        Assert.Equal(string.Empty, sut.Reason);
        Assert.Equal(string.Empty, sut.ReasonKey);
        Assert.Null(sut.RelatedItemName);
        Assert.Null(sut.Year);
        Assert.Null(sut.CommunityRating);
        Assert.Null(sut.CriticRating);
        Assert.Null(sut.PrimaryImageTag);
        Assert.Null(sut.OfficialRating);
        Assert.Null(sut.PremiereDate);
        Assert.Null(sut.DateCreated);
    }
}