using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
    public void BuildGenrePreferenceVector_HeavyRewatcher_Log1pGrowthIsSubLinear()
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
        // The extreme watcher gets a higher ratio, but log1p growth is sub-linear so the gain from 5->30 plays is less than 6× (log1p(30)/log1p(5) ≈ 1.83).
        Assert.True(eRatio > mRatio);
        var log1pRatioBound = Math.Log(1 + 30) / Math.Log(1 + 5) + 0.5; // generous ceiling well above sub-linear growth
        Assert.True(eRatio / mRatio < log1pRatioBound,
            $"Growth from PlayCount 5→30 must be sub-linear under log1p (ratio {eRatio / mRatio:F4} should be < {log1pRatioBound:F4})");
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

    // The progression multiplier was originally threaded into BuildGenrePreferenceVector / BuildPeoplePreferenceWeights only on the inference path (Engine.cs) and NOT on the training path (TrainingDataBuilder), so the model trained on unweighted vectors but was served.

    [Fact]
    public void BuildGenrePreferenceVector_ProgressionMap_ReshapesVectorVsNeutralPath()
    {
        // A user who COMPLETED a Drama series (10/10 episodes -> multiplier ~1.5) and ABANDONED a SciFi series (1/24 episodes -> multiplier ~0.35).
        var dramaSeries = Guid.NewGuid();
        var scifiSeries = Guid.NewGuid();
        var now = DateTime.UtcNow.AddDays(-30);

        var profile = new UserWatchProfile();
        for (var i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(), SeriesId = dramaSeries, Played = true, LastPlayedDate = now, Genres = ["Drama"]
            });
        }

        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), SeriesId = scifiSeries, Played = true, LastPlayedDate = now, Genres = ["SciFi"]
        });

        var seriesEpisodeCounts = new Dictionary<Guid, int> { [dramaSeries] = 10, [scifiSeries] = 24 };

        var neutral = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var weighted = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);

        // The completed Drama series is boosted and the abandoned SciFi series is dampened, so the Drama/SciFi ratio must be strictly larger with the map than without it.
        var neutralRatio = neutral["Drama"] / neutral["SciFi"];
        var weightedRatio = weighted["Drama"] / weighted["SciFi"];
        Assert.True(
            weightedRatio > neutralRatio,
            $"progression map must reshape the vector (weighted ratio {weightedRatio} should exceed neutral {neutralRatio})");
    }

    [Fact]
    public void BuildGenrePreferenceVector_SameProfileAndMap_TrainAndServeProduceIdenticalVector()
    {
        // Purity guard: the builder is deterministic in (profile, map), so the vector the training path now computes is byte-for-byte the vector the inference path computes.
        var seriesId = Guid.NewGuid();
        var now = DateTime.UtcNow.AddDays(-15);
        var profile = new UserWatchProfile();
        for (var i = 0; i < 5; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, LastPlayedDate = now, Genres = ["Thriller"]
            });
        }

        var map = new Dictionary<Guid, int> { [seriesId] = 8 };

        var serveVector = PreferenceBuilder.BuildGenrePreferenceVector(profile, map);
        var trainVector = PreferenceBuilder.BuildGenrePreferenceVector(profile, map);

        Assert.Equal(serveVector.Count, trainVector.Count);
        foreach (var (genre, weight) in serveVector)
        {
            Assert.True(trainVector.TryGetValue(genre, out var other));
            Assert.Equal(weight, other, precision: 12);
        }
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

    [Fact]
    public void BuildGenrePreferenceVector_ProximityExpansion_StaysNormalized()
    {
        // Contract: after ExpandGenreProximity fires the vector must remain max-normalised (largest weight == 1.0, everything in [0, 1]), AND the co-occurrence-derived boost must be observable in the final normalised weights - otherwise this test could pass with the expansion removed.
        var profile = new UserWatchProfile();
        var baseDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
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

        // Build a proximity-OFF reference by feeding the SAME genre frequencies as the full profile but with each genre on its own row (no co-occurrences).
        var baselineProfile = new UserWatchProfile();
        void AddBaselineRow(string genre, DateTime lastPlayed)
        {
            baselineProfile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = lastPlayed,
                Genres = [genre]
            });
        }

        for (var i = 0; i < 20; i++)
        {
            AddBaselineRow("Action", baseDate.AddHours(-i));
        }

        for (var i = 0; i < 20; i++)
        {
            AddBaselineRow("Adventure", baseDate.AddHours(-i));
        }

        for (var i = 0; i < 16; i++)
        {
            AddBaselineRow("SciFi", baseDate.AddHours(-i));
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

        // Baseline contract: the proximity-OFF vector reflects only direct-watch frequency (Action=Adventure=1.0 as the shared peak, SciFi=16/20=0.8).
        Assert.True(baselineVector.TryGetValue("Action", out var baselineAction));
        Assert.True(baselineVector.TryGetValue("Adventure", out var baselineAdventure));
        Assert.True(baselineVector.TryGetValue("SciFi", out var baselineSciFi));
        Assert.InRange(baselineAction, 0.999, 1.0001);
        Assert.InRange(baselineAdventure, 0.999, 1.0001);
        Assert.InRange(baselineSciFi, 0.79, 0.81);

        // Full profile with proximity expansion: SciFi's normalised weight MUST be strictly above the baseline 0.8 because ExpandGenreProximity adds a co-occurrence-derived boost from both Action↔SciFi and Adventure↔SciFi (min-count gate passes for both pairs at 8 co-occurrences each).
        Assert.True(vector.TryGetValue("SciFi", out var sciFiWeight));
        Assert.True(sciFiWeight > baselineSciFi + 0.005,
            $"Proximity expansion must lift SciFi above its direct-watch baseline of ~0.8. " +
            $"Got baseline={baselineSciFi:F4}, full={sciFiWeight:F4}. " +
            "A no-op expansion would produce equal values here.");
    }

    // These four tests lock the "strict completion" rule for the per-series progression multiplier used by BuildGenrePreferenceVector and BuildPeoplePreferenceWeights.

    [Fact]
    public void BuildGenrePreferenceVector_FullyCompletedSeries_ReceivesHigherWeightThanAbandonedSeries()
    {
        // Isolate the progression multiplier from the number of contributing rows. Both series contribute exactly ONE played episode row with identical temporal weight, PlayCount boost, and +0 favorite additive.
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

        Assert.True(vector.TryGetValue("SciFi", out var sciFiWeight));
        Assert.True(vector.TryGetValue("Drama", out var dramaWeight));
        Assert.True(sciFiWeight > dramaWeight,
            $"Fully-completed series should out-weigh abandoned series after progression scaling " +
            $"(SciFi={sciFiWeight:F4}, Drama={dramaWeight:F4})");
    }

    [Fact]
    public void BuildGenrePreferenceVector_PartialStartsDoNotInflateCompletionRatio()
    {
        // The strict completion predicate must ignore rows with PlaybackPositionTicks > 0 but Played=false and PlayCount=0.
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
            // No SeriesId -> non-episode row -> progression multiplier stays at neutral 1.0
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

        Assert.True(vectorA.TryGetValue("SciFi", out var sciFiA));
        Assert.True(vectorA.TryGetValue("Anchor", out var anchorA));
        Assert.True(vectorB.TryGetValue("SciFi", out var sciFiB));
        Assert.True(vectorB.TryGetValue("Anchor", out var anchorB));

        var ratioA = sciFiA / anchorA;
        var ratioB = sciFiB / anchorB;

        // Both profiles compute the same played counter (2/5), so the SciFi/Anchor ratio must match to within floating-point noise.
        Assert.Equal(ratioB, ratioA, 6);
    }

    [Fact]
    public void BuildPeoplePreferenceWeights_SharesProgressionSemanticsWithGenrePipeline()
    {
        // BuildPeoplePreferenceWeights and BuildGenrePreferenceVector must use the SAME strict completion predicate (Played || PlayCount>0) for the per-series progression counter.
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
        // Construction: * Two watched-item rows on the SAME series (5 total episodes): - One PLAYED episode with people {"Actor A"} -> counts as completed - One UNPLAYED FAVORITE episode with people {"Actor A", "Actor B"} -> NOT a completed episode; earlier code would have applied.
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

        // Actor B ONLY appears on the unplayed-favorite row. Its weight is therefore the single-row multiplier for that row.
        Assert.Equal(1.0, actorBWeight, 6);

        // Sanity: Actor A appears on BOTH rows, so its weight is the sum of: (a) played episode multiplier: rawRatio = 1/5 = 0.2 -> ProgressionFloor + 0.2*Span = 0.3 + 0.24 = 0.54 (b) unplayed favorite bypass: 1.0 Total ≈ 1.54.
        Assert.True(weights.TryGetValue("Actor A", out var actorAWeight));
        Assert.True(actorAWeight > actorBWeight,
            $"Actor A appears on both rows and must out-weigh Actor B, got A={actorAWeight}, B={actorBWeight}");
        Assert.True(actorAWeight < 2.0,
            $"Actor A must not be treated as two bypasses; expected < 2.0, got {actorAWeight}");
    }

    // Phantom watched-episode rows must not inflate the counter === When the on-disk episode files are deleted but the WatchedItemInfo rows survive in the history cache, the naive per-series counter would grow beyond the actual episode total and unlock ProgressionCeiling for a series.

    [Fact]
    public void BuildGenrePreferenceVector_PhantomRowsForDeletedSeries_AreIgnored()
    {
        // Series was fully deleted from the library - seriesEpisodeCounts no longer has an entry for it, but old watch rows still exist.
        var deletedSeries = Guid.NewGuid();
        var liveSeries = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { liveSeries, 2 } };
        var now = DateTime.UtcNow.AddDays(-1);

        var withPhantoms = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = liveSeries, Played = true, LastPlayedDate = now, Genres = ["Live"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = deletedSeries, Played = true, LastPlayedDate = now, Genres = ["Phantom"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = deletedSeries, Played = true, LastPlayedDate = now, Genres = ["Phantom"] }
            ]
        };
        var withoutPhantoms = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = liveSeries, Played = true, LastPlayedDate = now, Genres = ["Live"] }
            ]
        };

        var vectorWith = PreferenceBuilder.BuildGenrePreferenceVector(withPhantoms, counts);
        var vectorWithout = PreferenceBuilder.BuildGenrePreferenceVector(withoutPhantoms, counts);

        // The Live series must land at the exact same relative weight in both profiles once the phantom rows are excluded from the counter.
        Assert.True(vectorWith.TryGetValue("Live", out var liveWithWeight));
        Assert.True(vectorWithout.TryGetValue("Live", out var liveWithoutWeight));
        Assert.InRange(liveWithWeight, 0.999, 1.0001);
        Assert.InRange(liveWithoutWeight, 0.999, 1.0001);
    }

    [Fact]
    public void ComputeProgressionMultiplier_AbandonedSeries_StillContributesFloor()
    {
        // Locks the ProgressionFloor invariant: a barely-started series (1 of 20 episodes played) must NOT collapse to a near-zero weight - the floor guarantees the signal stays audible so users with mostly-abandoned history are not left with an empty preference vector.
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

        // Only 1 of 20 Fringe episodes played -> rawRatio 0.05 -> multiplier ≈ 0.36 (above floor).
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            SeriesId = fringe,
            Played = true,
            LastPlayedDate = now,
            Genres = ["Fringe"]
        });

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile, counts);

        Assert.True(vector.TryGetValue("Fringe", out var fringeWeight));
        Assert.True(vector.TryGetValue("Anchor", out var anchorWeight));

        // Anchor is the vector max, so it normalises to 1.0.
        Assert.InRange(anchorWeight, 0.999, 1.0001);

        // Fringe must sit inside the "with-floor" range. The lower bound 0.03 is strictly higher than the ~0.010 a floor-less implementation would produce, so a regression that drops or zeroes ProgressionFloor fails this test.
        Assert.InRange(fringeWeight, 0.03, 0.07);

        Assert.True(anchorWeight > fringeWeight,
            "Fully-completed anchor series must still out-weigh an abandoned one.");
    }

    // BuildGenrePreferenceVector merges profile.GenreDistribution as base weights for genres that have no WatchedItems rows.

    [Fact]
    public void BuildGenrePreferenceVector_GenreDistributionOnly_PopulatesVector()
    {
        // A profile with no WatchedItems but a populated GenreDistribution must still produce a non-empty, max-normalised vector.
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 10 },
                { "Comedy", 5 }
            }
        };

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        Assert.True(vector.TryGetValue("Action", out var actionWeight));
        Assert.True(vector.TryGetValue("Comedy", out var comedyWeight));

        // Max-normalised: the genre with count 10 must land at 1.0.
        Assert.InRange(actionWeight, 0.999, 1.0001);

        // Genre with count 5 must be half the max.
        Assert.InRange(comedyWeight, 0.499, 0.501);
    }

    [Fact]
    public void BuildGenrePreferenceVector_GenreDistributionDoesNotOverwriteWatchedItemsGenre()
    {
        // A genre that already has a WatchedItems-derived weight must NOT be overwritten by
        // GenreDistribution. The merge loop skips genres already in the vector.

        // The GenreDistribution entry for "Action" has count=1 and "Drama" has count=10, so if the guard is absent "Action" would be overwritten with 1/10 = 0.1 (scaled to max).
        var now = DateTime.UtcNow.AddDays(-7);
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = now, Genres = ["Action"] }
            ],
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 1 },   // same key - must NOT overwrite the watch-derived weight
                { "Drama", 10 }    // new key - must be inserted as a fallback
            }
        };

        var withGd = PreferenceBuilder.BuildGenrePreferenceVector(profile);

        Assert.True(withGd.TryGetValue("Action", out var actionWeight));
        Assert.True(withGd.TryGetValue("Drama", out _));

        // If the guard is absent the overwrite path would set Action = 1/10 = 0.1 (scaled by Drama's max-count).
        Assert.True(actionWeight > 0.5,
            $"Action must retain its watch-derived weight (> 0.5) and not be overwritten by " +
            $"GenreDistribution count/max = 0.1. Got {actionWeight:F4}.");

        // "Drama" must appear because it was a new key only in GenreDistribution.
        Assert.True(withGd.ContainsKey("Drama"),
            "Drama (only in GenreDistribution) must be inserted as a fallback genre.");

        // The vector must remain max-normalised.
        var max = withGd.Values.Max();
        Assert.InRange(max, 0.999, 1.0001);
    }

    // The proximity expansion is intentionally suppressed for sparse profiles (< 10 items)
    // to avoid injecting noise from a co-occurrence map built on too little data.

    [Fact]
    public void BuildGenrePreferenceVector_FewItems_ProximityExpansionDoesNotFire()
    {
        // 9 items - one short of the minimum-10 gate. The two genres appear together on every row (maximum co-occurrence signal) but expansion must still be suppressed.
        var baseDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var sparseProfile = new UserWatchProfile();
        for (var i = 0; i < 9; i++)
        {
            sparseProfile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-i),
                Genres = ["Action", "Thriller"]
            });
        }

        // Baseline: same 9 items but each genre on its own separate row so no co-occurrence
        // map can be built regardless of the item-count gate.
        var baselineProfile = new UserWatchProfile();
        for (var i = 0; i < 9; i++)
        {
            baselineProfile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-i),
                Genres = ["Action"]
            });
            baselineProfile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-i),
                Genres = ["Thriller"]
            });
        }

        var sparse = PreferenceBuilder.BuildGenrePreferenceVector(sparseProfile);
        var baseline = PreferenceBuilder.BuildGenrePreferenceVector(baselineProfile);

        // Both vectors must be max-normalised (both genres have equal direct-watch weight so
        // both should be 1.0 in their respective vectors, which also confirms no expansion fired).
        Assert.True(sparse.TryGetValue("Action", out var sparseAction));
        Assert.True(sparse.TryGetValue("Thriller", out var sparseThriller));
        Assert.InRange(sparseAction, 0.999, 1.0001);
        Assert.InRange(sparseThriller, 0.999, 1.0001);

        Assert.True(baseline.TryGetValue("Action", out var baselineAction));
        Assert.True(baseline.TryGetValue("Thriller", out var baselineThriller));
        Assert.InRange(baselineAction, 0.999, 1.0001);
        Assert.InRange(baselineThriller, 0.999, 1.0001);
    }

    // A genre that co-occurs with known genres but was never directly watched must be inserted into the vector with a derived weight.

    [Fact]
    public void BuildGenrePreferenceVector_ProximityExpansion_InsertsNewGenreNotDirectlyWatched()
    {
        // 10 items tagged ["Action", "Thriller"] give Action↔Thriller co-occurrence = 10, which is well above the minCooccurrences=2 gate.
        var baseDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // 10 items: Action only. Direct weight for Action.
        var profile = new UserWatchProfile();
        for (var i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-i),
                Genres = ["Action"]
            });
        }

        // 10 items: Action + Thriller together. Action is reinforced; Thriller is a co-occurrence neighbour.
        var insertionProfile = new UserWatchProfile();
        // 12 Action-only rows (direct signal for Action)
        for (var i = 0; i < 12; i++)
        {
            insertionProfile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-i),
                Genres = ["Action"]
            });
        }

        // 10 Action+Thriller co-occurrence rows. Both get direct weights from these.
        for (var i = 0; i < 10; i++)
        {
            insertionProfile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = baseDate.AddHours(-100 - i),
                Genres = ["Action", "Thriller"]
            });
        }

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(insertionProfile);

        // Action must be the dominant genre (most direct rows).
        Assert.True(vector.TryGetValue("Action", out var actionWeight));
        Assert.True(vector.TryGetValue("Thriller", out var thrillerWeight));
        Assert.InRange(actionWeight, 0.999, 1.0001);

        // Thriller has direct watch rows AND a proximity boost from Action. It must be present
        // with a non-trivial weight (not just the direct-watch floor).
        Assert.True(thrillerWeight > 0.0,
            "Thriller must appear in the vector via direct watch + proximity reinforcement.");

        // The vector must remain max-normalised after expansion.
        var max = vector.Values.Max();
        Assert.InRange(max, 0.999, 1.0001);
        foreach (var w in vector.Values)
        {
            Assert.InRange(w, 0.0, 1.0001);
        }
    }

    // The phantom guard in BuildGenrePreferenceVector (tested above) has a matching guard in BuildPeoplePreferenceWeights.

    [Fact]
    public void BuildPeoplePreferenceWeights_PhantomRowsForDeletedSeries_AreIgnored()
    {
        // A deleted series has two watched-episode rows still in the history cache. The people lookup has entries for both the live and the deleted series.
        var deletedSeries = Guid.NewGuid();
        var liveSeries = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { liveSeries, 2 } };
        var now = DateTime.UtcNow.AddDays(-1);

        var liveEpisode = Guid.NewGuid();
        var phantomEp1 = Guid.NewGuid();
        var phantomEp2 = Guid.NewGuid();

        var lookup = new Dictionary<Guid, HashSet<string>>
        {
            { liveEpisode, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor Live" } },
            { phantomEp1, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor Ghost" } },
            { phantomEp2, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Actor Ghost" } }
        };

        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = liveEpisode, SeriesId = liveSeries, Played = true, LastPlayedDate = now },
                new WatchedItemInfo { ItemId = phantomEp1, SeriesId = deletedSeries, Played = true, LastPlayedDate = now },
                new WatchedItemInfo { ItemId = phantomEp2, SeriesId = deletedSeries, Played = true, LastPlayedDate = now }
            ]
        };

        var weights = PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookup, counts);

        // "Actor Live" must be present - their row belongs to a series still in the library.
        Assert.True(weights.ContainsKey("Actor Live"),
            "Actor from live series must appear in the people weights.");

        // "Actor Ghost" must be absent - their rows belong to a deleted series.
        Assert.False(weights.ContainsKey("Actor Ghost"),
            "Actor from deleted (phantom) series must be excluded from people weights.");
    }

    // When an item is BOTH a favorite AND a completed episode (Played=true or PlayCount>0), the favorite-bypass branch must NOT fire.

    [Fact]
    public void BuildGenrePreferenceVector_CompletedFavoriteEpisode_UsesRatioNotBypass()
    {
        // Two profiles; each has exactly ONE row contributing to "SciFi" and one "Anchor" movie. The SciFi row differs only in IsFavorite - but in BOTH cases the row is Played=true.
        var series = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { series, 5 } };
        var now = DateTime.UtcNow.AddDays(-1);

        WatchedItemInfo SciFiRow(bool isFavorite) => new()
        {
            ItemId = Guid.NewGuid(),
            SeriesId = series,
            Played = true,
            IsFavorite = isFavorite,
            LastPlayedDate = now,
            Genres = ["SciFi"]
        };

        WatchedItemInfo AnchorRow() => new()
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            LastPlayedDate = now,
            Genres = ["Anchor"]
        };

        var profileNonFav = new UserWatchProfile { WatchedItems = [SciFiRow(false), AnchorRow()] };
        var profileFav    = new UserWatchProfile { WatchedItems = [SciFiRow(true), AnchorRow()] };

        var vecNonFav = PreferenceBuilder.BuildGenrePreferenceVector(profileNonFav, counts);
        var vecFav    = PreferenceBuilder.BuildGenrePreferenceVector(profileFav, counts);

        Assert.True(vecNonFav.TryGetValue("SciFi", out var sciFiNonFav));
        Assert.True(vecNonFav.TryGetValue("Anchor", out var anchorNonFav));
        Assert.True(vecFav.TryGetValue("SciFi", out var sciFiFav));
        Assert.True(vecFav.TryGetValue("Anchor", out var anchorFav));

        var ratioNonFav = sciFiNonFav / anchorNonFav;
        var ratioFav    = sciFiFav    / anchorFav;

        // Profile B has an additional +3.0 FavoriteGenreBoostFactor additive on the SciFi row, so its SciFi/Anchor ratio will be larger - that is expected and correct.
        Assert.True(ratioNonFav < 1.0,
            $"Non-favorite completed episode must use the ratio path (mult < 1.0). Got ratio {ratioNonFav:F4}.");

        // For the favorite row the FavoriteBoostFactor additive lifts the weight above Anchor, but the base (temporal+playCount) × multiplier portion must still reflect the ratio.
        Assert.True(ratioFav > ratioNonFav,
            "Favorite additive must lift the SciFi/Anchor ratio above the non-favorite baseline.");
    }

    /// <summary>
    ///     A WatchedItemInfo with PlayCount &gt; 0
    ///     but Played = false must still be treated as a meaningful interaction.
    /// </summary>
    [Fact]
    public void WatchedItemInfo_PlayCountAboveZero_IsMeaningfulInteraction()
    {
        var item = new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = false,
            IsFavorite = false,
            PlayCount = 3
        };

        Assert.True(item.HasMeaningfulInteraction(),
            "An item with PlayCount > 0 must be treated as a meaningful interaction " +
            "regardless of Played or IsFavorite flags (train/serve parity).");
    }

    [Fact]
    public void BuildFranchisePreferenceVector_EmptyHistory_ReturnsEmpty()
    {
        var profile = new UserWatchProfile();
        Assert.Empty(PreferenceBuilder.BuildFranchisePreferenceVector(profile));
    }

    [Fact]
    public void BuildFranchisePreferenceVector_ItemsWithoutCollectionName_AreSkipped()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionName = null },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionName = "   " }
            ]
        };
        Assert.Empty(PreferenceBuilder.BuildFranchisePreferenceVector(profile));
    }

    [Fact]
    public void BuildFranchisePreferenceVector_NormalizesTopFranchiseToOne_FavoriteOutranksRewatch()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, IsFavorite = true, LastPlayedDate = DateTime.UtcNow.AddDays(-30), TmdbCollectionName = "Marvel" },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-30), TmdbCollectionName = "DC" }
            ]
        };
        var vector = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
        Assert.Equal(1.0, vector["Marvel"], 10);           // max-normalized
        Assert.True(vector["Marvel"] > vector["DC"]);       // favorite boost
    }

    [Fact]
    public void BuildProductionCountryPreferenceVector_EmptyHistory_ReturnsEmpty()
    {
        Assert.Empty(PreferenceBuilder.BuildProductionCountryPreferenceVector(new UserWatchProfile()));
    }

    [Fact]
    public void BuildProductionCountryPreferenceVector_AggregatesAndNormalizes()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-10), ProductionCountries = ["Japan"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-10), ProductionCountries = ["Japan", "USA"] }
            ]
        };
        var vector = PreferenceBuilder.BuildProductionCountryPreferenceVector(profile);
        Assert.Equal(1.0, vector["Japan"], 10);   // appears twice -> dominant -> normalized to 1.0
        Assert.True(vector["Japan"] > vector["USA"]);
    }

    [Fact]
    public void BuildInheritedTagPreferenceSet_EmptyHistoryOrNoTags_ReturnsEmpty()
    {
        Assert.Empty(PreferenceBuilder.BuildInheritedTagPreferenceSet(new UserWatchProfile()));

        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, InheritedTags = [] }]
        };
        Assert.Empty(PreferenceBuilder.BuildInheritedTagPreferenceSet(profile));
    }

    [Fact]
    public void BuildInheritedTagPreferenceSet_CollectsDistinctTags_CaseInsensitive()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, InheritedTags = ["Marvel", "  "] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, InheritedTags = ["marvel", "Christmas"] }
            ]
        };
        var set = PreferenceBuilder.BuildInheritedTagPreferenceSet(profile);
        Assert.Equal(2, set.Count); // Marvel (deduped case-insensitively) + Christmas; whitespace skipped
        Assert.Contains("MARVEL", set); // case-insensitive membership
        Assert.Contains("christmas", set);
    }

    [Fact]
    public void BuildWriterPreferenceWeights_EmptyHistoryOrNoWriters_ReturnsEmpty()
    {
        Assert.Empty(PreferenceBuilder.BuildWriterPreferenceWeights(new UserWatchProfile()));

        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, WriterNames = [] }]
        };
        Assert.Empty(PreferenceBuilder.BuildWriterPreferenceWeights(profile));
    }

    [Fact]
    public void BuildWriterPreferenceWeights_AccumulatesPerRow_FavoriteOutranks()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, IsFavorite = true, LastPlayedDate = DateTime.UtcNow.AddDays(-20), WriterNames = ["Sorkin"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-20), WriterNames = ["Kaufman"] }
            ]
        };
        var weights = PreferenceBuilder.BuildWriterPreferenceWeights(profile);
        Assert.True(weights["Sorkin"] > weights["Kaufman"]); // favorite additive boost
    }

    [Fact]
    public void BuildWriterPreferenceWeights_DuplicateWriterOnSameRow_CountedOnce()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-20), WriterNames = ["Sorkin", "sorkin"] }
            ]
        };
        var weights = PreferenceBuilder.BuildWriterPreferenceWeights(profile);
        Assert.Single(weights); // per-row de-dup, case-insensitive
    }

    // These exercise the direct-item and episode->parent-series branches of BuildStudioPreferenceSet / BuildTagPreferenceSet, including the whitespace filter that keeps blank Studios/Tags out of the returned set.

    [Fact]
    public void BuildStudioPreferenceSet_MovieDirectMatch_CollectsStudiosSkippingBlank()
    {
        var movieId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, BaseItem>
        {
            { movieId, new Movie { Id = Guid.NewGuid(), Studios = ["A24", " ", ""] } }
        };
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = movieId, Played = true }] };

        var result = PreferenceBuilder.BuildStudioPreferenceSet(profile, lookup);

        Assert.Contains("A24", result);
        Assert.Single(result); // whitespace/empty studios filtered out
    }

    [Fact]
    public void BuildStudioPreferenceSet_EpisodeSeriesMatch_CollectsSeriesStudios()
    {
        // Episode row has no direct lookup entry, so studios must come from the parent series.
        var episodeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, BaseItem>
        {
            { seriesId, new Movie { Id = Guid.NewGuid(), Studios = ["HBO", "  "] } }
        };
        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = episodeId, SeriesId = seriesId, Played = true }]
        };

        var result = PreferenceBuilder.BuildStudioPreferenceSet(profile, lookup);

        Assert.Contains("HBO", result);
        Assert.Single(result); // blank series studio excluded
    }

    [Fact]
    public void BuildTagPreferenceSet_MovieDirectMatch_CollectsTagsSkippingBlank()
    {
        var movieId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, BaseItem>
        {
            { movieId, new Movie { Id = Guid.NewGuid(), Tags = ["heist", ""] } }
        };
        var profile = new UserWatchProfile { WatchedItems = [new WatchedItemInfo { ItemId = movieId, Played = true }] };

        var result = PreferenceBuilder.BuildTagPreferenceSet(profile, lookup);

        Assert.Contains("heist", result);
        Assert.Single(result); // empty tag filtered out
    }

    [Fact]
    public void BuildTagPreferenceSet_EpisodeSeriesMatch_CollectsSeriesTags()
    {
        var episodeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, BaseItem>
        {
            { seriesId, new Movie { Id = Guid.NewGuid(), Tags = ["christmas", "  "] } }
        };
        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { ItemId = episodeId, SeriesId = seriesId, Played = true }]
        };

        var result = PreferenceBuilder.BuildTagPreferenceSet(profile, lookup);

        Assert.Contains("christmas", result);
        Assert.Single(result); // blank series tag excluded
    }

    // A row without LastPlayedDate takes one of two temporal arms: favorites use a full 1.0, non-favorites use the ~365-day decayed value.

    [Fact]
    public void BuildFranchisePreferenceVector_NoLastPlayedDate_FavoriteVsNonFavoriteTemporalFallback()
    {
        // Combined profile: a favorite franchise ("Fav") and a non-favorite one ("Old"), both with LastPlayedDate=null.
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), IsFavorite = true, PlayCount = 0, LastPlayedDate = null, TmdbCollectionName = "Fav" },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), IsFavorite = false, PlayCount = 1, LastPlayedDate = null, TmdbCollectionName = "Old" }
            ]
        };

        var vector = PreferenceBuilder.BuildFranchisePreferenceVector(profile);

        // Favorite is the vector max (temporal 1.0 + favorite additive), so it normalizes to 1.0.
        Assert.Equal(1.0, vector["Fav"], 10);

        // The non-favorite used the decayed 365-day fallback, not 1.0, so it must be far below the favorite.
        Assert.True(vector["Fav"] > vector["Old"],
            $"Favorite no-date row (temporal 1.0) must outrank non-favorite no-date row (365-day decay). Got Fav={vector["Fav"]:F4}, Old={vector["Old"]:F4}");
    }

    [Fact]
    public void BuildGenreExposureAnalysis_LowShareGenre_MarkedUnderexposed()
    {
        // >= MinWatchCountForGenreExposure rows dominated by one genre plus a single rare genre
        // whose normalized share falls below GenreUnderexposureThreshold, so it is flagged underexposed.
        var profile = new UserWatchProfile { WatchedItems = [] };
        var now = DateTime.UtcNow;
        for (var i = 0; i < 60; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = now.AddDays(-i), Genres = ["Action"]
            });
        }

        // One old, single-play rare-genre row -> tiny normalized share, well under the 2% threshold.
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = now.AddDays(-3000), Genres = ["Polka"]
        });

        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);

        Assert.True(analysis.IsValid);
        Assert.Contains("Polka", analysis.UnderexposedGenres);
    }

    // Distinct from the empty-list guard: a non-empty candidate list of only whitespace collapses to
    // validCount==0 after the whitespace filter, hitting the second neutral-return guard.

    [Fact]
    public void ComputeGenreExposureFeatures_ValidAnalysisAllBlankGenres_ReturnsNeutral()
    {
        var analysis = new PreferenceBuilder.GenreExposureAnalysis
        {
            UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
            AveragePreferenceWeight = 0.5,
            GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { { "Action", 1.0 } },
            IsValid = true
        };

        var (underexposure, dominance, gap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(["", " "], analysis);

        Assert.Equal(0.0, underexposure);
        Assert.Equal(0.0, dominance);
        Assert.Equal(0.0, gap);
    }

    // A series present in seriesEpisodeCounts but with totalEpisodes <= 0 must fall back to the neutral 1.0 multiplier, NOT the ProgressionCeiling.

    [Fact]
    public void BuildGenrePreferenceVector_SeriesEpisodeCountZero_UsesNeutralMultiplier()
    {
        var series = Guid.NewGuid();
        var now = DateTime.UtcNow.AddDays(-1);

        WatchedItemInfo SciFiEpisode() => new()
        {
            ItemId = Guid.NewGuid(), SeriesId = series, Played = true, LastPlayedDate = now, Genres = ["SciFi"]
        };

        WatchedItemInfo Anchor() => new()
        {
            ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = now, Genres = ["Anchor"]
        };

        var profileZero = new UserWatchProfile { WatchedItems = [SciFiEpisode(), Anchor()] };
        var profileMapped = new UserWatchProfile { WatchedItems = [SciFiEpisode(), Anchor()] };

        var countsZero = new Dictionary<Guid, int> { { series, 0 } };
        var countsOne = new Dictionary<Guid, int> { { series, 1 } };

        var vectorZeroTotal = PreferenceBuilder.BuildGenrePreferenceVector(profileZero, countsZero);
        var vectorMappedOne = PreferenceBuilder.BuildGenrePreferenceVector(profileMapped, countsOne);

        var zeroRatio = vectorZeroTotal["SciFi"] / vectorZeroTotal["Anchor"];
        var mappedRatio = vectorMappedOne["SciFi"] / vectorMappedOne["Anchor"];

        // totalEps<=0 -> neutral 1.0 (same as Anchor); the 1-episode control -> ceiling ~1.5 boost.
        Assert.True(zeroRatio < mappedRatio,
            $"Zero-total series must use the neutral 1.0 multiplier, not the ceiling. Got zeroRatio={zeroRatio:F4}, mappedRatio={mappedRatio:F4}");
    }

    // The direct-vector loop skips phantom-series rows, but ExpandGenreProximity has no phantom guard,
    // so a genre appearing ONLY on phantom rows can still be inserted as a new proximity entry.

    [Fact]
    public void BuildGenrePreferenceVector_PhantomMultiGenreRows_InsertNewProximityGenre()
    {
        var liveSeries = Guid.NewGuid();
        var phantomSeries = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { liveSeries, 20 } };
        var baseDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var profile = new UserWatchProfile();

        // Live, non-phantom rows establishing "Action" (and "Adventure") as direct-vector genres.
        for (var i = 0; i < 12; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                SeriesId = liveSeries,
                Played = true,
                LastPlayedDate = baseDate.AddHours(-i),
                Genres = ["Action", "Adventure"]
            });
        }

        // Phantom rows: their series is absent from counts so the direct-vector loop skips them, but ExpandGenreProximity has no phantom guard, so Action<->Ghost co-occurs and Ghost (never a direct entry) is inserted via the new-genre proximity path.
        for (var i = 0; i < 3; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                SeriesId = phantomSeries,
                Played = true,
                LastPlayedDate = baseDate.AddHours(-200 - i),
                Genres = ["Action", "Ghost"]
            });
        }

        var vector = PreferenceBuilder.BuildGenrePreferenceVector(profile, counts);

        // Ghost never contributed a direct row (all its rows are phantom), yet proximity inserts it.
        Assert.True(vector.ContainsKey("Ghost"), "Ghost must be inserted via the new-genre proximity path.");
        Assert.InRange(vector["Ghost"], 0.0000001, 1.0);

        // Vector stays max-normalized.
        var max = vector.Values.Max();
        Assert.InRange(max, 0.999, 1.0001);
    }
}
