using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

/// <summary>
///     Tests the setter guards on RecommendedItem. The class exposes SEVEN collection properties (Genres, PeopleNames, Studios, Tags, AudioLanguages, SubtitleLanguages, and BoxSetIds) whose setters MUST coalesce null to an empty list.
/// </summary>
public sealed class RecommendedItemTests
{
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

    [Fact]
    public void Collections_ReassignFromNonNullToNull_ReplacesWithEmpty()
    {
        // An earlier draft used init-only assignment and silently dropped subsequent nulls.
        var sut = new RecommendedItem { Genres = ["Action"] };
        Assert.Single(sut.Genres);

        sut.Genres = null!;

        Assert.NotNull(sut.Genres);
        Assert.Empty(sut.Genres);
    }

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