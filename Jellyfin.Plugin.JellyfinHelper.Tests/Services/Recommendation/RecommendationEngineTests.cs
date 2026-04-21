using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

public class RecommendationEngineTests
{
    [Fact]
    public void BuildGenrePreferenceVector_EmptyDistribution_ReturnsEmpty()
    {
        var profile = new UserWatchProfile { GenreDistribution = new Dictionary<string, int>() };
        var vector = RecommendationEngine.BuildGenrePreferenceVector(profile);
        Assert.Empty(vector);
    }

    [Fact]
    public void BuildGenrePreferenceVector_NormalizesToMaxOne()
    {
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 10 },
                { "Comedy", 5 },
                { "Drama", 2 }
            }
        };

        var vector = RecommendationEngine.BuildGenrePreferenceVector(profile);

        Assert.Equal(1.0, vector["Action"]);
        Assert.Equal(0.5, vector["Comedy"]);
        Assert.Equal(0.2, vector["Drama"]);
    }

    [Fact]
    public void BuildGenrePreferenceVector_ZeroCounts_ReturnsZeroWeights()
    {
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 0 },
                { "Comedy", 0 }
            }
        };

        var vector = RecommendationEngine.BuildGenrePreferenceVector(profile);

        // When all counts are zero, weights are 0 (no division by zero)
        Assert.Equal(2, vector.Count);
        Assert.Equal(0.0, vector["Action"]);
        Assert.Equal(0.0, vector["Comedy"]);
    }

    [Fact]
    public void ComputeGenreSimilarity_NoGenres_ReturnsZero()
    {
        var prefs = new Dictionary<string, double> { { "Action", 1.0 } };
        Assert.Equal(0, RecommendationEngine.ComputeGenreSimilarity([], prefs));
    }

    [Fact]
    public void ComputeGenreSimilarity_NoPreferences_ReturnsZero()
    {
        Assert.Equal(0, RecommendationEngine.ComputeGenreSimilarity(
            new[] { "Action" }, new Dictionary<string, double>()));
    }

    [Fact]
    public void ComputeGenreSimilarity_FullMatch_SingleGenre_ReturnsOne()
    {
        // Single genre in both candidate and prefs → cosine = 1.0
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 }
        };

        var score = RecommendationEngine.ComputeGenreSimilarity(new[] { "Action" }, prefs);
        Assert.Equal(1.0, score, 4);
    }

    [Fact]
    public void ComputeGenreSimilarity_FullMatch_MultiGenre_ReturnsHighScore()
    {
        // Candidate has one matching genre, but prefs has multiple → cosine < 1.0
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 },
            { "Comedy", 0.8 }
        };

        var score = RecommendationEngine.ComputeGenreSimilarity(new[] { "Action" }, prefs);
        // dot=1.0, normC=1, normU=sqrt(1.64)≈1.28 → cosine≈0.78
        Assert.True(score > 0.7 && score < 0.85,
            $"Expected ~0.78 for single match against multi-genre prefs, got {score:F4}");
    }

    [Fact]
    public void ComputeGenreSimilarity_PartialMatch_CosineScore()
    {
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 }
        };

        // Two genres, only one matches → cosine = 1.0 / (sqrt(2) * 1.0) ≈ 0.707
        var score = RecommendationEngine.ComputeGenreSimilarity(new[] { "Action", "Horror" }, prefs);
        Assert.True(score > 0.65 && score < 0.75,
            $"Expected ~0.707 for partial match, got {score:F4}");
    }

    [Fact]
    public void ComputeGenreSimilarity_MultiGenreCandidate_NotPenalized()
    {
        // Marvel-style: candidate has many genres, all matching the user's prefs
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 },
            { "SciFi", 0.9 },
            { "Adventure", 0.8 },
            { "Fantasy", 0.5 }
        };

        // A film with 3 genres, all matching
        var score = RecommendationEngine.ComputeGenreSimilarity(
            new[] { "Action", "SciFi", "Adventure" }, prefs);

        Assert.True(score > 0.85, $"Multi-genre film with all-matching genres should score high, got {score:F4}");
    }

    [Fact]
    public void ComputeGenreSimilarity_NoOverlap_ReturnsZero()
    {
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 },
            { "SciFi", 0.8 }
        };

        var score = RecommendationEngine.ComputeGenreSimilarity(new[] { "Horror", "Comedy" }, prefs);
        Assert.Equal(0.0, score, 4);
    }

    [Fact]
    public void ComputeCollaborativeScore_EmptyMap_ReturnsZero()
    {
        Assert.Equal(0, RecommendationEngine.ComputeCollaborativeScore(
            Guid.NewGuid(), new Dictionary<Guid, int>(), 0));
    }

    [Fact]
    public void ComputeCollaborativeScore_ItemNotInMap_ReturnsZero()
    {
        var map = new Dictionary<Guid, int> { { Guid.NewGuid(), 5 } };
        Assert.Equal(0, RecommendationEngine.ComputeCollaborativeScore(Guid.NewGuid(), map, 5));
    }

    [Fact]
    public void ComputeCollaborativeScore_MaxItem_ReturnsOne()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, int>
        {
            { itemId, 10 },
            { Guid.NewGuid(), 5 }
        };

        Assert.Equal(1.0, RecommendationEngine.ComputeCollaborativeScore(itemId, map, 10));
    }

    [Fact]
    public void ComputeCollaborativeScore_HalfMax_ReturnsHalf()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, int>
        {
            { Guid.NewGuid(), 10 },
            { itemId, 5 }
        };

        Assert.Equal(0.5, RecommendationEngine.ComputeCollaborativeScore(itemId, map, 10));
    }

    [Fact]
    public void ComputeCollaborativeScore_ZeroMax_ReturnsZero()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, int> { { itemId, 5 } };
        Assert.Equal(0, RecommendationEngine.ComputeCollaborativeScore(itemId, map, 0));
    }

    [Fact]
    public void NormalizeRating_NullRating_ReturnsNeutral()
    {
        Assert.Equal(0.5, RecommendationEngine.NormalizeRating(null));
    }

    [Fact]
    public void NormalizeRating_ZeroRating_ReturnsNeutral()
    {
        Assert.Equal(0.5, RecommendationEngine.NormalizeRating(0f));
    }

    [Fact]
    public void NormalizeRating_MaxRating_ReturnsOne()
    {
        Assert.Equal(1.0, RecommendationEngine.NormalizeRating(10f));
    }

    [Fact]
    public void NormalizeRating_MidRating_ReturnsHalf()
    {
        Assert.Equal(0.5, RecommendationEngine.NormalizeRating(5f));
    }

    [Fact]
    public void NormalizeRating_AboveTen_ClampedToOne()
    {
        Assert.Equal(1.0, RecommendationEngine.NormalizeRating(12f));
    }

    [Fact]
    public void ComputeRecencyScore_FutureDate_ReturnsOne()
    {
        var score = RecommendationEngine.ComputeRecencyScore(DateTime.UtcNow.AddDays(1));
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void ComputeRecencyScore_Today_ReturnsCloseToOne()
    {
        var score = RecommendationEngine.ComputeRecencyScore(DateTime.UtcNow);
        Assert.True(score > 0.99, $"Expected > 0.99 but got {score}");
    }

    [Fact]
    public void ComputeRecencyScore_OneYearAgo_DecaysSignificantly()
    {
        var score = RecommendationEngine.ComputeRecencyScore(DateTime.UtcNow.AddDays(-365));
        // With half-life ~365 days: e^(-0.0019*365) ≈ 0.5
        Assert.True(score > 0.4 && score < 0.6, $"Expected ~0.5 but got {score}");
    }

    [Fact]
    public void ComputeYearProximity_NullYear_ReturnsNeutral()
    {
        Assert.Equal(0.5, RecommendationEngine.ComputeYearProximity(null, 2020));
    }

    [Fact]
    public void ComputeYearProximity_ZeroAverage_ReturnsNeutral()
    {
        Assert.Equal(0.5, RecommendationEngine.ComputeYearProximity(2020, 0));
    }

    [Fact]
    public void ComputeYearProximity_SameYear_ReturnsOne()
    {
        Assert.Equal(1.0, RecommendationEngine.ComputeYearProximity(2020, 2020));
    }

    [Fact]
    public void ComputeYearProximity_TenYearsDiff_DecaysExpected()
    {
        var score = RecommendationEngine.ComputeYearProximity(2010, 2020);
        // e^(-100/200) = e^(-0.5) ≈ 0.6065
        Assert.True(score > 0.55 && score < 0.65, $"Expected ~0.607 but got {score}");
    }

    [Fact]
    public void ComputeAverageYear_NoPlayedWithYears_ReturnsZero()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { Played = false, Year = 2020 },
                new WatchedItemInfo { Played = true, Year = null }
            ]
        };

        Assert.Equal(0, RecommendationEngine.ComputeAverageYear(profile));
    }

    [Fact]
    public void ComputeAverageYear_TwoItems_ReturnsAverage()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { Played = true, Year = 2000 },
                new WatchedItemInfo { Played = true, Year = 2020 }
            ]
        };

        Assert.Equal(2010, RecommendationEngine.ComputeAverageYear(profile));
    }

    [Fact]
    public void BuildCollaborativeMap_NoOverlap_ReturnsEmpty()
    {
        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }
            ]
        };

        var other = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }
            ]
        };

        var map = RecommendationEngine.BuildCollaborativeMap(user, [user, other]);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildCollaborativeMap_SufficientOverlap_ReturnsCoOccurrences()
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

        var map = RecommendationEngine.BuildCollaborativeMap(user, [user, other]);
        Assert.Single(map);
        Assert.Equal(1, map[uniqueToOther]);
    }

    [Fact]
    public void BuildCollaborativeMap_InsufficientOverlap_ReturnsEmpty()
    {
        var shared1 = Guid.NewGuid();
        var shared2 = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, Played = true }
            ]
        };

        var other = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared1, Played = true },
                new WatchedItemInfo { ItemId = shared2, Played = true },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }
            ]
        };

        // Only 2 shared items, minimum is 3
        var map = RecommendationEngine.BuildCollaborativeMap(user, [user, other]);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildCollaborativeMap_SkipsSelf()
    {
        var shared = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var user = new UserWatchProfile
        {
            UserId = userId,
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = shared, Played = true }
            ]
        };

        var map = RecommendationEngine.BuildCollaborativeMap(user, [user]);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildCollaborativeMap_EmptyWatchList_ReturnsEmpty()
    {
        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = []
        };

        var map = RecommendationEngine.BuildCollaborativeMap(user, [user]);
        Assert.Empty(map);
    }

    [Fact]
    public void ResolveStrategy_AlwaysReturnsEnsembleStrategy()
    {
        // Strategy is always Ensemble (combines adaptive ML + heuristic rules)
        var strategy = RecommendationEngine.ResolveStrategy();
        Assert.IsType<EnsembleScoringStrategy>(strategy);
    }
}
