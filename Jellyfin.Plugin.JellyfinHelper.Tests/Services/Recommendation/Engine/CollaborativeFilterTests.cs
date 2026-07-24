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
    public void BuildCollaborativeMap_SingleUserMode_AppliesIdfForNicheItems()
    {
        // Without a precomputed dictionary the single-user (on-demand) path used to
        // skip IDF entirely, so mainstream items outranked niche items even though the training
        // and batch paths damped them. Both branches must now produce the same ordering.
        WatchedItemInfo P(Guid id) => new() { ItemId = id, Played = true };
        var shared1 = Guid.NewGuid();
        var shared2 = Guid.NewGuid();
        var shared3 = Guid.NewGuid();
        var mainstreamItem = Guid.NewGuid();
        var nicheItem = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(shared1), P(shared2), P(shared3) }
        };

        // Single overlap neighbour carrying both candidate items.
        var neighbour = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>
            {
                P(shared1), P(shared2), P(shared3), P(mainstreamItem), P(nicheItem)
            }
        };

        // Extra viewers of mainstreamItem inflate its popularity but do not overlap enough with
        // user to reach the MinCollaborativeOverlap gate, so they only affect the IDF count.
        var extraOne = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(mainstreamItem), P(Guid.NewGuid()) }
        };
        var extraTwo = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(mainstreamItem), P(Guid.NewGuid()) }
        };

        var profiles = new Collection<UserWatchProfile> { user, neighbour, extraOne, extraTwo };

        // On-demand path: no precomputedUserSets passed in.
        var map = CollaborativeFilter.BuildCollaborativeMap(user, profiles);

        Assert.True(map.TryGetValue(nicheItem, out var nicheScore));
        Assert.True(map.TryGetValue(mainstreamItem, out var mainstreamScore));
        Assert.True(nicheScore > mainstreamScore,
            $"IDF must dampen mainstream items in single-user mode too (niche={nicheScore:F4}, mainstream={mainstreamScore:F4})");
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
        // A sparse-history neighbour (4 watches, below the 20-watch trust ceiling) is
        // down-weighted but must still produce a positive score
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

    [Fact]
    public void BuildCollaborativeMap_TrustWeight_SparseVsFullTrust_AttenuatesSignal()
    {
        // Approach: hold the neighbour identity constant (same watched IDs, same Jaccard) and
        // toggle the trust gate on/off by swapping the gatekeeper profile. When the gate is
        // OFF (all-sparse deployment) trust=1.0. When the gate is ON (at least one rich profile
        // exists) trust=1-exp(-4/CollaborativeTrustScale) — a large attenuation for a 4-watch
        // neighbour. Anything else (score identical, or gated ≥ ungated) means the trust factor
        // is broken.
        var shared = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var recommendedItem = Guid.NewGuid();

        WatchedItemInfo P(Guid id) => new() { ItemId = id, Played = true };

        var userWatchedItems = new Collection<WatchedItemInfo>(
            shared.Concat(Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()))
                .Select(id => new WatchedItemInfo { ItemId = id, Played = true })
                .ToList());

        // Identical sparse neighbour in both scenarios — same watched IDs, same total count (4).
        // This means the Jaccard, overlap, and popularity contributions are byte-for-byte equal
        // across the two BuildCollaborativeMap invocations; only the trust factor differs.
        var sparseNeighbourIds = shared.Append(recommendedItem).ToArray();

        var anchorUserId = Guid.NewGuid();
        var sparseNeighbourUserId = Guid.NewGuid();

        // Scenario A: cold-start gate RELEASED (no power users → gate is off → trust bypassed = 1.0).
        // All profiles are sparse: no one has crossed CollaborativeTrustWatchCeiling, so the
        // gate stays open and every sparse neighbour gets full trust.
        var userA = new UserWatchProfile { UserId = anchorUserId, WatchedItems = userWatchedItems };
        var sparseNeighbourA = new UserWatchProfile
        {
            UserId = sparseNeighbourUserId,
            WatchedItems = new Collection<WatchedItemInfo>(sparseNeighbourIds.Select(P).ToList())
        };
        var profilesA = new Collection<UserWatchProfile> { userA, sparseNeighbourA };
        var mapA = CollaborativeFilter.BuildCollaborativeMap(
            userA, profilesA, CollaborativeFilter.PrecomputeUserWatchSets(profilesA));

        // Scenario B: cold-start gate ENGAGED (a power user exists → gate is on → trust applies).
        // Adding a rich gatekeeper who has crossed CollaborativeTrustWatchCeiling flips the gate
        // on; the sparse neighbour's trust is now attenuated, reducing their recommendation score.
        var userB = new UserWatchProfile { UserId = anchorUserId, WatchedItems = userWatchedItems };
        var sparseNeighbourB = new UserWatchProfile
        {
            UserId = sparseNeighbourUserId,
            WatchedItems = new Collection<WatchedItemInfo>(sparseNeighbourIds.Select(P).ToList())
        };
        var gatekeeper = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>(
                Enumerable.Range(0, 30)
                    .Select(_ => P(Guid.NewGuid()))
                    .ToList())
        };
        var profilesB = new Collection<UserWatchProfile> { userB, sparseNeighbourB, gatekeeper };
        var mapB = CollaborativeFilter.BuildCollaborativeMap(
            userB, profilesB, CollaborativeFilter.PrecomputeUserWatchSets(profilesB));

        Assert.True(mapA.TryGetValue(recommendedItem, out var scoreA));
        Assert.True(mapB.TryGetValue(recommendedItem, out var scoreB));
        Assert.True(scoreA > 0.0, "Scenario A (gate released, trust=1.0) score must be positive");
        Assert.True(scoreB > 0.0, "Scenario B (gate engaged, trust attenuated) score must still be positive");
        Assert.True(
            scoreB < scoreA,
            $"Trust factor must attenuate the sparse neighbour when the gate is engaged (Scenario B) — " +
            $"expected scoreB ({scoreB:F4}) < scoreA ({scoreA:F4}). If they're equal, trust is a no-op; " +
            $"if scoreB > scoreA, the trust factor is inverted.");
    }

    [Fact]
    public void BuildCollaborativeMap_GeometricMean_MainstreamItemGetsMoreSignalThanProductStacking()
    {
        // The test locks the qualitative property (score ~2.4× larger under the geometric
        // mean) rather than a rigid absolute value, so future re-tuning of the trust curve
        // won't cause a flake. The lower bound of 0.20 is well above the old product's
        // 0.1271 while giving headroom below the theoretical geometric-mean value.
        WatchedItemInfo P(Guid id) => new() { ItemId = id, Played = true };

        var shared1 = Guid.NewGuid();
        var shared2 = Guid.NewGuid();
        var shared3 = Guid.NewGuid();
        var mainstreamItem = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(shared1), P(shared2), P(shared3) }
        };

        // Sparse neighbour: 5 watches total, 3 shared with user + mainstreamItem + one filler
        var sparseNeighbour = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>
            {
                P(shared1), P(shared2), P(shared3), P(mainstreamItem), P(Guid.NewGuid())
            }
        };

        // Power user with 25 watches so the trust gate flips to active
        var powerUserItems = new List<WatchedItemInfo>();
        for (var i = 0; i < 25; i++)
        {
            powerUserItems.Add(P(Guid.NewGuid()));
        }

        var powerUser = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>(powerUserItems)
        };

        // Two extra users bumping mainstreamItem's popularity to userCount = 4 (sparse + 3 extras).
        // Each has 2 unrelated watches so they don't create collaborative overlap with `user`.
        var extraOne = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(mainstreamItem), P(Guid.NewGuid()) }
        };
        var extraTwo = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(mainstreamItem), P(Guid.NewGuid()) }
        };
        var extraThree = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(mainstreamItem), P(Guid.NewGuid()) }
        };

        var profiles = new Collection<UserWatchProfile>
        {
            user, sparseNeighbour, powerUser, extraOne, extraTwo, extraThree
        };
        var precomputed = CollaborativeFilter.PrecomputeUserWatchSets(profiles);
        var map = CollaborativeFilter.BuildCollaborativeMap(user, profiles, precomputed);

        Assert.True(map.TryGetValue(mainstreamItem, out var score));

        // Old product-stacking upper bound would have been ~0.13. The geometric mean must
        // exceed that comfortably (~0.31 in this construction). The 0.20 threshold sits
        // safely between the two so accidental reversion to the old formula would fail here.
        Assert.True(score > 0.20,
            $"Geometric-mean modifier should keep the collaborative signal well above the old " +
            $"product-stacking ceiling for sparse-deployment / mainstream-item pairs, got {score:F4}");

        // Sanity ceiling: the geometric mean must still be strictly less than the raw
        // Jaccard weight (0.75), otherwise the modifier isn't actually damping anything.
        Assert.True(score < 0.75,
            $"Geometric-mean modifier must still damp the raw Jaccard weight, got {score:F4}");
    }

    [Fact]
    public void BuildCollaborativeMap_ColdStartGate_ReleasesTrustWhenAllNeighboursSparse()
    {
        // Deployment scenario: five users, each with 5-6 watches. Without the cold-start gate
        // the trust factor would multiply the collaborative signal by ~0.4 and, combined with
        // IDF, collapse it to a few percent. The gate detects that no neighbour reaches the
        // trust ceiling and releases the trust factor to 1.0, so recommendations still form.
        //
        // The test compares two runs of the same graph:
        //   1) The natural cold-start run (all sparse neighbours, gate open).
        //   2) A "control" run where a single power user is added (gate active, trust damps sparse
        //      contributions).
        // The unique-item score in the cold-start run must exceed the score in the control run,
        // proving the gate is doing real work rather than being a no-op.
        var overlapA = Guid.NewGuid();
        var overlapB = Guid.NewGuid();
        var overlapC = Guid.NewGuid();
        var uniqueItem = Guid.NewGuid();

        WatchedItemInfo P(Guid id) => new() { ItemId = id, Played = true };

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo> { P(overlapA), P(overlapB), P(overlapC) }
        };
        var sparseNeighbour = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>
            {
                P(overlapA), P(overlapB), P(overlapC), P(uniqueItem), P(Guid.NewGuid())
            }
        };

        var coldStartProfiles = new Collection<UserWatchProfile> { user, sparseNeighbour };
        var coldStartPrecomputed = CollaborativeFilter.PrecomputeUserWatchSets(coldStartProfiles);
        var coldStartMap = CollaborativeFilter.BuildCollaborativeMap(user, coldStartProfiles, coldStartPrecomputed);

        Assert.True(coldStartMap.TryGetValue(uniqueItem, out var coldStartScore));
        Assert.True(coldStartScore > 0.0);

        // Control: add a power user with >= 20 watches so the trust gate flips to active.
        var powerUserItems = new List<WatchedItemInfo> { P(overlapA), P(overlapB), P(overlapC), P(Guid.NewGuid()) };
        for (var i = 0; i < 25; i++)
        {
            powerUserItems.Add(P(Guid.NewGuid()));
        }

        var powerUser = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = new Collection<WatchedItemInfo>(powerUserItems)
        };
        var controlProfiles = new Collection<UserWatchProfile> { user, sparseNeighbour, powerUser };
        var controlPrecomputed = CollaborativeFilter.PrecomputeUserWatchSets(controlProfiles);
        var controlMap = CollaborativeFilter.BuildCollaborativeMap(user, controlProfiles, controlPrecomputed);

        Assert.True(controlMap.TryGetValue(uniqueItem, out var controlScore));
        Assert.True(controlScore > 0.0);
        Assert.True(coldStartScore > controlScore,
            $"Cold-start gate should release the trust factor, so uniqueItem's score is higher when all neighbours are sparse (cold={coldStartScore:F4}, control={controlScore:F4})");
    }
}
