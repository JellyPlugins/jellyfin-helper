using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="PreferenceBuilder"/>: BuildStudioPreferenceSet,
///     BuildTagPreferenceSet, BuildPeoplePreferenceSet, BuildGenreExposureAnalysis,
///     ComputeGenreExposureFeatures, and temporal decay in genre preferences.
/// </summary>
public class PreferenceBuilderTests
{
    // === BuildGenrePreferenceVector ===

    [Fact]
    public void BuildGenrePreferenceVector_TemporalDecay_RecentItemsWeighMore()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo
                {
                    ItemId = Guid.NewGuid(),
                    Played = true,
                    LastPlayedDate = DateTime.UtcNow.AddDays(-7),
                    Genres = ["Action"]
                },
                new WatchedItemInfo
                {
                    ItemId = Guid.NewGuid(),
                    Played = true,
                    LastPlayedDate = DateTime.UtcNow.AddDays(-365),
                    Genres = ["Comedy"]
                }
            ]
        };

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        // Action (7 days ago) should have higher weight than Comedy (365 days ago)
        Assert.True(vector.TryGetValue("Action", out var actionWeight));
        Assert.True(vector.TryGetValue("Comedy", out var comedyWeight));
        Assert.True(actionWeight > comedyWeight,
            $"Recent Action ({actionWeight:F4}) should outweigh old Comedy ({comedyWeight:F4})");
    }

    [Fact]
    public void BuildGenrePreferenceVector_FavoriteBoost()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo
                {
                    ItemId = Guid.NewGuid(),
                    Played = true,
                    IsFavorite = true,
                    LastPlayedDate = DateTime.UtcNow.AddDays(-30),
                    Genres = ["SciFi"]
                },
                new WatchedItemInfo
                {
                    ItemId = Guid.NewGuid(),
                    Played = true,
                    IsFavorite = false,
                    LastPlayedDate = DateTime.UtcNow.AddDays(-30),
                    Genres = ["Drama"]
                }
            ]
        };

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        // SciFi (favorited) should have higher weight than Drama (not favorited)
        Assert.True(vector["SciFi"] > vector["Drama"],
            "Favorited genre should have higher weight");
    }

    [Fact]
    public void BuildGenrePreferenceVector_UnplayedFavorite_StillIncluded()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo
                {
                    ItemId = Guid.NewGuid(),
                    Played = false,
                    IsFavorite = true,
                    Genres = ["Horror"]
                }
            ]
        };

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        Assert.True(vector.ContainsKey("Horror"),
            "Unplayed favorited items should contribute to genre preferences");
    }

    // === BuildPeoplePreferenceSet ===

    [Fact]
    public void BuildPeoplePreferenceSet_CollectsFromPlayedAndFavorited()
    {
        var movieId = Guid.NewGuid();
        var favId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { movieId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A" } },
            { favId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Director B" } }
        };

        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = movieId, Played = true },
                new WatchedItemInfo { ItemId = favId, Played = false, IsFavorite = true }
            ]
        };

        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);

        Assert.Contains("Actor A", result);
        Assert.Contains("Director B", result);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_SkipsUnplayedNonFavorite()
    {
        var itemId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { itemId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor C" } }
        };

        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = itemId, Played = false, IsFavorite = false }]
        };

        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_IncludesSeriesMapping()
    {
        var episodeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { seriesId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor D" } }
        };

        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = episodeId, SeriesId = seriesId, Played = true }]
        };

        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);

        Assert.Contains("Actor D", result);
    }

    // === BuildGenreExposureAnalysis ===

    [Fact]
    public void BuildGenreExposureAnalysis_InsufficientHistory_ReturnsInvalid()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Action"] }
            ]
        };

        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);

        Assert.False(analysis.IsValid);
    }

    [Fact]
    public void BuildGenreExposureAnalysis_SufficientHistory_ReturnsValid()
    {
        var profile = new UserWatchProfile { WatchedItems = [] };

        // Add 30+ items to meet MinWatchCountForGenreExposure threshold
        for (var i = 0; i < 35; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = DateTime.UtcNow.AddDays(-i),
                Genres = i < 25 ? ["Action"] : ["Comedy"]
            });
        }

        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);

        Assert.True(analysis.IsValid);
        Assert.True(analysis.DominantGenres.Count > 0);
    }

    // === ComputeGenreExposureFeatures ===

    [Fact]
    public void ComputeGenreExposureFeatures_InvalidAnalysis_ReturnsNeutral()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AveragePreferenceWeight = 0,
            GenrePreferences = new Dictionary<string, double>(),
            IsValid = false
        };

        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(["Action"], analysis);

        Assert.Equal(0.0, underexposure);
        Assert.Equal(0.0, dominance);
        Assert.Equal(0.0, gap);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_EmptyGenres_ReturnsNeutral()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double> { { "Action", 1.0 } },
            IsValid = true
        };

        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures([], analysis);

        Assert.Equal(0.0, underexposure);
        Assert.Equal(0.0, dominance);
        Assert.Equal(0.0, gap);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_DominantGenre_HighDominanceRatio()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action", "SciFi", "Drama" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 1.0 }, { "SciFi", 0.8 }, { "Drama", 0.6 }
            },
            IsValid = true
        };

        var (_, dominance, _) =
            PreferenceBuilder.ComputeGenreExposureFeatures(["Action", "SciFi"], analysis);

        // Both candidate genres are in the dominant set
        Assert.Equal(1.0, dominance);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_UnderexposedGenre_HighUnderexposure()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Horror" },
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 1.0 }, { "Horror", 0.01 }
            },
            IsValid = true
        };

        var (underexposure, _, _) =
            PreferenceBuilder.ComputeGenreExposureFeatures(["Horror"], analysis);

        Assert.Equal(1.0, underexposure);
    }

    [Fact]
    public void ComputeGenreExposureFeatures_AffinityGap_BelowAverage()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
            AveragePreferenceWeight = 0.8,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 1.0 }, { "Horror", 0.1 }
            },
            IsValid = true
        };

        var (_, _, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(["Horror"], analysis);

        // Horror weight (0.1) is far below average (0.8), so gap should be high
        Assert.True(gap > 0.5, $"Affinity gap should be high for below-average genre, got {gap:F4}");
    }

    [Fact]
    public void ComputeGenreExposureFeatures_NullWhitespaceGenres_Handled()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 1.0 }
            },
            IsValid = true
        };

        // Candidate genres with whitespace entries should be filtered
        var (underexposure, dominance, _) =
            PreferenceBuilder.ComputeGenreExposureFeatures(["", " ", "Action"], analysis);

        // Only "Action" counts as a valid genre
        Assert.Equal(0.0, underexposure);
        Assert.Equal(1.0, dominance);
    }
}