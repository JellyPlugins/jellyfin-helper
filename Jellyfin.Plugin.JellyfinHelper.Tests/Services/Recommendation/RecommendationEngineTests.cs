using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

public class RecommendationEngineTests
{
    [Fact]
    public void BuildGenrePreferenceVector_EmptyDistribution_ReturnsEmpty()
    {
        var profile = new UserWatchProfile { GenreDistribution = new Dictionary<string, int>() };
        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);
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

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        Assert.Equal(1.0, vector["Action"]);
        Assert.Equal(0.5, vector["Comedy"]);
        Assert.Equal(0.2, vector["Drama"]);
    }

    [Fact]
    public void BuildGenrePreferenceVector_ZeroCounts_ReturnsEmpty()
    {
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 0 },
                { "Comedy", 0 }
            }
        };

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        // Zero-count genres with no WatchedItems produce an empty vector
        Assert.Empty(vector);
    }

    [Fact]
    public void ComputeGenreSimilarity_NoGenres_ReturnsZero()
    {
        var prefs = new Dictionary<string, double> { { "Action", 1.0 } };
        Assert.Equal(0, SimilarityComputer.ComputeGenreSimilarity([], prefs));
    }

    [Fact]
    public void ComputeGenreSimilarity_NoPreferences_ReturnsZero()
    {
        Assert.Equal(0, SimilarityComputer.ComputeGenreSimilarity(
            new[] { "Action" }, new Dictionary<string, double>()));
    }

    [Fact]
    public void ComputeGenreSimilarity_FullMatch_SingleGenre_ReturnsOne()
    {
        // Single genre in both candidate and prefs -> cosine = 1.0
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 }
        };

        var score = SimilarityComputer.ComputeGenreSimilarity(new[] { "Action" }, prefs);
        Assert.Equal(1.0, score, 4);
    }

    [Fact]
    public void ComputeGenreSimilarity_FullMatch_MultiGenre_ReturnsHighScore()
    {
        // Candidate has one matching genre, but prefs has multiple -> cosine < 1.0
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 },
            { "Comedy", 0.8 }
        };

        var score = SimilarityComputer.ComputeGenreSimilarity(new[] { "Action" }, prefs);
        // dot=1.0, normC=1, normU=sqrt(1.64)~=1.28 -> cosine~=0.78
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

        // Two genres, only one matches -> cosine = 1.0 / (sqrt(2) * 1.0) ~= 0.707
        // With unknown-genre damping (factor 0.5): 0.707 × (1 - 0.5 × 0.5) = 0.707 × 0.75 ~= 0.530
        var score = SimilarityComputer.ComputeGenreSimilarity(new[] { "Action", "Horror" }, prefs);
        Assert.True(score > 0.48 && score < 0.58,
            $"Expected ~0.530 for partial match with unknown-genre damping, got {score:F4}");
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
        var score = SimilarityComputer.ComputeGenreSimilarity(
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

        var score = SimilarityComputer.ComputeGenreSimilarity(new[] { "Horror", "Comedy" }, prefs);
        Assert.Equal(0.0, score, 4);
    }

    [Fact]
    public void ComputeCollaborativeScore_EmptyMap_ReturnsZero()
    {
        Assert.Equal(0, ContentScoring.ComputeCollaborativeScore(
            Guid.NewGuid(), new Dictionary<Guid, double>(), 0));
    }

    [Fact]
    public void ComputeCollaborativeScore_ItemNotInMap_ReturnsZero()
    {
        var map = new Dictionary<Guid, double> { { Guid.NewGuid(), 5.0 } };
        Assert.Equal(0, ContentScoring.ComputeCollaborativeScore(Guid.NewGuid(), map, 5.0));
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

        Assert.Equal(1.0, ContentScoring.ComputeCollaborativeScore(itemId, map, 1.5));
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

        Assert.Equal(0.5, ContentScoring.ComputeCollaborativeScore(itemId, map, 1.0));
    }

    [Fact]
    public void ComputeCollaborativeScore_ZeroMax_ReturnsZero()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, double> { { itemId, 0.5 } };
        Assert.Equal(0, ContentScoring.ComputeCollaborativeScore(itemId, map, 0));
    }

    [Fact]
    public void NormalizeRating_NullRating_ReturnsNeutral()
    {
        Assert.Equal(0.5, ContentScoring.NormalizeRating(null));
    }

    [Fact]
    public void NormalizeRating_ZeroRating_ReturnsNeutral()
    {
        Assert.Equal(0.5, ContentScoring.NormalizeRating(0f));
    }

    [Fact]
    public void NormalizeRating_MaxRating_ReturnsOne()
    {
        Assert.Equal(1.0, ContentScoring.NormalizeRating(10f));
    }

    [Fact]
    public void NormalizeRating_MidRating_ReturnsHalf()
    {
        Assert.Equal(0.5, ContentScoring.NormalizeRating(5f));
    }

    [Fact]
    public void NormalizeRating_AboveTen_ClampedToOne()
    {
        Assert.Equal(1.0, ContentScoring.NormalizeRating(12f));
    }

    [Fact]
    public void ComputeRecencyScore_FutureDate_ReturnsOne()
    {
        var score = ContentScoring.ComputeRecencyScore(DateTime.UtcNow.AddDays(1));
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void ComputeRecencyScore_Today_ReturnsCloseToOne()
    {
        var score = ContentScoring.ComputeRecencyScore(DateTime.UtcNow);
        Assert.True(score > 0.99, $"Expected > 0.99 but got {score}");
    }

    [Fact]
    public void ComputeRecencyScore_OneYearAgo_DecaysSignificantly()
    {
        var score = ContentScoring.ComputeRecencyScore(DateTime.UtcNow.AddDays(-365));
        // With half-life ~365 days: e^(-0.0019*365) ~= 0.5
        Assert.True(score > 0.4 && score < 0.6, $"Expected ~0.5 but got {score}");
    }

    [Fact]
    public void ComputeYearProximity_NullYear_ReturnsNeutral()
    {
        Assert.Equal(0.5, ContentScoring.ComputeYearProximity(null, 2020));
    }

    [Fact]
    public void ComputeYearProximity_ZeroAverage_ReturnsNeutral()
    {
        Assert.Equal(0.5, ContentScoring.ComputeYearProximity(2020, 0));
    }

    [Fact]
    public void ComputeYearProximity_SameYear_ReturnsOne()
    {
        Assert.Equal(1.0, ContentScoring.ComputeYearProximity(2020, 2020));
    }

    [Fact]
    public void ComputeYearProximity_TenYearsDiff_DecaysExpected()
    {
        var score = ContentScoring.ComputeYearProximity(2010, 2020);
        // e^(-100/200) = e^(-0.5) ~= 0.6065
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

        Assert.Equal(0, ContentScoring.ComputeAverageYear(profile));
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

        Assert.Equal(2010, ContentScoring.ComputeAverageYear(profile));
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

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user, other]);
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

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user, other]);
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
        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user, other]);
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

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user]);
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

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user]);
        Assert.Empty(map);
    }

    // --- ComputeUserRatingScore tests ---

    [Fact]
    public void ComputeUserRatingScore_NullItem_ReturnsNeutral()
    {
        Assert.Equal(0.5, ContentScoring.ComputeUserRatingScore(null));
    }

    [Fact]
    public void ComputeUserRatingScore_NoRating_ReturnsNeutral()
    {
        var item = new WatchedItemInfo { UserRating = null };
        Assert.Equal(0.5, ContentScoring.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_ZeroRating_ReturnsNeutral()
    {
        var item = new WatchedItemInfo { UserRating = 0 };
        Assert.Equal(0.5, ContentScoring.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_MaxRating_ReturnsOne()
    {
        var item = new WatchedItemInfo { UserRating = 10.0 };
        Assert.Equal(1.0, ContentScoring.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_MidRating_ReturnsHalf()
    {
        var item = new WatchedItemInfo { UserRating = 5.0 };
        Assert.Equal(0.5, ContentScoring.ComputeUserRatingScore(item));
    }

    [Fact]
    public void ComputeUserRatingScore_AboveTen_ClampedToOne()
    {
        var item = new WatchedItemInfo { UserRating = 15.0 };
        Assert.Equal(1.0, ContentScoring.ComputeUserRatingScore(item));
    }

    // --- ComputeCompletionRatio tests ---

    [Fact]
    public void ComputeCompletionRatio_NullItem_ReturnsZero()
    {
        Assert.Equal(0.0, ContentScoring.ComputeCompletionRatio(null));
    }

    [Fact]
    public void ComputeCompletionRatio_ZeroRuntime_ReturnsZero()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 0, PlaybackPositionTicks = 100 };
        Assert.Equal(0.0, ContentScoring.ComputeCompletionRatio(item));
    }

    [Fact]
    public void ComputeCompletionRatio_HalfWatched_ReturnsHalf()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 1000, PlaybackPositionTicks = 500 };
        Assert.Equal(0.5, ContentScoring.ComputeCompletionRatio(item));
    }

    [Fact]
    public void ComputeCompletionRatio_FullyWatched_ReturnsOne()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 1000, PlaybackPositionTicks = 1000 };
        Assert.Equal(1.0, ContentScoring.ComputeCompletionRatio(item));
    }

    [Fact]
    public void ComputeCompletionRatio_OverWatched_ClampedToOne()
    {
        var item = new WatchedItemInfo { RuntimeTicks = 1000, PlaybackPositionTicks = 1500 };
        Assert.Equal(1.0, ContentScoring.ComputeCompletionRatio(item));
    }

    // --- ComputeJaccardFromSets tests ---

    [Fact]
    public void ComputeJaccardFromSets_BothEmpty_ReturnsZero()
    {
        var a = new HashSet<string>();
        var b = new HashSet<string>();
        Assert.Equal(0.0, SimilarityComputer.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_OneEmpty_ReturnsZero()
    {
        var a = new HashSet<string> { "Action" };
        var b = new HashSet<string>();
        Assert.Equal(0.0, SimilarityComputer.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_Identical_ReturnsOne()
    {
        var a = new HashSet<string> { "Action", "Comedy" };
        var b = new HashSet<string> { "Action", "Comedy" };
        Assert.Equal(1.0, SimilarityComputer.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_NoOverlap_ReturnsZero()
    {
        var a = new HashSet<string> { "Action" };
        var b = new HashSet<string> { "Comedy" };
        Assert.Equal(0.0, SimilarityComputer.ComputeJaccardFromSets(a, b));
    }

    [Fact]
    public void ComputeJaccardFromSets_PartialOverlap_ReturnsExpected()
    {
        // intersection=1 (Action), union=3 (Action,Comedy,Drama)
        var a = new HashSet<string> { "Action", "Comedy" };
        var b = new HashSet<string> { "Action", "Drama" };
        Assert.Equal(1.0 / 3.0, SimilarityComputer.ComputeJaccardFromSets(a, b), 4);
    }

    // --- Cold-start behavior tests ---

    [Fact]
    public void BuildGenrePreferenceVector_WithFavorites_BoostsGenres()
    {
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 3 },
                { "Comedy", 3 }
            },
            WatchedItems =
            [
                new WatchedItemInfo
                {
                    IsFavorite = true,
                    Genres = new[] { "Action" },
                    ItemId = Guid.NewGuid(),
                    Played = true,
                    LastPlayedDate = DateTime.UtcNow.AddDays(-7),
                    PlayCount = 5
                }
            ]
        };

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        // Action should be boosted (temporal weight + favorite boost + PlayCount boost).
        // Comedy comes from the GenreDistribution fallback only (count=3).
        // Both should be present; Action should have a higher weight than Comedy after normalization.
        Assert.True(vector.TryGetValue("Action", out var actionWeight), "Action should be in vector");
        Assert.True(vector.TryGetValue("Comedy", out var comedyWeight), "Comedy should be in vector");
        Assert.True(actionWeight > comedyWeight,
            $"Action ({actionWeight:F4}) should be higher than Comedy ({comedyWeight:F4}) due to favorite boost");
    }

    [Fact]
    public void ComputeGenreSimilarity_CaseInsensitive()
    {
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "action", 1.0 }
        };

        var score = SimilarityComputer.ComputeGenreSimilarity(new[] { "Action" }, prefs);
        Assert.Equal(1.0, score, 4);
    }

    [Fact]
    public void ComputeRecencyScore_WithExplicitNow_Deterministic()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oneYearAgo = now.AddDays(-365);

        var score = ContentScoring.ComputeRecencyScore(oneYearAgo, now);
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

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user, other1, other2]);

        // uniqueItem should have accumulated Jaccard weight from both other users
        // Each user shares 3/4 items with user -> Jaccard = 0.75, total = 1.5
        Assert.True(map.TryGetValue(uniqueItem, out var uniqueItemScore));
        Assert.Equal(1.5, uniqueItemScore, 4);
    }

    // -- PeopleSimilarity Tests ----------------------------------------------

    [Fact]
    public void ComputePeopleSimilarity_EmptySets_ReturnsZero()
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = SimilarityComputer.ComputePeopleSimilarity(empty, empty);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarity_NoOverlap_ReturnsZero()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Actor B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor C", "Actor D" };
        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarity_FullOverlap_ReturnsOne()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Director B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Director B" };
        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarity_PartialOverlap_ReturnsOverlapCoefficient()
    {
        // Intersection = {A}, min(|candidate|, |preferred|) = min(2, 2) = 2 -> Overlap = 1/2
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "C" };
        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(0.5, result, 6);
    }

    [Fact]
    public void ComputePeopleSimilarity_CaseInsensitive()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tom hanks" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tom Hanks" };
        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, preferred);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_EmptyProfile_ReturnsEmpty()
    {
        var profile = new UserWatchProfile { WatchedItems = [] };
        var lookup = new Dictionary<Guid, HashSet<string>>();
        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);
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

        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);

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

        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);

        Assert.Empty(result);
    }

    // -- Edge-Case Tests ----------------------------------------------

    [Fact]
    public void BuildGenrePreferenceVector_SingleGenre_ReturnsOne()
    {
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int> { { "Horror", 7 } }
        };

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        Assert.Single(vector);
        Assert.Equal(1.0, vector["Horror"]);
    }

    [Fact]
    public void BuildCollaborativeMap_EmptyProfiles_ReturnsEmpty()
    {
        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = []
        };

        var map = CollaborativeFilter.BuildCollaborativeMap(user, []);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildCollaborativeMap_EmptyWatchedItems_ReturnsEmpty()
    {
        var user = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = []
        };

        var other = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            WatchedItems = []
        };

        var map = CollaborativeFilter.BuildCollaborativeMap(user, [user, other]);
        Assert.Empty(map);
    }

    [Fact]
    public void ComputeCollaborativeScore_NegativeMax_ReturnsZero()
    {
        var itemId = Guid.NewGuid();
        var map = new Dictionary<Guid, double> { { itemId, 0.5 } };
        Assert.Equal(0, ContentScoring.ComputeCollaborativeScore(itemId, map, -1.0));
    }

    [Fact]
    public void NormalizeRating_NegativeRating_ReturnsNeutral()
    {
        // Negative ratings are treated as invalid/absent and return the neutral value (0.5)
        Assert.Equal(0.5, ContentScoring.NormalizeRating(-5f));
    }

    [Fact]
    public void ComputeRecencyScore_VeryOldDate_ReturnsNearZero()
    {
        var score = ContentScoring.ComputeRecencyScore(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(score >= 0.0 && score < 0.01, $"Expected near-zero for very old date, got {score}");
    }

    [Fact]
    public void ComputeYearProximity_ZeroYear_StillComputes()
    {
        // Edge case: year 0 vs 2020 — should not throw, returns valid score
        var score = ContentScoring.ComputeYearProximity(0, 2020);
        Assert.True(score >= 0.0 && score <= 1.0, $"Expected valid score, got {score}");
    }

    [Fact]
    public void ComputeCompletionRatio_NegativeRuntime_ReturnsZero()
    {
        var item = new WatchedItemInfo { RuntimeTicks = -100, PlaybackPositionTicks = 50 };
        Assert.Equal(0.0, ContentScoring.ComputeCompletionRatio(item));
    }

    [Fact]
    public void ComputePeopleSimilarity_LargePreferredSet_SmallCandidate()
    {
        // Overlap coefficient uses min(|A|,|B|) in denominator
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C", "D", "E" };
        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, preferred);
        // Intersection = {A}, min(1, 5) = 1 -> Overlap = 1/1 = 1.0
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputeGenreSimilarity_DuplicateGenresInCandidate_HandledCorrectly()
    {
        var prefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 1.0 }
        };

        // Candidate with duplicate genres (edge case from malformed metadata)
        // ComputeGenreSimilarity deduplicates via HashSet before computing cosine similarity,
        // so duplicates become a single element. The single matching genre yields cosine = 1.0.
        var score = SimilarityComputer.ComputeGenreSimilarity(
            new[] { "Action", "Action", "Action" }, prefs);

        // After dedup via HashSet: {Action} -> dot=1*1.0=1, normC=sqrt(1)=1, normU=1 -> cosine=1.0
        Assert.Equal(1.0, score, 4);
    }

    [Fact]
    public void ComputeAverageYear_SingleItem_ReturnsThatYear()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { Played = true, Year = 2015 }
            ]
        };

        Assert.Equal(2015, ContentScoring.ComputeAverageYear(profile));
    }

    [Fact]
    public void ComputeAverageYear_EmptyProfile_ReturnsZero()
    {
        var profile = new UserWatchProfile { WatchedItems = [] };
        Assert.Equal(0, ContentScoring.ComputeAverageYear(profile));
    }

    //  -- NaN/Infinity Guard Tests  -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- --

    [Fact]
    public void GuardScore_FiniteValue_ReturnsSameValue()
    {
        Assert.Equal(0.75, ScoringHelper.GuardScore(0.75));
        Assert.Equal(0.0, ScoringHelper.GuardScore(0.0));
        Assert.Equal(1.0, ScoringHelper.GuardScore(1.0));
        Assert.Equal(-0.5, ScoringHelper.GuardScore(-0.5));
    }

    [Fact]
    public void GuardScore_NaN_ReturnsFallback()
    {
        Assert.Equal(ScoringHelper.NaNFallbackScore, ScoringHelper.GuardScore(double.NaN));
    }

    [Fact]
    public void GuardScore_PositiveInfinity_ReturnsFallback()
    {
        Assert.Equal(ScoringHelper.NaNFallbackScore, ScoringHelper.GuardScore(double.PositiveInfinity));
    }

    [Fact]
    public void GuardScore_NegativeInfinity_ReturnsFallback()
    {
        Assert.Equal(ScoringHelper.NaNFallbackScore, ScoringHelper.GuardScore(double.NegativeInfinity));
    }

    [Fact]
    public void ComputeRawScore_NaNWeight_ReturnsFallback()
    {
        var vector = new double[] { 1.0, 0.5, 0.3 };
        var weights = new double[] { double.NaN, 0.5, 0.3 };
        var result = ScoringHelper.ComputeRawScore(vector, weights, 0.0);
        Assert.Equal(ScoringHelper.NaNFallbackScore, result);
    }

    [Fact]
    public void ComputeRawScore_InfinityWeight_ReturnsFallback()
    {
        var vector = new double[] { 1.0, 0.5 };
        var weights = new double[] { double.PositiveInfinity, 0.5 };
        var result = ScoringHelper.ComputeRawScore(vector, weights, 0.0);
        Assert.Equal(ScoringHelper.NaNFallbackScore, result);
    }

    [Fact]
    public void ComputeRawScore_NaNBias_ReturnsFallback()
    {
        var vector = new double[] { 1.0, 0.5 };
        var weights = new double[] { 0.3, 0.5 };
        var result = ScoringHelper.ComputeRawScore(vector, weights, double.NaN);
        Assert.Equal(ScoringHelper.NaNFallbackScore, result);
    }

    [Fact]
    public void ComputeRawScore_ValidInputs_ReturnsCorrectScore()
    {
        var vector = new double[] { 1.0, 2.0 };
        var weights = new double[] { 0.5, 0.25 };
        // 0.1 + (1.0 * 0.5) + (2.0 * 0.25) = 0.1 + 0.5 + 0.5 = 1.1
        var result = ScoringHelper.ComputeRawScore(vector, weights, 0.1);
        Assert.Equal(1.1, result, 6);
    }

    [Fact]
    public void NeuralScoringStrategy_Sigmoid_NaN_ReturnsNaN()
    {
        // Sigmoid itself propagates NaN  -- the guard is in Score(), not Sigmoid()
        var result = NeuralScoringStrategy.Sigmoid(double.NaN);
        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void NeuralScoringStrategy_Score_WithDefaultWeights_ReturnsFiniteValue()
    {
        using var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CombinedCriticScore = 0.7,
            RecencyScore = 0.5
        };

        var score = strategy.Score(features);
        Assert.True(double.IsFinite(score), $"Score should be finite, got {score}");
        Assert.True(score >= 0.0 && score <= 1.0, $"Score should be in [0, 1], got {score}");
    }

    [Fact]
    public void NeuralScoringStrategy_ScoreWithExplanation_ReturnsFiniteValues()
    {
        using var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CombinedCriticScore = 0.7,
            RecencyScore = 0.5
        };

        var explanation = strategy.ScoreWithExplanation(features);
        Assert.True(double.IsFinite(explanation.FinalScore),
            $"FinalScore should be finite, got {explanation.FinalScore}");
        Assert.True(explanation.FinalScore >= 0.0 && explanation.FinalScore <= 1.0,
            $"FinalScore should be in [0, 1], got {explanation.FinalScore}");
    }

    [Fact]
    public void LearnedScoringStrategy_Score_WithDefaultWeights_ReturnsFiniteValue()
    {
        var strategy = new LearnedScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CombinedCriticScore = 0.7,
            RecencyScore = 0.5
        };

        var score = strategy.Score(features);
        Assert.True(double.IsFinite(score), $"Score should be finite, got {score}");
        Assert.True(score >= 0.0 && score <= 1.0, $"Score should be in [0, 1], got {score}");
    }

    // ============================================================
    // Genre Exposure Analysis Tests
    // ============================================================

    [Fact]
    public void BuildGenreExposureAnalysis_InsufficientHistory_ReturnsInvalid()
    {
        var profile = new UserWatchProfile();
        // Add only 10 items (below MinWatchCountForGenreExposure = 30)
        for (var i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                Genres = ["Action"]
            });
        }

        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        Assert.False(analysis.IsValid);
    }

    [Fact]
    public void BuildGenreExposureAnalysis_SufficientHistory_ReturnsValid()
    {
        var profile = new UserWatchProfile();
        // Add 40 items (above MinWatchCountForGenreExposure = 30)
        for (var i = 0; i < 30; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                Genres = ["Action"],
                LastPlayedDate = DateTime.UtcNow.AddDays(-i)
            });
        }

        for (var i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                Genres = ["Drama"],
                LastPlayedDate = DateTime.UtcNow.AddDays(-i)
            });
        }

        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        Assert.True(analysis.IsValid);
        Assert.True(analysis.DominantGenres.Count > 0);
        Assert.True(analysis.AveragePreferenceWeight > 0);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_InvalidAnalysis_ReturnsAllZero()
    {
        var invalidAnalysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(),
            DominantGenres = new HashSet<string>(),
            AveragePreferenceWeight = 0,
            GenrePreferences = new Dictionary<string, double>(),
            IsValid = false
        };

        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(new[] { "Animation" }, invalidAnalysis);

        Assert.Equal(0.0, underexposure);
        Assert.Equal(0.0, dominance);
        Assert.Equal(0.0, gap);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_EmptyGenres_ReturnsAllZero()
    {
        var validAnalysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Animation" },
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { { "Action", 1.0 } },
            IsValid = true
        };

        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(Array.Empty<string>(), validAnalysis);

        Assert.Equal(0.0, underexposure);
        Assert.Equal(0.0, dominance);
        Assert.Equal(0.0, gap);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_AllDominantGenres_HighDominanceZeroUnderexposure()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Animation" },
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action", "Drama", "Thriller" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 1.0 },
                { "Drama", 0.8 },
                { "Thriller", 0.6 }
            },
            IsValid = true
        };

        // Candidate with all dominant genres
        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(new[] { "Action", "Drama" }, analysis);

        Assert.Equal(0.0, underexposure); // None are underexposed
        Assert.Equal(1.0, dominance); // All 2/2 genres are in top-3
        Assert.Equal(0.0, gap); // Candidate avg (0.9) > overall avg (0.5) -> gap = 0
    }

    [Fact]
    public void ComputeGenreExposureFeatures_AnimationForActionUser_ShowsUnderexposure()
    {
        // Simulates: user watches lots of Action (dominant), rarely watches Animation (underexposed)
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Animation", "Horror" },
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action", "SciFi", "Adventure" },
            AveragePreferenceWeight = 0.4,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 1.0 },
                { "SciFi", 0.7 },
                { "Adventure", 0.5 },
                { "Drama", 0.3 },
                { "Animation", 0.02 },
                { "Horror", 0.01 }
            },
            IsValid = true
        };

        // "Spider-Man: Into the Spider-Verse" -- Animation + Action + Adventure
        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(new[] { "Animation", "Action", "Adventure" }, analysis);

        // 1/3 genres (Animation) is underexposed
        Assert.Equal(1.0 / 3.0, underexposure, 4);
        // 2/3 genres (Action, Adventure) are dominant
        Assert.Equal(2.0 / 3.0, dominance, 4);
        // Candidate avg weight = (0.02 + 1.0 + 0.5) / 3 ~= 0.507 > avg 0.4 -> gap = 0
        Assert.Equal(0.0, gap, 4);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_PureAnimationFilm_HighUnderexposure()
    {
        // Pure animation film for an action user -- strong underexposure signal
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Animation", "Family" },
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action", "SciFi", "Thriller" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 1.0 },
                { "SciFi", 0.8 },
                { "Thriller", 0.6 },
                { "Animation", 0.01 },
                { "Family", 0.01 }
            },
            IsValid = true
        };

        // "Frozen" -- Animation + Family
        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(new[] { "Animation", "Family" }, analysis);

        Assert.Equal(1.0, underexposure); // Both genres are underexposed
        Assert.Equal(0.0, dominance); // Neither genre is in top-3
        Assert.True(gap > 0.5, $"AffinityGap should be high for pure animation, got {gap:F4}");
    }

    [Fact]
    public void ComputeGenreExposureFeatures_CaseInsensitive()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "animation" },
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ACTION" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "action", 1.0 },
                { "animation", 0.01 }
            },
            IsValid = true
        };

        var (underexposure, dominance, _) =
            PreferenceBuilder.ComputeGenreExposureFeatures(new[] { "Animation", "Action" }, analysis);

        Assert.Equal(0.5, underexposure, 4); // 1/2 underexposed (case-insensitive match)
        Assert.Equal(0.5, dominance, 4); // 1/2 dominant (case-insensitive match)
    }

    [Fact]
    public void GenreExposureFeatures_DefaultToZero_InCandidateFeatures()
    {
        // Verify that new features default to 0 in CandidateFeatures
        var features = new CandidateFeatures();
        Assert.Equal(0.0, features.GenreUnderexposure);
        Assert.Equal(0.0, features.GenreDominanceRatio);
        Assert.Equal(0.0, features.GenreAffinityGap);
    }

    [Fact]
    public void GenreExposureFeatures_ClampToZeroOne()
    {
        var features = new CandidateFeatures
        {
            GenreUnderexposure = 1.5,
            GenreDominanceRatio = -0.5,
            GenreAffinityGap = 2.0
        };

        Assert.Equal(1.0, features.GenreUnderexposure);
        Assert.Equal(0.0, features.GenreDominanceRatio);
        Assert.Equal(1.0, features.GenreAffinityGap);
    }

    [Fact]
    public void GenreExposureFeatures_InFeatureVector_AtCorrectIndices()
    {
        var features = new CandidateFeatures
        {
            GenreUnderexposure = 0.33,
            GenreDominanceRatio = 0.67,
            GenreAffinityGap = 0.5
        };

        var vector = features.ToVector();

        Assert.Equal(0.33, vector[(int)FeatureIndex.GenreUnderexposure], 10);
        Assert.Equal(0.67, vector[(int)FeatureIndex.GenreDominanceRatio], 10);
        Assert.Equal(0.5, vector[(int)FeatureIndex.GenreAffinityGap], 10);
    }

    [Fact]
    public void DefaultWeights_GenreExposure_HasCorrectValues()
    {
        var weights = DefaultWeights.CreateWeightArray();

        Assert.Equal(-0.12, weights[(int)FeatureIndex.GenreUnderexposure], 10);
        Assert.Equal(0.10, weights[(int)FeatureIndex.GenreDominanceRatio], 10);
        Assert.Equal(-0.08, weights[(int)FeatureIndex.GenreAffinityGap], 10);
    }
}
