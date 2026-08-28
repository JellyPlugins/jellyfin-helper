using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
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
///     Tests for Engine.BuildCommunityPopularityMap - the shared cold-start community-popularity computation used by both the batch path and the live cold-start path.
/// </summary>
public sealed class EngineCommunityPopularityTests
{
    // Two-user gate

    [Fact]
    public void BuildCommunityPopularityMap_EmptyDictionary_ReturnsNull()
    {
        // No users at all: obviously no community signal to compute.
        var input = new Dictionary<Guid, HashSet<Guid>>();
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_SingleUserWithNoHistory_ReturnsNull()
    {
        // One user in the map but they have no watch data - no community signal.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = []
        };
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_SingleUserWithHistory_ReturnsNull()
    {
        // BUG GUARD: one user with real watch data must NOT be treated as "the community".
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]
        };
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_TwoUsers_OneEmpty_ReturnsNull()
    {
        // The gate requires TWO users with actual watch history - one active + one empty
        // still counts as "essentially a single-user deployment" from a community-signal POV.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid()],
            [Guid.NewGuid()] = [] // empty profile
        };
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_TwoUsersWithHistory_ReturnsMap()
    {
        // The minimum viable case: two active users unlock the community signal.
        var item = Guid.NewGuid();
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [item],
            [Guid.NewGuid()] = [item]
        };
        var result = Invoke(input);

