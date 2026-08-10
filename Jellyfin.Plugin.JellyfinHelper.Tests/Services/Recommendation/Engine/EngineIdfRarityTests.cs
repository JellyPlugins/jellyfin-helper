using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the library-wide genre/studio IDF (inverse document frequency) rarity table
///     the recommendation
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>
///     builds from <see cref="IItemRepository.GetGenres"/> / <see cref="IItemRepository.GetStudios"/>.
///     <para>
///         The default harness returns empty genre/studio counts, which short-circuits
///         <c>BuildGenreStudioIdfTable</c> before it ever accumulates or normalizes. These tests
///         populate the repository with real per-term item counts so the accumulate loop, the
///         add-one-smoothed IDF computation, and the [0,1] max-normalization all execute - the
///         path that turns raw term frequencies into the <c>GenreStudioIdfPrior</c> scoring signal.
///     </para>
///     <para>
///         BUG SURFACE: a regression in the smoothing or normalization (e.g. dividing by the wrong
///         max, or a log-domain error on a zero document frequency) would either crash the batch or
///         silently flatten the rarity prior to a constant - erasing the "rare genres are more
///         informative" signal without any visible failure.
///     </para>
/// </summary>
public sealed class EngineIdfRarityTests
{
    private static Movie MakeMovie(string name, string[] genres, string[]? studios = null)
    {
        return new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = $"/media/movies/{Guid.NewGuid():N}.mkv",
            ProductionYear = 2020,
            Genres = genres,
            Studios = studios ?? [],
            CommunityRating = 7.0f,
            PremiereDate = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
    }

    private static (BaseItem, ItemCounts) Term(string name, int itemCount)
        => (new Movie { Id = Guid.NewGuid(), Name = name }, new ItemCounts { ItemCount = itemCount });

    [Fact]
    public void GetAllRecommendations_GenreCountsPresent_BuildsAndNormalizesIdfTable()
    {
        // Populate GetGenres with a ubiquitous term (high count) and a rare term (count 1) so the
        // accumulate + add-one-smoothing + [0,1] normalization path actually runs, then feed
        // candidates carrying those genres through a warm batch so the table is consulted.
        var harness = EngineTestFactory.Create();

        harness.ItemRepository
            .Setup(r => r.GetGenres(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<(BaseItem, ItemCounts)>(new List<(BaseItem, ItemCounts)>
            {
                Term("Action", 500),   // ubiquitous → low IDF
                Term("Neo-Noir", 1)     // rare → high IDF
            }));

        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "warm",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = watchedId,
                    Name = "Watched",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1,
                    Genres = new List<string> { "Action" }
                }
            }
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        var candidates = new List<BaseItem>
        {
            MakeMovie("Ubiquitous", ["Action"]),
            MakeMovie("Rare", ["Neo-Noir"])
        };
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(candidates);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns([]);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns([]);

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        // The batch must complete and the populated genre facet must have been consulted to build
        // the rarity table (the empty-default path would never enter the accumulate/normalize loop).
        Assert.NotNull(results);
        harness.ItemRepository.Verify(r => r.GetGenres(It.IsAny<InternalItemsQuery>()), Times.AtLeastOnce);
        harness.ItemRepository.Verify(r => r.GetStudios(It.IsAny<InternalItemsQuery>()), Times.AtLeastOnce);
    }

    [Fact]
    public void GetAllRecommendations_IdfGenreQueryThrows_BatchCompletesAndLogsNeutralWarning()
    {
        // BuildGenreStudioIdfTable must treat a repository failure as non-fatal: catch it, log a
        // warning that the GenreStudioIdfPrior will be neutral, and return an empty table so the
        // batch still finishes. A regression that let the exception escape would abort the whole run.
        var harness = EngineTestFactory.Create();

        harness.ItemRepository
            .Setup(r => r.GetGenres(It.IsAny<InternalItemsQuery>()))
            .Throws(new InvalidOperationException("genre facet query failed"));

        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "warm",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = watchedId,
                    Name = "Watched",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1,
                    Genres = new List<string> { "Action" }
                }
            }
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        var candidates = new List<BaseItem> { MakeMovie("Cand", ["Action"]) };
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(candidates);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns([]);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns([]);

        // The batch must NOT propagate the failure.
        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        Assert.NotNull(results);
        harness.PluginLog.Verify(
            p => p.LogWarning(
                It.IsAny<string>(),
                It.Is<string>(msg => msg.Contains("GenreStudioIdfPrior will be neutral")),
                It.IsAny<Exception>(),
                It.IsAny<Microsoft.Extensions.Logging.ILogger>()),
            Times.AtLeastOnce);
    }
}
