using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the library-wide genre/studio IDF (inverse document frequency) rarity table the recommendation Engine builds in-memory from the allowed Movie/Series items.
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

    [Fact]
    public void GetAllRecommendations_GenreCountsPresent_BuildsAndNormalizesIdfTable()
    {
        // Wire many movies carrying a ubiquitous genre ("Action") plus one movie with a rare genre
        // ("Neo-Noir") so the in-memory document-frequency accumulate, add-one smoothing, and [0,1]
        // normalization path runs over a real term population, then run a warm batch so the table is
        // consulted. The IDF numbers are internal, so the assertion is the observable one: the batch
        // completes over the wired candidates and produces at least one recommendation.
        var harness = EngineTestFactory.Create();

        var candidates = new List<BaseItem>
        {
            MakeMovie("Ubiquitous 1", ["Action"]),
            MakeMovie("Ubiquitous 2", ["Action"]),
            MakeMovie("Ubiquitous 3", ["Action"]),
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

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        Assert.NotNull(results);
        var userResult = results.FirstOrDefault(r => r.UserId == userId);
        Assert.NotNull(userResult);
        Assert.NotEmpty(userResult!.Recommendations);
    }
}
