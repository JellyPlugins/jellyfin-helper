using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Coverage for the live-library metadata pipeline (BuildLibraryItemMetadata /
///     AddLibraryItemMetadata, driven by TrainStrategy) and the GetEnsembleDiagnostics accessor on the
///     recommendation Engine.
/// </summary>
public sealed class EngineLibraryMetadataTests : IDisposable
{
    private readonly string _tempDir;

    public EngineLibraryMetadataTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), $"engine_libmeta_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (DirectoryNotFoundException)
        {
            // best-effort cleanup
        }
        catch (IOException)
        {
            // best-effort cleanup - file may be locked on CI
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup
        }
    }

    private static Movie MakeMovie(string name, string[]? studios = null, string[]? tags = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = $"/media/movies/{Guid.NewGuid():N}.mkv",
            ProductionYear = 2020,
            Genres = ["Action"],
            Studios = studios ?? [],
            Tags = tags ?? [],
            PremiereDate = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };

    private static void SetupLibrary(TestFixtures.EngineTestFactory.EngineHarness harness, IReadOnlyList<BaseItem> movies, IReadOnlyList<BaseItem> series)
    {
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(new List<BaseItem>(movies));
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem>(series));
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns([]);
    }

    private static IReadOnlyList<RecommendationResult> OneUserResult()
    {
        return
        [
            new RecommendationResult { UserId = Guid.NewGuid(), UserName = "Alice" }
        ];
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CollectionQueryFailure_PreservesPreviouslyCorroboratedIdsButConfirmedRemovalClearsThem(bool failure, bool linkedFailure)
    {
        var linked = new Mock<ILinkedChildrenService>();
        var harness = EngineTestFactory.Create(linkedChildren: linked.Object);
        var probe = MakeMovie("Previously corroborated");
        probe.ProviderIds["Tmdb"] = "123";
        probe.ProviderIds["TmdbCollection"] = "123";
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var roster = harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))));
        if (failure && !linkedFailure) roster.Throws(new InvalidOperationException("roster unavailable"));
        else roster.Returns([collection]);
        if (linkedFailure) linked.Setup(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual)).Throws(new InvalidOperationException("membership unavailable"));
        else linked.Setup(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual)).Returns([]);
        var metadata = harness.Engine.BuildTrainingLibraryMetadata([probe], []);
        Assert.Equal(!failure, metadata.TmdbCollectionIds!.ContainsKey(probe.Id));
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddDays(-2);
        var profiles = new Collection<UserWatchProfile> { new() { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbCollectionId = 123, Played = true, ItemType = "Movie", LastPlayedDate = at.AddDays(-1) }] } };
        var previous = new RecommendationResult { UserId = user, GeneratedAt = at, Recommendations = [new RecommendedItem { ItemId = probe.Id, TmdbId = 123, TmdbCollectionId = 123, ItemType = "Movie", Genres = ["Action"] }] };
        var examples = TrainingDataBuilder.BuildExamples([previous], profiles, null, null, null, metadata, null, TestContext.Current.CancellationToken).Examples;
        var recommendation = Assert.Single(examples, example => example.ObservationId == TrainingExample.CreateTitleObservationId(user, probe.Id, 123, false));
        Assert.Equal(failure ? 1 : 0, recommendation.Features.FranchiseAffinity);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void OrganicSeries_UseHistoricalDistinctSiblingCountsAndExcludeTheirOwnEpisodes(int siblings, bool membership)
    {
        var linked = new Mock<ILinkedChildrenService>();
        var harness = EngineTestFactory.Create(linkedChildren: linked.Object);
        var seriesId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var collection = new BoxSet { Id = Guid.NewGuid() };
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns(membership ? [collection] : []);
        linked.Setup(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual)).Returns([seriesId, siblingId, movieId]);
        var metadata = harness.Engine.BuildTrainingLibraryMetadata([new Movie { Id = movieId }], [new Series { Id = seriesId }, new Series { Id = siblingId }]);
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddDays(-1);
        var profile = new UserWatchProfile { UserId = user };
        for (var i = 0; i < 5; i++) profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = seriesId, Played = true, LastPlayedDate = at.AddMinutes(-i), Genres = ["Drama"] });
        if (siblings > 0)
        {
            for (var i = 0; i < 5; i++) profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = siblingId, Played = true, LastPlayedDate = at.AddDays(-1).AddMinutes(-i), Genres = ["Action"] });
        }

        if (siblings > 1) profile.WatchedItems.Add(new WatchedItemInfo { ItemId = movieId, ItemType = "Movie", Played = true, LastPlayedDate = at.AddDays(-1), Genres = ["Action"] });
        var examples = TrainingDataBuilder.BuildExamples([], new Collection<UserWatchProfile> { profile }, null, null, null, metadata, null, TestContext.Current.CancellationToken).Examples;
        var series = Assert.Single(examples, example => example.ObservationId == TrainingExample.CreateTitleObservationId(user, seriesId, 0, true));
        Assert.Equal(membership && siblings > 0 ? 0.3 + ((siblings - 1) * 0.2) : 0, series.Features.CollectionProgressionBoost, 12);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("library")]
    [InlineData("linked")]
    [InlineData("null")]
    public void RemovedMembershipsAndMetadata_ClearCachedValuesOnlyWhenConfirmed(string failure)
    {
        var linked = new Mock<ILinkedChildrenService>();
        var harness = EngineTestFactory.Create(linkedChildren: linked.Object);
        var watched = MakeMovie("Metadata removed");
        var probe = MakeMovie("Still in collection", ["OldStudio"], ["old tag"]);
        var collection = new BoxSet { Id = Guid.NewGuid() };
        var query = harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.BoxSet))));
        if (failure == "library") query.Throws(new InvalidOperationException("roster unavailable"));
        else if (failure == "null") query.Returns((IReadOnlyList<BaseItem>)null!);
        else query.Returns([collection]);
        if (failure == "linked") linked.Setup(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual)).Throws(new InvalidOperationException("membership unavailable"));
        else linked.Setup(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual)).Returns([probe.Id]);
        var metadata = harness.Engine.BuildTrainingLibraryMetadata([watched, probe], []);
        Assert.Empty(metadata.Studios[watched.Id]);
        Assert.Empty(metadata.Tags[watched.Id]);
        if (failure == "none") Assert.Empty(metadata.BoxSetIds[watched.Id]);
        else Assert.False(metadata.BoxSetIds.ContainsKey(watched.Id));

        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddDays(-2);
        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = watched.Id, ItemType = "Movie", Played = true, LastPlayedDate = at.AddDays(-1), Genres = ["Action"], Studios = ["OldStudio"], Tags = ["old tag"] }] }
        };
        var previous = new RecommendationResult { UserId = user, GeneratedAt = at, Recommendations =
        [
            new RecommendedItem { ItemId = watched.Id, ItemType = "Movie", Genres = ["Action"], Studios = ["OldStudio"], Tags = ["old tag"], BoxSetIds = [collection.Id] },
            new RecommendedItem { ItemId = probe.Id, ItemType = "Movie", Genres = ["Comedy", "Indie"], Studios = ["OldStudio"], Tags = ["old tag"], BoxSetIds = [collection.Id] }
        ] };
        var examples = TrainingDataBuilder.BuildExamples([previous], profiles, null, null, null, metadata, null, TestContext.Current.CancellationToken).Examples;
        var features = Assert.Single(examples, example => example.Features.GenreCount == 2).Features;
        Assert.False(features.StudioMatch);
        Assert.Equal(0, features.TagSimilarity);
        Assert.Equal(failure == "none" ? 0 : 0.3, features.CollectionProgressionBoost);
    }

    [Fact]
    public void TrainStrategy_QueriesLibraryForMovieAndSeriesMetadata()
    {
        // Driving TrainStrategy runs BuildLibraryItemMetadata, which loads Movies and Series from the library
        // so watched-item studios/tags resolve from the same source the serve path reads.
        var harness = EngineTestFactory.Create();
        SetupLibrary(
            harness,
            [MakeMovie("Studio Movie", studios: ["A24"], tags: ["cult"])],
            [new Series { Id = Guid.NewGuid(), Name = "HBO Show", Studios = ["HBO"], Tags = ["prestige"] }]);

        var trained = harness.Engine.TrainStrategy(OneUserResult(), incremental: false, CancellationToken.None);

        // Heuristic strategy (harness default) is not trainable, so trained==false, but the metadata build
        // must still have queried the library for both item types.
        Assert.False(trained);
        harness.LibraryManager.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)),
            Times.AtLeastOnce);
        harness.LibraryManager.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)),
            Times.AtLeastOnce);
    }

    [Fact]
    public void TrainStrategy_ItemsWithEmptyStudiosAndTags_DoNotThrow()
    {
        // Empty/whitespace metadata must be accepted and recorded as a known empty value.
        var harness = EngineTestFactory.Create();
        SetupLibrary(
            harness,
            [MakeMovie("Blank", studios: ["", "  "], tags: ["", " "])],
            []);

        var ex = Record.Exception(() => harness.Engine.TrainStrategy(OneUserResult(), incremental: false, CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public void GetEnsembleDiagnostics_NonEnsembleStrategy_ReturnsNull()
    {
        // The harness default is a HeuristicScoringStrategy, so the engine has no ensemble to report on.
        var harness = EngineTestFactory.Create();

        Assert.Null(harness.Engine.GetEnsembleDiagnostics());
    }

    [Fact]
    public void GetEnsembleDiagnostics_EnsembleStrategy_ReturnsCoherentSnapshot()
    {
        using var ensemble = new EnsembleScoringStrategy(Path.Join(_tempDir, "ml_weights.json"));
        var harness = EngineTestFactory.Create(ensemble);

        var diag = harness.Engine.GetEnsembleDiagnostics();

        Assert.NotNull(diag);
        Assert.InRange(diag!.Alpha, diag.AlphaMin, diag.AlphaMax);
        Assert.True(diag.TrainingExampleCount >= 0);
    }

    [Fact]
    public void TrainStrategy_EnsembleStrategy_ResolvesFeatureMeansFromLearnedSubStrategy()
    {
        // Covers the EnsembleScoringStrategy arm of the featureMeans switch in TrainingService: training an
        // ensemble-backed engine reads the learned sub-strategy's means to impute discovery examples.
        using var ensemble = new EnsembleScoringStrategy(Path.Join(_tempDir, "ens_weights.json"));
        var harness = EngineTestFactory.Create(ensemble);
        SetupLibrary(harness, [MakeMovie("M", studios: ["A24"])], []);

        var ex = Record.Exception(() => harness.Engine.TrainStrategy(OneUserResult(), incremental: false, CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public void TrainStrategy_LearnedStrategy_ResolvesFeatureMeansDirectly()
    {
        // Covers the LearnedScoringStrategy arm of the featureMeans switch (a bare learned strategy, not
        // wrapped in an ensemble).
        var learned = new LearnedScoringStrategy(Path.Join(_tempDir, "learned_weights.json"));
        var harness = EngineTestFactory.Create(learned);
        SetupLibrary(harness, [MakeMovie("M", studios: ["A24"])], []);

        var ex = Record.Exception(() => harness.Engine.TrainStrategy(OneUserResult(), incremental: false, CancellationToken.None));

        Assert.Null(ex);
    }
}
