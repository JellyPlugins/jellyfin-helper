using System;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="CollaborativeFilter"/>: PrecomputeUserWatchSets,
///     BuildCollaborativeMap with IDF weighting, favorites, and edge cases.
/// </summary>
public class CollaborativeFilterTests
{
    // === PrecomputeUserWatchSets ===

    [Fact]
    public void PrecomputeUserWatchSets_IncludesPlayedItems()
    {
        var itemId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = [new WatchedItemInfo { ItemId = itemId, Played = true }]
        };

        var sets = CollaborativeFilter.PrecomputeUserWatchSets([profile]);

        Assert.Contains(itemId, sets[profile.UserId]);
    }

    [Fact]
    public void PrecomputeUserWatchSets_IncludesFavoritedItems()
    {
        var itemId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = [new WatchedItemInfo { ItemId = itemId, Played = false, IsFavorite = true }]
        };

        var sets = CollaborativeFilter.PrecomputeUserWatchSets([profile]);

        Assert.Contains(itemId, sets[profile.UserId]);
    }

    [Fact]
    public void PrecomputeUserWatchSets_ExcludesUnplayedNonFavorite()
    {
        var itemId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = [new WatchedItemInfo { ItemId = itemId, Played = false, IsFavorite = false }]
        };

        var sets = CollaborativeFilter.PrecomputeUserWatchSets([profile]);

        Assert.DoesNotContain(itemId, sets[profile.UserId]);
    }

    [Fact]
    public void PrecomputeUserWatchSets_IncludesSeriesIdFromEpisodes()
    {
        var episodeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = [new WatchedItemInfo { ItemId = episodeId, SeriesId = seriesId, Played = true }]
        };

        var sets = CollaborativeFilter.PrecomputeUserWatchSets([profile]);

        Assert.Contains(episodeId, sets[profile.UserId]);
        Assert.Contains(seriesId, sets[profile.UserId]);
    }

    [Fact]
    public void PrecomputeUserWatchSets_MultipleUsers()
    {
        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = Guid.NewGuid(), WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }] },
            new() { UserId = Guid.NewGuid(), WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }] }
        };

        var sets = CollaborativeFilter.PrecomputeUserWatchSets(profiles);

        Assert.Equal(2, sets.Count);
    }

    // === BuildCollaborativeMap with precomputed sets (IDF path) ===

    [Fact]
    public void BuildCollaborativeMap_WithPrecomputed_ReturnsCoOccurrences()
    {
        var shared1 = Guid.NewGuid();
        var shared2 = Guid.NewGuid();
        var shared3 = Guid.NewGuid();
        var uniqueToOther = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, Played = true },
                new WatchedItemInfo { ItemId = shared3, Played = true }
            ]
        };

        var other = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, Played = true },
                new WatchedItemInfo { ItemId = shared3, Played = true },
                new WatchedItemInfo { ItemId = uniqueToOther, Played = true }
            ]
        };

        var allProfiles = new Collection<UserWatchProfile> { user, other };
        var precomputed = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);
        var map = CollaborativeFilter.BuildCollaborativeMap(user, allProfiles, precomputed);

        Assert.True(map.TryGetValue(uniqueToOther, out var score));
        Assert.True(score > 0);
    }

    [Fact]
    public void BuildCollaborativeMap_IdfBoost_NicheItemsScoreHigher()
    {
        // Setup: both nicheItem and mainstreamItem are recommended via the SAME single
        // similar user (otherUser), so they receive identical Jaccard base weights.
        // The only difference is item popularity:
        //   - nicheItem is watched by 1 user (otherUser) -> IDF not applied (userCount=1)
        //   - mainstreamItem is watched by 3 users -> IDF = 1/log2(1+3) = 0.5
        // Therefore nicheItem should score higher than mainstreamItem.
        var shared1 = Guid.NewGuid();
        var shared2 = Guid.NewGuid();
        var shared3 = Guid.NewGuid();
        var nicheItem = Guid.NewGuid();
        var mainstreamItem = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, Played = true },
                new WatchedItemInfo { ItemId = shared3, Played = true }
            ]
        };

        // otherUser shares overlap with user and has BOTH items
        var otherUser = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, Played = true },
                new WatchedItemInfo { ItemId = shared3, Played = true },
                new WatchedItemInfo { ItemId = nicheItem, Played = true },
                new WatchedItemInfo { ItemId = mainstreamItem, Played = true }
            ]
        };

        // popularUser1 and popularUser2 also watched mainstreamItem (inflating its popularity)
        // but do NOT share enough overlap with user to contribute Jaccard scores.
        // They only affect the itemPopularity count used by IDF.
        var popularUser1 = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = mainstreamItem, Played = true },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }
            ]
        };

        var popularUser2 = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = mainstreamItem, Played = true },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }
            ]
        };

        var allProfiles = new Collection<UserWatchProfile> { user, otherUser, popularUser1, popularUser2 };
        var precomputed = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);
        var map = CollaborativeFilter.BuildCollaborativeMap(user, allProfiles, precomputed);

        Assert.True(map.TryGetValue(nicheItem, out var nicheScore));
        Assert.True(map.TryGetValue(mainstreamItem, out var mainstreamScore));
        Assert.True(nicheScore > mainstreamScore,
            $"Expected niche item to score higher than mainstream due to IDF boost, " +
            $"but got niche={nicheScore:F4}, mainstream={mainstreamScore:F4}");
    }

    [Fact]
    public void BuildCollaborativeMap_EmptyUser_ReturnsEmpty()
    {
        var user = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [] };
        var allProfiles = new Collection<UserWatchProfile> { user };

        var map = CollaborativeFilter.BuildCollaborativeMap(user, allProfiles);

        Assert.Empty(map);
    }

    [Fact]
    public void BuildCollaborativeMap_InsufficientOverlap_ReturnsEmpty()
    {
        var shared1 = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = [new WatchedItemInfo { ItemId = shared1, Played = true }]
        };

        var other = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }
            ]
        };

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user, other]);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildCollaborativeMap_FavoritesCountAsOverlap()
    {
        var shared1 = Guid.NewGuid();
        var shared2 = Guid.NewGuid();
        var shared3 = Guid.NewGuid();
        var uniqueItem = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, IsFavorite = true },
                new WatchedItemInfo { ItemId = shared3, Played = true }
            ]
        };

        var other = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, Played = true },
                new WatchedItemInfo { ItemId = shared3, Played = true },
                new WatchedItemInfo { ItemId = uniqueItem, Played = true }
            ]
        };

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user, other]);
        Assert.True(map.ContainsKey(uniqueItem),
            "Favorited items should count as overlap for collaborative filtering");
    }

    // === Neighbour trust weighting ===
    // A neighbour with very small watch history reaches high Jaccard trivially,
    // but the signal is statistically fragile. BuildCollaborativeMap applies a
    // trust weight (min(1, otherWatchCount / 20)) so sparse neighbours contribute
    // proportionally less. A neighbour with ≥ 20 watches is unaffected.

    [Fact]
    public void BuildCollaborativeMap_TrustWeight_HighHistoryNeighbourStillContributes()
    {
        // Neighbour with 20+ watches → trust = 1.0 → weight identical to pre-fix behaviour.
        var shared = new[]
        {
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        };
        var recommendedItem = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>(
                shared.Select(id => new WatchedItemInfo { ItemId = id, Played = true }).ToList())
        };
        var richNeighbourIds = shared
            .Concat(new[] { recommendedItem })
            .Concat(Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()))
            .ToArray();
        var richNeighbour = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>(
                richNeighbourIds.Select(id => new WatchedItemInfo { ItemId = id, Played = true }).ToList())
        };

        var allProfiles = new Collection<UserWatchProfile> { user, richNeighbour };
        var precomputed = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);
        var map = CollaborativeFilter.BuildCollaborativeMap(user, allProfiles, precomputed);

        Assert.True(map.TryGetValue(recommendedItem, out var score));
        Assert.True(score > 0.0, "Rich-history neighbour should contribute a positive collaborative score");
    }

    [Fact]
    public void BuildCollaborativeMap_TrustWeight_LowHistoryNeighbourStillContributes()
    {
        // A sparse-history neighbour (5 watches, below the 20-watch trust ceiling) is
        // down-weighted to 25% of full trust but must still produce a positive score
        // — we do not want to silently drop legitimate signal, only to attenuate it.
        var shared = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var unique = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>(
                shared.Concat(Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()))
                    .Select(id => new WatchedItemInfo { ItemId = id, Played = true })
                    .ToList())
        };
        var sparseNeighbour = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>(
                shared.Concat(new[] { unique })
                    .Select(id => new WatchedItemInfo { ItemId = id, Played = true })
                    .ToList())
        };

        var allProfiles = new Collection<UserWatchProfile> { user, sparseNeighbour };
        var precomputed = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);
        var map = CollaborativeFilter.BuildCollaborativeMap(user, allProfiles, precomputed);

        Assert.True(map.TryGetValue(unique, out var score));
        Assert.True(score > 0.0, "Sparse-history neighbour should still contribute a (down-weighted) positive score");
    }
}
