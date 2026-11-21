using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

public sealed class MovieSequencePolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActualColdWarmLiveAndBatchSelectionSubstitutesTheUnseenPredecessorInTheSelectedSlot(bool warm, bool batch)
    {
        var first = Movie(9737, 14890, .1f);
        var second = Movie(8961, 14890, 10);
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        if (warm) profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Action"] });
        var harness = EngineTestFactory.Create();
        using var engine = harness.Engine;
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [second, first] : []);
        RecommendationResult Generate() => batch ? Assert.Single(engine.GetAllRecommendations(1, TestContext.Current.CancellationToken)) : engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!;
        var delivered = Assert.Single(Generate().Recommendations);
        Assert.Equal(first.Id, delivered.ItemId);
        Assert.Equal(first.CommunityRating, delivered.CommunityRating);
        if (warm) Assert.Equal(41, delivered.ScoringFeatures!.ToVector().Length);

        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = first.Id, ItemType = "Movie", TmdbId = 9737, Played = true, Genres = ["Action"] });
        Assert.Equal(second.Id, Assert.Single(Generate().Recommendations).ItemId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hidden")]
    [InlineData("started")]
    [InlineData("virtual")]
    [InlineData("restricted")]
    public void AnUnavailablePrerequisiteNeverOpensAHiddenCopyOrConsumesTheBackfillSlot(string boundary)
    {
        var first = Movie(9737, 14890, .1f);
        var second = Movie(8961, 14890, 10);
        var unrelated = Movie(999999, 0, .2f);
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), AccessibleItemIds = new HashSet<Guid> { first.Id, second.Id, unrelated.Id } };
        if (boundary == "hidden") profile.AccessibleItemIds = new HashSet<Guid> { second.Id, unrelated.Id };
        if (boundary == "started") profile.WatchedItems.Add(new WatchedItemInfo { ItemId = first.Id, ItemType = "Movie", TmdbId = 9737, PlaybackPositionTicks = 800, RuntimeTicks = 1000 });
        if (boundary == "virtual") first.IsVirtualItem = true;
        if (boundary == "restricted") { profile.MaxParentalRating = 12; first.InheritedParentalRatingValue = 18; }
        var harness = EngineTestFactory.Create(virtualItems: TestMockFactory.CreateVirtualItemDetector());
        using var engine = harness.Engine;
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? boundary == "missing" ? [second, unrelated] : [first, second, unrelated] : []);
        var result = engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!;
        Assert.Equal(unrelated.Id, Assert.Single(result.Recommendations).ItemId);
    }

    [Fact]
    public void DuplicateSubstitutionsUseOneSlotAndKeepThePredecessorsOwnScore()
    {
        var first = Movie(9737, 14890, 1);
        var second = Movie(8961, 14890, 10);
        var third = Movie(38700, 14890, 9);
        var other = Movie(999999, 0, 2);
        var eligible = new[] { first, second, third, other }.Select(movie => ((BaseItem)movie, (double)movie.CommunityRating!.Value / 10, "own reason", "own key", (string?)null)).ToList();
        var selected = MovieSequencePolicy.Apply([eligible[1], eligible[2]], eligible, null, 2, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { first.Id, other.Id }, selected.Select(row => row.Item.Id));
        Assert.Equal(.1, selected[0].Score);
        Assert.Equal(.2, selected[1].Score);
    }

    [Theory]
    [InlineData(657, 645)]
    [InlineData(8961, 0)]
    [InlineData(999999, 14890)]
    public void StandaloneUnknownAndUncorroboratedMembershipDoesNotInventADependency(int movie, int collection)
    {
        Assert.Equal(movie, MovieSequencePolicy.RequiredMovie(movie, collection, new HashSet<int>()));
    }

    [Fact]
    public void TvProgressNeverCompletesTheMoviePrerequisiteAndPermittedCopiesRemainUsable()
    {
        var first = Movie(9737, 14890, 1);
        var second = Movie(8961, 14890, 10);
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemType = "Series", TmdbId = 9737, Played = true }] };
        var eligible = new[] { first, second }.Select(movie => ((BaseItem)movie, .5, string.Empty, string.Empty, (string?)null)).ToList();
        Assert.Equal(first.Id, Assert.Single(MovieSequencePolicy.Apply([eligible[1]], eligible, profile, 1, TestContext.Current.CancellationToken)).Item.Id);
        profile.WatchedItems[0].ItemType = "Movie";
        Assert.Equal(second.Id, Assert.Single(MovieSequencePolicy.Apply([eligible[1]], eligible, profile, 1, TestContext.Current.CancellationToken)).Item.Id);
    }

    private static Movie Movie(int id, int collection, float rating) => new()
    {
        Id = Guid.NewGuid(), Name = "Provider movie " + id, Path = "/media/" + id + ".mkv", Genres = ["Action"],
        CommunityRating = rating, ProductionYear = 2020, InheritedParentalRatingValue = 6,
        ProviderIds = new() { ["Tmdb"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["TmdbCollection"] = collection.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    };
}
