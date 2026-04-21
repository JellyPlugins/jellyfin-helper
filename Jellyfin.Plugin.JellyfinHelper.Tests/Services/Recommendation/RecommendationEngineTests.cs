using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
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
            Guid.NewGuid(), new Dictionary<Guid, double>(), 0));
    }

    [Fact]
    public void ComputeCollaborativeScore_ItemNotInMap_ReturnsZero()
    {
        var map = new Dictionary<Guid, double> { { Guid.NewGuid(), 5.0 } };
        Assert.Equal(0, RecommendationEngine.ComputeCollaborativeScore(Guid.NewGuid(), map, 5.0));
    }

    [Fact]
    public void ComputeCollaborativeScore_MaxItem_ReturnsOne()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, double>
        {
            { itemId, 1.5 },
            { Guid.NewGuid(), 0.75 }
        };

        Assert.Equal(1.0, RecommendationEngine.ComputeCollaborativeScore(itemId, map, 1.5));
    }

    [Fact]
    public void ComputeCollaborativeScore_HalfMax_ReturnsHalf()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, double>
        {
            { Guid.NewGuid(), 1.0 },
            { itemId, 0.5 }
        };

        Assert.Equal(0.5, RecommendationEngine.ComputeCollaborativeScore(itemId, map, 1.0));
    }

    [Fact]
    public void ComputeCollaborativeScore_ZeroMax_ReturnsZero()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, double> { { itemId, 0.5 } };
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
        // Jaccard similarity: overlap=3, union=3+4-3=4, weight=3/4=0.75
        Assert.Equal(0.75, map[uniqueToOther], 4);
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

    // --- ComputeUserRatingScore tests ---

    [Fact]
    public void ComputeUserRatingScore_NullItem_ReturnsNeutral()
    {
        Assert.Equal(0.5, RecommendationEngine.ComputeUserRatingScore(null));
    }

    [Fact]
    public void ComputeUserRatingScore_NoRating_ReturnsNeutral()
    {
        var item = new WatchedItemInfo { UserRating = null };
        Assert.Equal(0.5, RecommendationEngine.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_ZeroRating_ReturnsNeutral()
    {
        var item = new WatchedItemInfo { UserRating = 0 };
        Assert.Equal(0.5, RecommendationEngine.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_MaxRating_ReturnsOne()
    {
        var item = new WatchedItemInfo { UserRating = 10.0 };
        Assert.Equal(1.0, RecommendationEngine.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_MidRating_ReturnsHalf()
    {
        var item = new WatchedItemInfo { UserRating = 5.0 };
        Assert.Equal(0.5, RecommendationEngine.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_AboveTen_ClampedToOne()
    {
        var item = new WatchedItemInfo { UserRating = 15.0 };
        Assert.Equal(1.0, RecommendationEngine.ComputeUserRatingScore(item));
    }

    // --- ComputeCompletionRatio tests ---

    [Fact]
    public void ComputeCompletionRatio_NullItem_ReturnsZero()
    {
        Assert.Equal(0.0, RecommendationEngine.ComputeCompletionRatio(null));
    }

    [Fact]
    public void ComputeCompletionRatio_ZeroRuntime_ReturnsZero()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 0, PlaybackPositionTicks = 100 };
        Assert.Equal(0.0, RecommendationEngine.ComputeCompletionRatio(item));
    }

    [Fact]
    public void ComputeCompletionRatio_HalfWatched_ReturnsHalf()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 1000, PlaybackPositionTicks = 500 };
        Assert.Equal(0.5, RecommendationEngine.ComputeCompletionRatio(item));
    }

    [Fact]
    public void ComputeCompletionRatio_FullyWatched_ReturnsOne()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 1000, PlaybackPositionTicks = 1000 };
        Assert.Equal(1.0, RecommendationEngine.ComputeCompletionRatio(item));
    }

    [Fact]
    public void ComputeCompletionRatio_OverWatched_ClampedToOne()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 1000, PlaybackPositionTicks = 1500 };
        Assert.Equal(1.0, RecommendationEngine.ComputeCompletionRatio(item));
    }

    // --- ComputeJaccardFromSets tests ---

    [Fact]
    public void ComputeJaccardFromSets_BothEmpty_ReturnsZero()
    {
        var a = new HashSet<string>();
        var b = new HashSet<string>();
        Assert.Equal(0.0, RecommendationEngine.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_OneEmpty_ReturnsZero()
    {
        var a = new HashSet<string> { "Action" };
        var b = new HashSet<string>();
        Assert.Equal(0.0, RecommendationEngine.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_Identical_ReturnsOne()
    {
        var a = new HashSet<string> { "Action", "Comedy" };
        var b = new HashSet<string> { "Action", "Comedy" };
        Assert.Equal(1.0, RecommendationEngine.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_NoOverlap_ReturnsZero()
    {
        var a = new HashSet<string> { "Action" };
        var b = new HashSet<string> { "Comedy" };
        Assert.Equal(0.0, RecommendationEngine.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_PartialOverlap_ReturnsExpected()
    {
        // intersection=1 (Action), union=3 (Action,Comedy,Drama)
        var a = new HashSet<string> { "Action", "Comedy" };
        var b = new HashSet<string> { "Action", "Drama" };
        Assert.Equal(1.0 / 3.0, RecommendationEngine.ComputeJaccardFromSets(a, b), 4);
    }

    // --- Cold-start behavior tests ---

    [Fact]
    public void BuildGenrePreferenceVector_WithFavorites_BoostsGenres()
    {
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 5 },
                { "Comedy", 5 }
            },
            WatchedItems =
            [
                new WatchedItemInfo
                {
                    IsFavorite = true,
                    Genres = new[] { "Action" },
                    ItemId = Guid.NewGuid(),
                    Played = true
                }
            ]
        };

        var vector = RecommendationEngine.BuildGenrePreferenceVector(profile);

        // Action should be boosted (5 + 3.0 = 8.0) vs Comedy (5.0)
        // Normalized: Action = 1.0, Comedy = 5.0/8.0 = 0.625
        Assert.Equal(1.0, vector["Action"]);
        Assert.True(vector["Comedy"] < 0.7, $"Comedy should be lower than Action, got {vector["Comedy"]}");
    }

    [Fact]
    public void ComputeGenreSimilarity_CaseInsensitive()
    {
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "action", 1.0 }
        };

        var score = RecommendationEngine.ComputeGenreSimilarity(new[] { "Action" }, prefs);
        Assert.Equal(1.0, score, 4);
    }

    [Fact]
    public void ComputeRecencyScore_WithExplicitNow_Deterministic()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oneYearAgo = now.AddDays(-365);

        var score = RecommendationEngine.ComputeRecencyScore(oneYearAgo, now);
        Assert.True(score > 0.4 && score < 0.6, $"Expected ~0.5 for 1-year decay, got {score}");
    }

    [Fact]
    public void BuildCollaborativeMap_MultipleUsersAccumulateWeight()
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
                new WatchedItemInfo { ItemId = shared2, Played = true },
                new WatchedItemInfo { ItemId = shared3, Played = true }
            ]
        };

        var other1 = new UserWatchProfile
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

        var other2 = new UserWatchProfile
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

        var map = RecommendationEngine.BuildCollaborativeMap(user, [user, other1, other2]);

        // uniqueItem should have accumulated Jaccard weight from both other users
        Assert.True(map.ContainsKey(uniqueItem));
        Assert.True(map[uniqueItem] > 0.75, $"Expected accumulated weight > 0.75 from two users, got {map[uniqueItem]}");
    }

    // ── PeopleSimilarity Tests ──────────────────────────────────────────

    [Fact]
    public void ComputePeopleSimilarity_EmptySets_ReturnsZero()
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = RecommendationEngine.ComputePeopleSimilarity(empty, empty);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarity_NoOverlap_ReturnsZero()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Actor B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor C", "Actor D" };
        var result = RecommendationEngine.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarity_FullOverlap_ReturnsOne()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Director B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Director B" };
        var result = RecommendationEngine.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarity_PartialOverlap_ReturnsJaccard()
    {
        // Intersection = {A}, Union = {A, B, C} → Jaccard = 1/3
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "C" };
        var result = RecommendationEngine.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(1.0 / 3.0, result, 6);
    }

    [Fact]
    public void ComputePeopleSimilarity_CaseInsensitive()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tom hanks" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tom Hanks" };
        var result = RecommendationEngine.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_EmptyProfile_ReturnsEmpty()
    {
        var profile = new UserWatchProfile { WatchedItems = [] };
        var lookup = new Dictionary<Guid, HashSet<string>>();
        var result = RecommendationEngine.BuildPeoplePreferenceSet(
            profile, lookup, new HashSet<Guid>(), new HashSet<Guid>());
        Assert.Empty(result);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_CollectsFromWatchedItems()
    {
        var itemId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { itemId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Director B" } },
            { seriesId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor C" } }
        };

        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = itemId, Played = true },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, SeriesId = seriesId }
            ]
        };

        var watchedIds = new HashSet<Guid> { itemId };
        var watchedSeriesIds = new HashSet<Guid> { seriesId };

        var result = RecommendationEngine.BuildPeoplePreferenceSet(
            profile, lookup, watchedIds, watchedSeriesIds);

        Assert.Contains("Actor A", result);
        Assert.Contains("Director B", result);
        Assert.Contains("Actor C", result);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_SkipsUnplayedItems()
    {
        var itemId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { itemId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A" } }
        };

        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = itemId, Played = false }]
        };

        var result = RecommendationEngine.BuildPeoplePreferenceSet(
            profile, lookup, new HashSet<Guid>(), new HashSet<Guid>());

        Assert.Empty(result);
    }
}
