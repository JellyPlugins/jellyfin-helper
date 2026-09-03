using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
        _tempDir = Path.Combine(Path.GetTempPath(), $"engine_libmeta_{Guid.NewGuid():N}");
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
        // AddLibraryItemMetadata must skip empty/whitespace studios and tags without adding a map entry.
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
