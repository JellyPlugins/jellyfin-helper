using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Only confirmed, ordered, unfinished movie collections supply continuation confidence.</summary>
public sealed class CollectionContinuationCatalogTests
{
    [Fact]
    public void ThreeCompletedPartsSupportTheFourthNotAnArbitraryLaterSequel()
    {
        var movies = Movies();
        var catalog = Catalog(movies);
        var profile = Profile(movies.Take(3));
        var next = catalog.BuildSupport(profile);
        Assert.Equal(.75, Assert.Single(next).Value);
        Assert.Equal(movies[3].Id, Assert.Single(next).Key);
        Assert.DoesNotContain(movies[4].Id, next.Keys);
        Assert.Empty(catalog.BuildSupport(new UserWatchProfile()));
        Assert.Empty(catalog.BuildSupport(Profile(movies)));
    }

    [Fact]
    public void MissingPartialAndFavoriteOnlyGapsDoNotAdvanceToLaterParts()
    {
        var movies = Movies();
        var catalog = Catalog(movies);
        var profile = Profile([movies[0], movies[2]]);
        Assert.Equal(movies[1].Id, Assert.Single(catalog.BuildSupport(profile)).Key);
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = movies[1].Id, ItemType = "Movie", PlayCount = 1, PlaybackPositionTicks = 100, IsFavorite = true });
        Assert.Equal(movies[1].Id, Assert.Single(catalog.BuildSupport(profile)).Key);
        Assert.DoesNotContain(movies[3].Id, catalog.BuildSupport(profile).Keys);
        movies[1].Path = string.Empty;
        Assert.Empty(Catalog(movies).BuildSupport(profile));
    }

    [Fact]
    public void UnknownAmbiguousUnconfirmedOrUnplayableSequencesStayNeutral()
    {
        var movies = Movies();
        var profile = Profile(movies.Take(3));
        Assert.Empty(Catalog(movies, complete: false).BuildSupport(profile));
        var memberships = movies.ToDictionary(movie => movie.Id, _ => new List<Guid> { Guid.NewGuid() });
        Assert.Empty(CollectionContinuationCatalog.Create(movies, memberships, new HashSet<Guid>(), true).BuildSupport(profile));
        movies[3].Path = string.Empty;
        Assert.Empty(Catalog(movies).BuildSupport(profile));
        movies[3].Path = "/media/part-four.mkv";
        movies[3].PremiereDate = null;
        movies[3].ProductionYear = null;
        Assert.Empty(Catalog(movies).BuildSupport(profile));
        movies[3].ProductionYear = movies[2].ProductionYear;
        movies[2].PremiereDate = null;
        Assert.Empty(Catalog(movies).BuildSupport(profile));
    }

    [Fact]
    public void TvIdentitiesExcludedMoviesAndDislikedProgressCannotManufactureMovieContinuation()
    {
        var movies = Movies();
        var catalog = Catalog(movies);
        var tv = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = movies[0].Id, TmdbId = 100, SeriesTmdbId = 100, ItemType = "Episode", Played = true }] };
        Assert.Empty(catalog.BuildSupport(tv));
        var profile = Profile(movies.Take(3));
        Assert.Empty(Catalog(movies.Skip(3).ToArray()).BuildSupport(profile));
        foreach (var row in profile.WatchedItems) row.UserRating = 2;
        Assert.Empty(catalog.BuildSupport(profile));
    }

    [Fact]
    public void MetadataIsDetachedButCompletedUserOutcomesAreAlwaysRecomputed()
    {
        var movies = Movies();
        var catalog = Catalog(movies);
        var profile = Profile(movies.Take(3));
        movies[3].Path = string.Empty;
        movies[3].PremiereDate = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(movies[3].Id, Assert.Single(catalog.BuildSupport(profile)).Key);
        profile.WatchedItems.Clear();
        Assert.Empty(catalog.BuildSupport(profile));
    }

    [Fact]
    public void DuplicateLibraryCopiesCannotAmplifyProgressAndConflictingDatesFailNeutral()
    {
        var movies = Movies();
        var duplicate = new Movie { Id = Guid.NewGuid(), Name = "Another copy", Path = "/media/copy.mkv", ProviderIds = new() { ["Tmdb"] = "100" }, PremiereDate = movies[0].PremiereDate, ProductionYear = movies[0].ProductionYear };
        var profile = Profile(movies.Take(3));
        profile.WatchedItems.Add(Profile([duplicate]).WatchedItems[0]);
        Assert.Equal(.75, Assert.Single(Catalog([.. movies, duplicate]).BuildSupport(profile)).Value);
        duplicate.PremiereDate = movies[4].PremiereDate;
        Assert.Empty(Catalog([.. movies, duplicate]).BuildSupport(profile));
    }

    [Fact]
    public void PreparedNextIdentityRetainsEveryPlayableCopyAndReevaluatesEachHistoricalProfile()
    {
        var movies = Movies();
        var copy = new Movie { Id = Guid.NewGuid(), Path = "/media/next-copy.mkv", ProviderIds = new() { ["Tmdb"] = "103" }, PremiereDate = movies[3].PremiereDate };
        var placeholder = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "103" }, PremiereDate = movies[3].PremiereDate };
        var catalog = Catalog([.. movies, copy, placeholder]);
        var historical = Profile(movies.Take(3));
        var first = catalog.BuildSupport(historical);
        Assert.Equal(2, first.Count);
        Assert.Equal(.75, first[movies[3].Id]);
        Assert.Equal(.75, first[copy.Id]);
        Assert.DoesNotContain(placeholder.Id, first.Keys);

        copy.Path = string.Empty;
        movies[3].PremiereDate = movies[0].PremiereDate;
        var later = catalog.BuildSupport(Profile(movies.Take(4)));
        Assert.Equal(movies[4].Id, Assert.Single(later).Key);
        Assert.Equal(.8, Assert.Single(later).Value);
        var again = catalog.BuildSupport(historical);
        Assert.Equal(first.OrderBy(pair => pair.Key), again.OrderBy(pair => pair.Key));
        Assert.Empty(catalog.BuildSupport(new UserWatchProfile()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullServingPipelinePrefersTheNextConfirmedPartWithoutChangingTheOldProgressionCoordinate(bool batch)
    {
        var movies = Movies();
        var box = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "777" }, LinkedChildren = movies.Select(movie => new LinkedChild { ItemId = movie.Id, Type = LinkedChildType.Manual }).ToArray() };
        var profile = Profile(movies.Take(3));
        var harness = EngineTestFactory.Create();
        using var lifetime = harness.Engine;
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true) return movies;
            if (query.IncludeItemTypes?.Contains(BaseItemKind.BoxSet) == true) return [box];
            return [];
        });
        var result = batch
            ? Assert.Single(harness.Engine.GetAllRecommendations(2, TestContext.Current.CancellationToken))
            : harness.Engine.GetRecommendations(profile.UserId, 2, TestContext.Current.CancellationToken)!;
        Assert.Equal(movies[3].Id, result.Recommendations[0].ItemId);
        var next = result.Recommendations[0].ScoringFeatures!;
        var later = result.Recommendations[1].ScoringFeatures!;
        Assert.Equal(.75, next.CollectionContinuationSupport);
        Assert.Null(later.CollectionContinuationSupport);
        Assert.Equal(next.CollectionProgressionBoost, later.CollectionProgressionBoost);
        Assert.True(result.Recommendations[0].Score > result.Recommendations[1].Score);
        Assert.All(result.Recommendations, item => Assert.Equal(41, item.ScoringFeatures!.ToVector().Length));
        Assert.Equal(.75, Assert.Single(result.RankingDiagnostics!.Entries, entry => entry.ItemId == movies[3].Id).CollectionContinuationSupport);
    }

    [Fact]
    public void OrganicTrainingUsesOnlyTheHistoricalSelfExcludedCompletedPrefix()
    {
        var movies = Movies();
        var profile = Profile(movies.Take(4));
        var box = Guid.NewGuid();
        var memberships = movies.ToDictionary(movie => movie.Id, _ => new List<Guid> { box });
        var metadata = new LibraryItemMetadata(
            new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<string>>(),
            memberships.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Guid>)pair.Value),
            movies.ToDictionary(movie => movie.Id, movie => int.Parse(movie.ProviderIds["Tmdb"], CultureInfo.InvariantCulture)))
        {
            Continuation = CollectionContinuationCatalog.Create(movies, memberships, new HashSet<Guid> { box }, true)
        };
        var built = TrainingDataBuilder.BuildExamples([], [profile], null, null, null, metadata, null, TestContext.Current.CancellationToken);
        var id = TrainingExample.CreateTitleObservationId(profile.UserId, movies[3].Id, 103, false);
        var fourth = Assert.Single(built.Examples, example => example.ObservationId == id);
        Assert.Equal(.75, fourth.Features.CollectionContinuationSupport);
        Assert.Equal(profile.WatchedItems[3].LastPlayedDate, fourth.GeneratedAtUtc);
        Assert.True(fourth.IsObserved);
        Assert.DoesNotContain(built.Examples, example => example.ObservationId == TrainingExample.CreateTitleObservationId(profile.UserId, movies[4].Id, 104, false));
    }

    private static CollectionContinuationCatalog Catalog(Movie[] movies, bool complete = true)
    {
        var collection = Guid.NewGuid();
        return CollectionContinuationCatalog.Create(movies, movies.ToDictionary(movie => movie.Id, _ => new List<Guid> { collection }), new HashSet<Guid> { collection }, complete);
    }

    private static Movie[] Movies() => Enumerable.Range(0, 5).Select(index => new Movie
    {
        Id = Guid.NewGuid(), Name = "Part " + (index + 1), Path = "/media/part-" + index + ".mkv", Genres = ["Action"],
        CommunityRating = 8, ProductionYear = 2000 + index, PremiereDate = new DateTime(2000 + index, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        ProviderIds = new() { ["Tmdb"] = (100 + index).ToString(System.Globalization.CultureInfo.InvariantCulture) }
    }).ToArray();

    private static UserWatchProfile Profile(IEnumerable<Movie> movies) => new()
    {
        UserId = Guid.NewGuid(),
        WatchedItems = [.. movies.Select((movie, index) => new WatchedItemInfo
        {
            ItemId = movie.Id, TmdbId = int.Parse(movie.ProviderIds["Tmdb"], CultureInfo.InvariantCulture), ItemType = "Movie", Played = true, PlayCount = 1,
            Genres = ["Action"], LastPlayedDate = new DateTime(2026, 1, index + 1, 0, 0, 0, DateTimeKind.Utc), Year = movie.ProductionYear
        })]
    };
}
