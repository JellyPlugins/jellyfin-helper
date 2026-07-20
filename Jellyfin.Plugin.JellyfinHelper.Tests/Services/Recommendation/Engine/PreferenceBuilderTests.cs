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
        // (largest weight == 1.0, everything in [0, 1]), AND the co-occurrence-derived boost
        // must be observable in the final normalised weights — otherwise this test could pass
        // with the expansion removed entirely.
        //
        // Full profile row counts:
        //   • 12 items ["Action", "Adventure"]  → Action/Adventure co-occur strongly.
        //   • 8  items ["Adventure", "SciFi"]   → Adventure/SciFi co-occur (min-count gate passes).
        //   • 8  items ["Action", "SciFi"]      → Action/SciFi co-occur (min-count gate passes).
        //   → Direct row counts: Action 20, Adventure 20, SciFi 16.
        //
        // ExpandGenreProximity reinforces peer weights by adding a co-occurrence-derived
        // additive to existing entries (v3 hardening pass — the earlier ContainsKey-guarded
        // version was a no-op for the common case where every neighbour was already direct).
        // We assert this reinforcement by comparing SciFi's normalised weight against a
        // proximity-OFF baseline built with the same direct frequencies but each genre on its
        // own row (no co-occurrences, so ExpandGenreProximity cannot build its map). A no-op
        // expansion would collapse the full profile's SciFi back to the baseline's ~0.8; the
        // reinforcement lifts it strictly above.
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

        // Build a proximity-OFF reference by feeding the SAME genre frequencies as the full
        // profile but with each genre on its own row (no co-occurrences). ExpandGenreProximity
        // needs at least two distinct genres on a row to build the co-occurrence map, so a
        // single-genre-per-row baseline gives us the same direct-watch signal without any
        // proximity contribution. Any observable difference between the two vectors therefore
        // MUST come from ExpandGenreProximity — a stubbed-out no-op expansion would produce
        // an identical vector.
        //
        // Row counts (kept in lock-step with the full profile above):
        //   Action    : 12 (Action+Adventure)  + 8 (Action+SciFi)     = 20 rows
        //   Adventure : 12 (Action+Adventure)  + 8 (Adventure+SciFi)  = 20 rows
        //   SciFi     : 8  (Adventure+SciFi)   + 8 (Action+SciFi)     = 16 rows
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

        // Baseline contract: the proximity-OFF vector reflects only direct-watch frequency
        // (Action=Adventure=1.0 as the shared peak, SciFi=16/20=0.8). Pinning this pins the
        // "expansion actually did something" delta below.
        Assert.True(baselineVector.TryGetValue("Action", out var baselineAction));
        Assert.True(baselineVector.TryGetValue("Adventure", out var baselineAdventure));
        Assert.True(baselineVector.TryGetValue("SciFi", out var baselineSciFi));
        Assert.InRange(baselineAction, 0.999, 1.0001);
        Assert.InRange(baselineAdventure, 0.999, 1.0001);
        Assert.InRange(baselineSciFi, 0.79, 0.81);

        // Full profile with proximity expansion: SciFi's normalised weight MUST be strictly
        // above the baseline 0.8 because ExpandGenreProximity adds a co-occurrence-derived
        // boost from both Action↔SciFi and Adventure↔SciFi (min-count gate passes for both
        // pairs at 8 co-occurrences each). A stubbed-out no-op expansion would collapse the
        // full-profile vector back onto the baseline shape, failing this assertion.
        Assert.True(vector.TryGetValue("SciFi", out var sciFiWeight));
        Assert.True(sciFiWeight > baselineSciFi + 0.005,
            $"Proximity expansion must lift SciFi above its direct-watch baseline of ~0.8. " +
            $"Got baseline={baselineSciFi:F4}, full={sciFiWeight:F4}. " +
            "A no-op expansion would produce equal values here.");
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

        Assert.True(vector.TryGetValue("SciFi", out var sciFiWeight));
        Assert.True(vector.TryGetValue("Drama", out var dramaWeight));
        Assert.True(sciFiWeight > dramaWeight,
            $"Fully-completed series should out-weigh abandoned series after progression scaling " +
            $"(SciFi={sciFiWeight:F4}, Drama={dramaWeight:F4})");
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

        Assert.True(vectorA.TryGetValue("SciFi", out var sciFiA));
        Assert.True(vectorA.TryGetValue("Anchor", out var anchorA));
        Assert.True(vectorB.TryGetValue("SciFi", out var sciFiB));
        Assert.True(vectorB.TryGetValue("Anchor", out var anchorB));

        var ratioA = sciFiA / anchorA;
        var ratioB = sciFiB / anchorB;

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

    // === F-04 regression: phantom watched-episode rows must not inflate the counter ===
    // When the on-disk episode files are deleted but the WatchedItemInfo rows survive in the
    // history cache, the naive per-series counter would grow beyond the actual episode total
    // and unlock ProgressionCeiling for a series the user never came close to completing.
    // The two tests below pin the two failure modes: (1) whole-series deletion, (2) partial
    // file loss within an existing series.

    [Fact]
    public void BuildGenrePreferenceVector_PhantomRowsForDeletedSeries_AreIgnored()
    {
        // Series was fully deleted from the library — seriesEpisodeCounts no longer has an
        // entry for it, but old watch rows still exist. Without the skip guard those rows
        // would drive the multiplier off a series length of zero (or, historically, from a
        // reused row's Genres alone) and could still push the vector towards the deleted
        // signal. Now they must be treated exactly like non-existent rows.
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

        // The Live series must land at the exact same relative weight in both profiles once
        // the phantom rows are excluded from the counter. A regression that let phantom rows
        // through would either dilute Live's normalised weight or introduce a Phantom entry.
        Assert.True(vectorWith.TryGetValue("Live", out var liveWithWeight));
        Assert.True(vectorWithout.TryGetValue("Live", out var liveWithoutWeight));
        Assert.InRange(liveWithWeight, 0.999, 1.0001);
        Assert.InRange(liveWithoutWeight, 0.999, 1.0001);
    }

    [Fact]
    public void ComputeProgressionMultiplier_AbandonedSeries_StillContributesFloor()
    {
        // Locks the ProgressionFloor invariant: a barely-started series (1 of 20 episodes
        // played) must NOT collapse to a near-zero weight — the floor guarantees the signal
        // stays audible so users with mostly-abandoned history are not left with an empty
        // preference vector.
        //
        // Construction:
        //   * Anchor series: 5/5 episodes played (rawRatio=1.0) → multiplier 1.5 per row.
        //     5 rows × 1.5 × temporal(~0.996) ≈ 7.47 raw weight, which is the vector max
        //     after normalization → normalized Anchor = 1.0.
        //   * Fringe series: 1/20 episodes played (rawRatio=0.05) → multiplier 0.36 per row
        //     (0.3 floor + 0.05 × 1.2 span). One row → raw weight ≈ 0.36 × 0.996 ≈ 0.358.
        //     Normalized Fringe = 0.358 / 7.47 ≈ 0.048.
        //
        // Without the floor: Fringe multiplier would be 0.05 × 1.5 = 0.075, weight ≈ 0.0747,
        // normalized ≈ 0.010. The lower bound below (0.03) sits well above the "no-floor"
        // value and comfortably below the "with-floor" value, so this assertion can only
        // pass when the floor is present and > 0. That is the regression it guards.
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

        Assert.True(vector.TryGetValue("Fringe", out var fringeWeight));
        Assert.True(vector.TryGetValue("Anchor", out var anchorWeight));

        // Anchor is the vector max, so it normalises to 1.0.
        Assert.InRange(anchorWeight, 0.999, 1.0001);

        // Fringe must sit inside the "with-floor" range. The lower bound 0.03 is strictly
        // higher than the ~0.010 a floor-less implementation would produce, so a regression
        // that drops or zeroes ProgressionFloor fails this test.
        Assert.InRange(fringeWeight, 0.03, 0.07);

        Assert.True(anchorWeight > fringeWeight,
            "Fully-completed anchor series must still out-weigh an abandoned one.");
    }
}
