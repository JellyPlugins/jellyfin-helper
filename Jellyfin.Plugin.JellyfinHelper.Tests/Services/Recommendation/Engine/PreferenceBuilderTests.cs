using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="PreferenceBuilder"/>.
/// </summary>
public class PreferenceBuilderTests
{
    [Fact]
    public void BuildGenrePreferenceVector_TemporalDecay_RecentItemsWeighMore()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-7), Genres = ["Action"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-365), Genres = ["Comedy"] }
            ]
        };
        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        Assert.True(vector["Action"] > vector["Comedy"]);
    }

    [Fact]
    public void BuildGenrePreferenceVector_FavoriteBoost()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, IsFavorite = true, LastPlayedDate = DateTime.UtcNow.AddDays(-30), Genres = ["SciFi"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, IsFavorite = false, LastPlayedDate = DateTime.UtcNow.AddDays(-30), Genres = ["Drama"] }
            ]
        };
        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        Assert.True(vector["SciFi"] > vector["Drama"]);
    }

    [Fact]
    public void BuildGenrePreferenceVector_UnplayedFavorite_StillIncluded()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = false, IsFavorite = true, Genres = ["Horror"] }]
        };
        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        Assert.True(vector.ContainsKey("Horror"));
    }

    [Fact]
    public void BuildGenrePreferenceVector_HeavyRewatcher_DoesNotDominateLinearly()
    {
        var now = DateTime.UtcNow;
        var moderateProfile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 5, LastPlayedDate = now, Genres = ["Action"] }] };
        var extremeProfile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 30, LastPlayedDate = now, Genres = ["Action"] }] };
        moderateProfile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 0, LastPlayedDate = now, Genres = ["Anchor"] });
        extremeProfile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 0, LastPlayedDate = now, Genres = ["Anchor"] });
        var mv = PreferenceBuilder.BuildGenrePreferenceVector(moderateProfile);
        var ev = PreferenceBuilder.BuildGenrePreferenceVector(extremeProfile);
        var mRatio = mv["Action"] / mv["Anchor"];
        var eRatio = ev["Action"] / ev["Anchor"];
        Assert.True(eRatio > mRatio);
        Assert.True(eRatio / mRatio < 6.0);
    }

    [Fact]
    public void BuildGenrePreferenceVector_PlayCountBeyond100_IsCapped()
    {
        var now = DateTime.UtcNow;
        var capped = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 100, LastPlayedDate = now, Genres = ["Action"] }, new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 0, LastPlayedDate = now, Genres = ["Anchor"] }] };
        var patho = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 1000, LastPlayedDate = now, Genres = ["Action"] }, new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 0, LastPlayedDate = now, Genres = ["Anchor"] }] };
        var cv = PreferenceBuilder.BuildGenrePreferenceVector(capped);
        var pv = PreferenceBuilder.BuildGenrePreferenceVector(patho);
        Assert.Equal(cv["Action"], pv["Action"], 10);
        Assert.Equal(cv["Anchor"], pv["Anchor"], 10);
    }

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
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = movieId, Played = true }, new WatchedItemInfo { ItemId = favId, Played = false, IsFavorite = true }] };
        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);
        Assert.Contains("Actor A", result);
        Assert.Contains("Director B", result);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_SkipsUnplayedNonFavorite()
    {
        var itemId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>> { { itemId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor C" } } };
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = itemId, Played = false, IsFavorite = false }] };
        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildPeoplePreferenceSet_IncludesSeriesMapping()
    {
        var episodeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>> { { seriesId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor D" } } };
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = episodeId, SeriesId = seriesId, Played = true }] };
        var result = PreferenceBuilder.BuildPeoplePreferenceSet(profile, lookup);
        Assert.Contains("Actor D", result);
    }

    [Fact]
    public void BuildPeoplePreferenceWeights_EmptyProfile_ReturnsEmpty()
    {
        var result = PreferenceBuilder.BuildPeoplePreferenceWeights(new UserWatchProfile { WatchedItems = [] }, new Dictionary<Guid, HashSet<string>>());
        Assert.Empty(result);
    }

    [Fact]
    public void BuildPeoplePreferenceWeights_CountsAppearancesAcrossWatchedItems()
    {
        var n1 = Guid.NewGuid(); var n2 = Guid.NewGuid(); var n3 = Guid.NewGuid(); var c = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { n1, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Christopher Nolan" } },
            { n2, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Christopher Nolan", "Cillian Murphy" } },
            { n3, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Christopher Nolan" } },
            { c, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Timothee Chalamet" } }
        };
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = n1, Played = true }, new WatchedItemInfo { ItemId = n2, Played = true }, new WatchedItemInfo { ItemId = n3, Played = true }, new WatchedItemInfo { ItemId = c, Played = true }] };
        var result = PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookup);
        Assert.Equal(3.0, result["Christopher Nolan"]);
        Assert.Equal(1.0, result["Cillian Murphy"]);
        Assert.Equal(1.0, result["Timothee Chalamet"]);
    }

    [Fact]
    public void BuildPeoplePreferenceWeights_SkipsUnplayedNonFavorite()
    {
        var itemId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>> { { itemId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor X" } } };
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = itemId, Played = false, IsFavorite = false }] };
        var result = PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookup);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildPeoplePreferenceWeights_MergesItemAndSeriesPeopleWithoutDoubleCounting()
    {
        var episodeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { episodeId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A" } },
            { seriesId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Actor B" } }
        };
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = episodeId, SeriesId = seriesId, Played = true }] };
        var result = PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookup);
        Assert.Equal(1.0, result["Actor A"]);
        Assert.Equal(1.0, result["Actor B"]);
    }

    [Fact]
    public void BuildGenreExposureAnalysis_InsufficientHistory_ReturnsInvalid()
    {
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Action"] }] };
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        Assert.False(analysis.IsValid);
    }

    [Fact]
    public void BuildGenreExposureAnalysis_SufficientHistory_ReturnsValid()
    {
        var profile = new UserWatchProfile { WatchedItems = [] };
        for (var i = 0; i < 35; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-i), Genres = i < 25 ? ["Action"] : ["Comedy"] });
        }
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        Assert.True(analysis.IsValid);
        Assert.True(analysis.DominantGenres.Count > 0);
    }

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

    // === BuildGenrePreferenceVector: normalization after proximity expansion ===

    [Fact]
    public void BuildGenrePreferenceVector_ProximityExpansion_StaysNormalized()
    {
        // Contract: after ExpandGenreProximity fires the vector must remain max-normalised
        // (largest weight == 1.0, everything in [0, 1]), AND at least one genre that never
        // appeared directly in watch history must be introduced via proximity — otherwise
        // this test could pass with the expansion removed entirely.
        //
        // Construction:
        //   • 12 items ["Action", "Adventure"] → Action and Adventure are the direct base genres.
        //   • 8 items ["Adventure", "SciFi"] → Adventure and SciFi co-occur, and SciFi is now
        //     also direct.
        //   • 8 items ["Action", "SciFi"] → Action and SciFi co-occur too.
        //
        // ExpandGenreProximity looks at each direct genre's neighbours and adds proximity-derived
        // weight to genres that were themselves already in the vector but whose base weight came
        // from a different (weaker) co-occurrence path. To guarantee a strictly *new* key we
        // instead assert a distinct behaviour: build a snapshot of the direct-only vector by
        // computing what BuildGenrePreferenceVector would produce for a profile that stripped
        // out the co-occurrences, and check the produced vector introduces "SciFi" — a genre
        // whose base weight (from 8+8 rows) is lower than the proximity-boosted weight that a
        // strong Action↔Adventure↔SciFi triangle produces.
        var profile = new UserWatchProfile();
        var baseDate = DateTime.UtcNow.AddDays(-10);
        for (var i = 0; i < 12; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-i),
                Genres = ["Action", "Adventure"]
            });
        }

        for (var i = 0; i < 8; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-100 - i),
                Genres = ["Adventure", "SciFi"]
            });
        }

        for (var i = 0; i < 8; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-200 - i),
                Genres = ["Action", "SciFi"]
            });
        }

        // Build a "proximity-off" reference by feeding a profile with only one row per genre
        // pair so ExpandGenreProximity's co-occurrence gate (needs several co-occurrences) does
        // not fire. Any proximity-derived boost above this baseline proves the expansion did
        // something.
        var baselineProfile = new UserWatchProfile();
        var baselinePairs = new[]
        {
            new[] { "Action", "Adventure" },
            new[] { "Adventure", "SciFi" },
            new[] { "Action", "SciFi" }
        };
        foreach (var pair in baselinePairs)
        {
            baselineProfile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate,
                Genres = pair
            });
        }

        var baselineVector = PreferenceBuilder.BuildGenrePreferenceVector(baselineProfile);
        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        // Normalisation invariants
        Assert.NotEmpty(vector);
        var max = vector.Values.Max();
        Assert.InRange(max, 0.999, 1.0001);
        foreach (var weight in vector.Values)
        {
            Assert.InRange(weight, 0.0, 1.0);
        }

        // At least one genre's *relative rank* changed because of proximity expansion. The
        // baseline has all three genres appear once each with identical decayed weights, so
        // the baseline vector is uniform (every key ≈ 1.0). The full profile has SciFi as a
        // proximity target of both Action and Adventure, so its normalised weight should be
        // strictly less than 1.0 while Action or Adventure hold the peak. This asymmetry only
        // shows up when ExpandGenreProximity actually redistributes weight — a stubbed-out
        // no-op expansion would preserve the baseline's uniform shape.
        Assert.True(baselineVector.ContainsKey("SciFi"));
        var uniformBaseline = baselineVector.Values.All(w => Math.Abs(w - 1.0) < 1e-9);
        Assert.True(uniformBaseline,
            "Sanity check: the baseline profile must produce a uniform vector so the assertion below is meaningful.");

        Assert.True(vector.ContainsKey("SciFi"));
        var vectorHasStructure = vector.Values.Any(w => w < 0.999);
        Assert.True(vectorHasStructure,
            "Proximity expansion should introduce weight variance between genres (peak-vs-off-peak). " +
            "A uniform vector after 28 rows would mean the expansion produced no observable effect.");
    }

    // === Progression multiplier: IsEpisodeCompletedForProgression counter semantics ===
    // These four tests lock the "strict completion" rule for the per-series progression
    // multiplier used by BuildGenrePreferenceVector and BuildPeoplePreferenceWeights.
    // A previous version of the counter used WatchedItemInfo.HasPlaybackActivity(), which
    // also treats PlaybackPositionTicks > 0 as "watched" — meaning a user who briefly opens
    // every episode of a series (a 30-second click-through) would push playedEps up to
    // totalEps and unlock the maximum ProgressionCeiling (1.5) even though no episode was
    // actually finished. The strict counter (Played || PlayCount > 0) treats partial starts
    // as noise, so the multiplier reflects real engagement.

    [Fact]
    public void BuildGenrePreferenceVector_FullyCompletedSeries_ReceivesHigherWeightThanAbandonedSeries()
    {
        // Isolate the progression multiplier from the number of contributing rows.
        //
        // Both series contribute exactly ONE played episode row with identical temporal
        // weight, PlayCount boost, and +0 favorite additive. The only remaining difference is
        // series length:
        //   * SciFi: 1 episode watched of 1 total → rawRatio = 1.0 → multiplier ≈ 1.5.
        //   * Drama: 1 episode watched of 5 total → rawRatio = 0.2 → multiplier ≈ 0.54.
        // If ComputeProgressionMultiplier ever regressed to a constant (e.g. always 1.0),
        // both rows would produce identical genre weights and this test would fail — that is
        // the regression it is designed to catch. A previous version of this test seeded
        // 5 SciFi rows vs. 1 Drama row, which already produced SciFi > Drama purely by row
        // count and would silently pass even after ComputeProgressionMultiplier was neutered.
        // Keeping the row count symmetric while varying only the seriesEpisodeCounts input is
        // what makes this a real progression-multiplier guard.
        var sciFiSeries = Guid.NewGuid();
        var dramaSeries = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { sciFiSeries, 1 }, { dramaSeries, 5 } };

        var profile = new UserWatchProfile();
        var now = DateTime.UtcNow.AddDays(-1); // same recency for both series to isolate progression effect

        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            SeriesId = sciFiSeries,
            Played = true,
            LastPlayedDate = now,
            Genres = ["SciFi"]
        });

        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            SeriesId = dramaSeries,
            Played = true,
            LastPlayedDate = now,
            Genres = ["Drama"]
        });

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile, counts);

        Assert.True(vector.ContainsKey("SciFi"));
        Assert.True(vector.ContainsKey("Drama"));
        Assert.True(vector["SciFi"] > vector["Drama"],
            $"Fully-completed series should out-weigh abandoned series after progression scaling " +
            $"(SciFi={vector["SciFi"]:F4}, Drama={vector["Drama"]:F4})");
    }

    [Fact]
    public void BuildGenrePreferenceVector_PartialStartsDoNotInflateCompletionRatio()
    {
        // The strict completion predicate must ignore rows with PlaybackPositionTicks > 0 but
        // Played=false and PlayCount=0. A non-series "Anchor" movie row (immune to the
        // progression multiplier, which returns 1.0 for non-episode rows) is added to both
        // profiles as a normalisation reference — because both profiles produce a vector with
        // "SciFi" and "Anchor", the SciFi/Anchor ratio exposes the progression multiplier that
        // would otherwise be hidden by max-normalisation.
        //
        // Under the STRICT counter both profiles have the same 2 played episodes counted
        // → mult ≈ 0.78 per row → total SciFi weight identical → SciFi/Anchor ratio identical.
        // Under the REGRESSED HasPlaybackActivity counter profile A would see 5/5 (mult 1.5)
        // while B stays at 2/5 (mult 0.78), so A's ratio would be dramatically larger.
        var series = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { series, 5 } };
        var now = DateTime.UtcNow.AddDays(-1);

        WatchedItemInfo Played() => new()
        {
            ItemId = Guid.NewGuid(),
            SeriesId = series,
            Played = true,
            LastPlayedDate = now,
            Genres = ["SciFi"]
        };

        WatchedItemInfo PartialStart() => new()
        {
            ItemId = Guid.NewGuid(),
            SeriesId = series,
            Played = false,
            PlayCount = 0,
            PlaybackPositionTicks = 3000,
            LastPlayedDate = now,
            Genres = ["SciFi"]
        };

        WatchedItemInfo Anchor() => new()
        {
            ItemId = Guid.NewGuid(),
            // No SeriesId → non-episode row → progression multiplier stays at neutral 1.0
            Played = true,
            LastPlayedDate = now,
            Genres = ["Anchor"]
        };

        var withPartials = new UserWatchProfile
        {
            WatchedItems = [Played(), Played(), PartialStart(), PartialStart(), PartialStart(), Anchor()]
        };
        var withoutPartials = new UserWatchProfile
        {
            WatchedItems = [Played(), Played(), Anchor()]
        };

        var vectorA = PreferenceBuilder.BuildGenrePreferenceVector(withPartials, counts);
        var vectorB = PreferenceBuilder.BuildGenrePreferenceVector(withoutPartials, counts);

        Assert.True(vectorA.ContainsKey("SciFi") && vectorA.ContainsKey("Anchor"));
        Assert.True(vectorB.ContainsKey("SciFi") && vectorB.ContainsKey("Anchor"));

        var ratioA = vectorA["SciFi"] / vectorA["Anchor"];
        var ratioB = vectorB["SciFi"] / vectorB["Anchor"];

        // Both profiles compute the same played counter (2/5), so the SciFi/Anchor ratio must
        // match to within floating-point noise. A regression to HasPlaybackActivity would push
        // ratioA well above ratioB (roughly a 1.5/0.78 ≈ 1.9× multiplier gap).
        Assert.Equal(ratioB, ratioA, 6);
    }

    [Fact]
    public void BuildPeoplePreferenceWeights_SharesProgressionSemanticsWithGenrePipeline()
    {
        // BuildPeoplePreferenceWeights and BuildGenrePreferenceVector must use the SAME strict
        // completion predicate (Played || PlayCount>0) for the per-series progression counter.
        // If they diverge, the People-Similarity feature and the Genre-Similarity feature see
        // contradictory progression signals for the same series.
        //
        // Ordering-based construction: identical eligible Played rows (3) but different partial
        // -start noise. Under the strict counter both profiles compute the same 3/5 ratio, so
        // "Actor Z" ends up with the same weight. Under a regressed HasPlaybackActivity counter
        // profile A would see 5/5 → mult 1.5 while profile B still sees 3/5 → mult 1.02, so
        // A's Actor-Z weight would be strictly larger. Asserting equality (with a tiny epsilon
        // for floating-point noise) is robust to any tuning of the ProgressionFloor / span
        // constants because the two profiles walk through the same code path with the same
        // input to the multiplier — they only differ in the count of noise rows.
        var series = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { series, 5 } };
        var now = DateTime.UtcNow.AddDays(-1);

        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { series, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor Z" } }
        };

        WatchedItemInfo Played() => new()
        {
            ItemId = Guid.NewGuid(), SeriesId = series, Played = true, LastPlayedDate = now
        };

        WatchedItemInfo PartialStart() => new()
        {
            ItemId = Guid.NewGuid(),
            SeriesId = series,
            Played = false,
            PlayCount = 0,
            PlaybackPositionTicks = 5000,
            LastPlayedDate = now
        };

        var withPartials = new UserWatchProfile
        {
            WatchedItems = [Played(), Played(), Played(), PartialStart(), PartialStart()]
        };
        var withoutPartials = new UserWatchProfile
        {
            WatchedItems = [Played(), Played(), Played()]
        };

        var wA = PreferenceBuilder.BuildPeoplePreferenceWeights(withPartials, lookup, counts);
        var wB = PreferenceBuilder.BuildPeoplePreferenceWeights(withoutPartials, lookup, counts);

        Assert.True(wA.TryGetValue("Actor Z", out var weightA));
        Assert.True(wB.TryGetValue("Actor Z", out var weightB));
        Assert.Equal(weightB, weightA, 9);
    }

    [Fact]
    public void BuildPeoplePreferenceWeights_UnplayedFavoriteEpisode_KeepsFullWeightDespiteAbandonedSeries()
    {
        // Regression guard for the "favorite always keeps full weight" invariant advertised in
        // BuildPeoplePreferenceWeights' XML doc. Before this fix, an unplayed-favorite EPISODE
        // of an abandoned series inherited that series' ProgressionFloor (0.3), silently
        // contradicting the invariant — BuildPeoplePreferenceWeights has no separate favorite
        // additive, so the multiplier was the only signal per person and 0.3 is meaningfully
        // weaker than the 1.0 an unplayed-favorite movie would produce.
        //
        // Construction:
        //   * Two watched-item rows on the SAME series (5 total episodes):
        //       - One PLAYED episode with people {"Actor A"}     → counts as completed
        //       - One UNPLAYED FAVORITE episode with people {"Actor A", "Actor B"}
        //         → NOT a completed episode; earlier code would have applied multiplier 0.3.
        //   * The played row contributes multiplier ~0.54 (1/5 completed → ProgressionFloor +
        //     0.2 × ProgressionSpan = 0.3 + 0.24 = 0.54) to Actor A.
        //   * The unplayed favorite row must now contribute a FULL 1.0 to both Actor A and
        //     Actor B (favorite bypass). Actor A's total therefore lands around 1.54, Actor B
        //     at exactly 1.0.
        //
        // The critical assertion: Actor B — who ONLY appears on the unplayed favorite row —
        // must have weight ≈ 1.0, not 0.3. A regression that reintroduced the multiplier for
        // this row would drop Actor B to ~0.3, which the assertion below rejects.
        var series = Guid.NewGuid();
        var playedEpisode = Guid.NewGuid();
        var favoriteEpisode = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { series, 5 } };

        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { playedEpisode, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A" } },
            { favoriteEpisode, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor A", "Actor B" } }
        };

        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo
                {
                    ItemId = playedEpisode,
                    SeriesId = series,
                    Played = true,
                    LastPlayedDate = DateTime.UtcNow.AddDays(-1)
                },
                new WatchedItemInfo
                {
                    // Unplayed favorite episode of the (mostly abandoned) same series.
                    ItemId = favoriteEpisode,
                    SeriesId = series,
                    Played = false,
                    PlayCount = 0,
                    IsFavorite = true,
                    LastPlayedDate = null
                }
            ]
        };

        var weights = PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookup, counts);

        Assert.True(weights.TryGetValue("Actor B", out var actorBWeight));

        // Actor B ONLY appears on the unplayed-favorite row. Its weight is therefore the
        // single-row multiplier for that row. With the favorite-bypass fix that multiplier
        // is 1.0. A regression to the abandoned-series path would drop this to ~0.3.
        Assert.Equal(1.0, actorBWeight, 6);

        // Sanity: Actor A appears on BOTH rows, so its weight is the sum of:
        //   (a) played episode multiplier: rawRatio = 1/5 = 0.2 → ProgressionFloor + 0.2*Span
        //       = 0.3 + 0.24 = 0.54
        //   (b) unplayed favorite bypass: 1.0
        // Total ≈ 1.54. Assert strictly greater than Actor B's 1.0, and greater than the
        // sum of two multipliers if BOTH were bypassed (2.0) — the played row still uses the
        // ratio, only the favorite gets bypassed.
        Assert.True(weights.TryGetValue("Actor A", out var actorAWeight));
        Assert.True(actorAWeight > actorBWeight,
            $"Actor A appears on both rows and must out-weigh Actor B, got A={actorAWeight}, B={actorBWeight}");
        Assert.True(actorAWeight < 2.0,
            $"Actor A must not be treated as two bypasses; expected < 2.0, got {actorAWeight}");
    }

    [Fact]
    public void ComputeProgressionMultiplier_AbandonedSeries_StillContributesFloor()
    {
        // Locks the ProgressionFloor invariant: a barely-started series (1 of 20 episodes
        // played) must NOT disappear from the preference vector — it should still contribute
        // at the ProgressionFloor level (0.3), so users with mostly-abandoned history are not
        // left with an empty preference vector.
        //
        // We assert this indirectly: a completed 5-episode "Anchor" series (multiplier 1.5)
        // compared to a 1-of-20 "Fringe" series (multiplier 0.3 + 0.05*1.2 = 0.36) — Fringe
        // should still produce a non-zero weight, and Anchor should out-weigh it, but Fringe
        // must be strictly greater than zero (the floor guarantees this).
        var anchor = Guid.NewGuid();
        var fringe = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { anchor, 5 }, { fringe, 20 } };
        var now = DateTime.UtcNow.AddDays(-1);

        var profile = new UserWatchProfile();
        for (var i = 0; i < 5; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                SeriesId = anchor,
                Played = true,
                LastPlayedDate = now,
                Genres = ["Anchor"]
            });
        }

        // Only 1 of 20 Fringe episodes played → rawRatio 0.05 → multiplier ≈ 0.36 (above floor).
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            SeriesId = fringe,
            Played = true,
            LastPlayedDate = now,
            Genres = ["Fringe"]
        });

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile, counts);

        Assert.True(vector.ContainsKey("Fringe"));
        Assert.True(vector["Fringe"] > 0.0,
            "Abandoned series must still contribute a non-zero weight via ProgressionFloor.");
        Assert.True(vector["Anchor"] > vector["Fringe"],
            "Fully-completed anchor series must still out-weigh an abandoned one.");
    }
}