        Assert.NotNull(result);
        Assert.Equal(2, result![item]);
    }

    // Counting semantics

    [Fact]
    public void BuildCommunityPopularityMap_ItemCountsReflectPerUserWatches()
    {
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var itemC = Guid.NewGuid();

        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [itemA, itemB],       // watched A + B
            [Guid.NewGuid()] = [itemA, itemC],       // watched A + C
            [Guid.NewGuid()] = [itemA]               // watched A only
        };

        var result = Invoke(input);

        Assert.NotNull(result);
        Assert.Equal(3, result![itemA]);   // seen by 3 users
        Assert.Equal(1, result[itemB]);    // seen by 1 user
        Assert.Equal(1, result[itemC]);    // seen by 1 user
    }

    [Fact]
    public void BuildCommunityPopularityMap_UsersWithDisjointHistories_YieldsSingleCounts()
    {
        // Users with entirely disjoint watch lists -> every item has count 1. BUG GUARD: if the counting logic ever gets an off-by-one that doubles counts across users, this test detects it via the strict equality assertion.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid()],
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid()]
        };

        var result = Invoke(input);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Count); // 4 distinct items
        Assert.All(result.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void BuildCommunityPopularityMap_ThreeEmptyUsers_ReturnsNull()
    {
        // The gate iterates until it finds >= 2 users with non-empty sets.
        // 3 empty users must not accidentally unlock the map because .Count == 3 > 1.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = []
        };

        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_ExactlyTwoActiveUsersAmongMany_UnlocksMap()
    {
        // Mixed deployment: many users but only two with actual history. The gate must
        // count users with history rather than dictionary size.
        var item = Guid.NewGuid();
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = [item],
            [Guid.NewGuid()] = [item],
            [Guid.NewGuid()] = []
        };

        var result = Invoke(input);
        Assert.NotNull(result);
        Assert.Equal(2, result![item]);
    }

    [Fact]
    public void BuildCommunityPopularityMap_SameItemAcrossManyUsers_CountsAccurately()
    {
        // 10 users all watched the same 3 items. Every item must show count 10.
        // Verifies the outer/inner-loop composition doesn't accidentally short-circuit.
        var items = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var input = new Dictionary<Guid, HashSet<Guid>>();
        for (var i = 0; i < 10; i++)
        {
            input[Guid.NewGuid()] = new HashSet<Guid>(items);
        }

        var result = Invoke(input);

        Assert.NotNull(result);
        foreach (var item in items)
        {
            Assert.Equal(10, result![item]);
        }
    }

    // Live cold-start path: community-popularity build + computed-flag reuse

    private static Movie MakeMovie(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = $"/media/movies/{Guid.NewGuid():N}.mkv",
            ProductionYear = 2020,
            Genres = ["Action"],
            CommunityRating = 7.0f,
            PremiereDate = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };

    private static void WireMovies(EngineTestFactory.EngineHarness harness, List<BaseItem> movies)
    {
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(movies);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns([]);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns([]);
    }

    [Fact]
    public void GetRecommendations_ColdStartUser_LiveSnapshot_CrowdFavoriteOutranksUnseen()
    {
        // On the LIVE cold-start path, GetOrRefreshLiveSnapshot publishes a snapshot with CommunityPopularityComputed=false, so GetOrBuildCommunityPopularity must run BuildCommunityPopularityForColdStart.
        var harness = EngineTestFactory.Create();

        var coldUser = Guid.NewGuid();
        var warmA = Guid.NewGuid();
        var warmB = Guid.NewGuid();

        var crowdFavorite = MakeMovie("Crowd Favorite");
        var unseenByCrowd = MakeMovie("Unseen");
        WireMovies(harness, [crowdFavorite, unseenByCrowd]);

        UserWatchProfile MakeCrowdMember(Guid id, string name) => new()
        {
            UserId = id,
            UserName = name,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = crowdFavorite.Id,
                    Name = "Crowd Favorite",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1
                }
            }
        };

        var coldProfile = new UserWatchProfile { UserId = coldUser, UserName = "cold", WatchedItems = [] };
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(coldUser)).Returns(coldProfile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>
            {
                coldProfile,
                MakeCrowdMember(warmA, "warmA"),
                MakeCrowdMember(warmB, "warmB")
            });

        var result = harness.Engine.GetRecommendations(coldUser, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("strategyColdStart", result!.ScoringStrategyKey);

        var favoriteRec = result.Recommendations.FirstOrDefault(i => i.ItemId == crowdFavorite.Id);
        var unseenRec = result.Recommendations.FirstOrDefault(i => i.ItemId == unseenByCrowd.Id);
        Assert.NotNull(favoriteRec);
        Assert.NotNull(unseenRec);

        Assert.True(
            favoriteRec!.Score > unseenRec!.Score,
            $"Live cold-start community-watched candidate (score={favoriteRec.Score}) must outrank the " +
            $"crowd-unseen candidate (score={unseenRec.Score}) under the 40/30/30 blend.");
    }

    [Fact]
    public void GetRecommendations_ColdStartAfterBatch_ReusesComputedCommunityMap_WithoutRecompute()
    {
        // GetAllRecommendations publishes a snapshot with CommunityPopularityComputed=true and a non-null map.
        var harness = EngineTestFactory.Create();

        var coldUser = Guid.NewGuid();
        var warmA = Guid.NewGuid();
        var warmB = Guid.NewGuid();

        var crowdFavorite = MakeMovie("Crowd Favorite");
        var unseenByCrowd = MakeMovie("Unseen");
        WireMovies(harness, [crowdFavorite, unseenByCrowd]);

        UserWatchProfile MakeCrowdMember(Guid id, string name) => new()
        {
            UserId = id,
            UserName = name,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = crowdFavorite.Id,
                    Name = "Crowd Favorite",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1
                }
            }
        };

        var coldProfile = new UserWatchProfile { UserId = coldUser, UserName = "cold", WatchedItems = [] };
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(coldUser)).Returns(coldProfile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>
            {
                coldProfile,
                MakeCrowdMember(warmA, "warmA"),
                MakeCrowdMember(warmB, "warmB")
            });

        // Batch first: publishes a snapshot whose CommunityPopularity map is already computed.
        var batchResults = harness.Engine.GetAllRecommendations(10, CancellationToken.None);
        Assert.NotNull(batchResults);

        // Live cold-start now reuses the batch-computed map (computed-flag short-circuit).
        var result = harness.Engine.GetRecommendations(coldUser, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("strategyColdStart", result!.ScoringStrategyKey);

        var favoriteRec = result.Recommendations.FirstOrDefault(i => i.ItemId == crowdFavorite.Id);
        var unseenRec = result.Recommendations.FirstOrDefault(i => i.ItemId == unseenByCrowd.Id);
        Assert.NotNull(favoriteRec);
        Assert.NotNull(unseenRec);

        // The reused (not silently dropped) community map still separates the two items.
        Assert.True(
            favoriteRec!.Score > unseenRec!.Score,
            $"Reused batch community map must keep the crowd favorite (score={favoriteRec.Score}) above " +
            $"the unseen candidate (score={unseenRec.Score}).");
    }

    // Reflection glue

    private static Dictionary<Guid, int>? Invoke(IReadOnlyDictionary<Guid, HashSet<Guid>> userSets)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "BuildCommunityPopularityMap",
                BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (Dictionary<Guid, int>?)method!.Invoke(null, [userSets]);
    }
}