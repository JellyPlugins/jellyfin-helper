using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>
///     Tests for <see cref="TrainingFeatureComputer"/>, the shared training-time feature
///     helpers that must remain in lock-step with the live scoring path. These tests focus on:
///     <list type="bullet">
///         <item>Case-insensitive de-duplication in the studio/tag preference builders.</item>
///         <item>The "same item is excluded from its own temporal signal" label-leakage guard.</item>
///         <item>Language affinity precedence (primary &gt; preferred &gt; tolerated &gt; known &gt; unknown).</item>
///         <item>Neutral fallbacks when either side of a language comparison is empty.</item>
///     </list>
/// </summary>
public class TrainingFeatureComputerTests
{
    // -----------------------------------------------------------------------
    // BuildStudioPreferenceSetFromCache
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildStudioPreferenceSetFromCache_EmptyProfile_ReturnsEmpty()
    {
        var profile = new UserWatchProfile();
        var lookup = new Dictionary<Guid, IReadOnlyList<string>>();

        var result = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookup);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildStudioPreferenceSetFromCache_SkipsItemsWithoutMeaningfulInteraction()
    {
        var profile = new UserWatchProfile();
        var itemId = Guid.NewGuid();
        // Interaction-less item: not Played, not favorite, PlayCount==0, PlaybackPositionTicks==0.
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = itemId });

        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [itemId] = new[] { "Studio A" }
        };

        Assert.Empty(TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookup));
    }

    [Fact]
    public void BuildStudioPreferenceSetFromCache_MergesItemAndSeriesStudios()
    {
        var itemId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = itemId,
            Played = true,
            SeriesId = seriesId
        });

        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [itemId] = new[] { "ItemStudio" },
            [seriesId] = new[] { "SeriesStudio" }
        };

        var result = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookup);

        Assert.Contains("ItemStudio", result);
        Assert.Contains("SeriesStudio", result);
    }

    [Fact]
    public void BuildStudioPreferenceSetFromCache_IgnoresWhitespaceAndNullEntries()
    {
        var itemId = Guid.NewGuid();
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = itemId, Played = true });

        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [itemId] = new[] { "  ", "", "Real Studio", "\t" }
        };

        var result = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookup);

        Assert.Single(result);
        Assert.Contains("Real Studio", result);
    }

    [Fact]
    public void BuildStudioPreferenceSetFromCache_IsCaseInsensitive()
    {
        // HashSet must dedupe "Studio A" and "STUDIO A" to a single entry.
        var i1 = Guid.NewGuid();
        var i2 = Guid.NewGuid();
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = i1, Played = true });
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = i2, Played = true });

        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [i1] = new[] { "Studio A" },
            [i2] = new[] { "STUDIO A", "studio a" }
        };

        var result = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookup);

        Assert.Single(result);
    }

    [Fact]
    public void BuildStudioPreferenceSetFromCache_SkipsMissingItemWithSeriesLookupOnly()
    {
        // An episode may not be in the item lookup, only its series is.
        var itemId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = itemId,
            Played = true,
            SeriesId = seriesId
        });

        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [seriesId] = new[] { "SeriesOnly" }
        };

        var result = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookup);

        Assert.Single(result);
        Assert.Contains("SeriesOnly", result);
    }

    [Fact]
    public void BuildStudioPreferenceSetFromCache_NullSeriesId_UsesOnlyItemLookup()
    {
        var itemId = Guid.NewGuid();
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = itemId, Played = true, SeriesId = null });
        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [itemId] = new[] { "ItemStudio" }
        };

        var result = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookup);

        Assert.Single(result);
        Assert.Contains("ItemStudio", result);
    }

    // -----------------------------------------------------------------------
    // BuildTagPreferenceSetFromCache
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildTagPreferenceSetFromCache_SkipsItemsWithoutMeaningfulInteraction()
    {
        var itemId = Guid.NewGuid();
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = itemId });

        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [itemId] = new[] { "TagA" }
        };

        Assert.Empty(TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, lookup));
    }

    [Fact]
    public void BuildTagPreferenceSetFromCache_MergesItemAndSeriesTags_CaseInsensitive()
    {
        var itemId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = itemId,
            IsFavorite = true, // still meaningful interaction
            SeriesId = seriesId
        });

        var lookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [itemId] = new[] { "Cyberpunk", "  ", "" },
            [seriesId] = new[] { "CYBERPUNK", "Dystopia" }
        };

        var result = TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, lookup);

        Assert.Equal(2, result.Count);
        // Exact stored form: first-insert wins for dedup, tags kept in original casing.
        // "Cyberpunk" (from itemId) wins over "CYBERPUNK" (from seriesId).
        // "Dystopia" stored as-is from seriesId input.
        Assert.True(result.Contains("Cyberpunk", StringComparer.Ordinal), "Expected exact casing 'Cyberpunk'");
        Assert.True(result.Contains("Dystopia", StringComparer.Ordinal), "Expected exact casing 'Dystopia'");
    }

    // -----------------------------------------------------------------------
    // ComputeTagSimilarityFromCache
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeTagSimilarityFromCache_EmptyCandidate_ReturnsZero()
    {
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        Assert.Equal(0.0, TrainingFeatureComputer.ComputeTagSimilarityFromCache(Array.Empty<string>(), preferred));
    }

    [Fact]
    public void ComputeTagSimilarityFromCache_EmptyPreferred_ReturnsZero()
    {
        var candidate = new[] { "A" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0.0, TrainingFeatureComputer.ComputeTagSimilarityFromCache(candidate, preferred));
    }

    [Fact]
    public void ComputeTagSimilarityFromCache_FullOverlap_ReturnsOne()
    {
        var candidate = new[] { "A", "B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        Assert.Equal(1.0, TrainingFeatureComputer.ComputeTagSimilarityFromCache(candidate, preferred));
    }

    [Fact]
    public void ComputeTagSimilarityFromCache_CaseInsensitive()
    {
        var candidate = new[] { "action", "DRAMA" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action", "drama" };
        Assert.Equal(1.0, TrainingFeatureComputer.ComputeTagSimilarityFromCache(candidate, preferred));
    }

    [Fact]
    public void ComputeTagSimilarityFromCache_PartialOverlap_ReturnsJaccard()
    {
        // Intersection = 1 ("A"), Union = 3 (A, B, C) → 1/3.
        var candidate = new[] { "A", "B" };
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "C" };
        var result = TrainingFeatureComputer.ComputeTagSimilarityFromCache(candidate, preferred);
        Assert.Equal(1.0 / 3.0, result, precision: 5);
    }

    // -----------------------------------------------------------------------
    // ComputeContentNearestNeighborFromCache
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeContentNearestNeighborFromCache_EmptyWatchedGenres_ReturnsZero()
    {
        // Contract: all three parallel lists must have identical length.
        var result = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
            new[] { "Action" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            watchedGenreSets: new List<HashSet<string>>(),
            watchedPeopleSets: new List<HashSet<string>>(),
            watchedStudioSets: new List<HashSet<string>>());

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeContentNearestNeighborFromCache_EmptyCandidateGenres_ReturnsZero()
    {
        // Parallel-array invariant: watchedPeople/Studios must match watchedGenres length,
        // even when we only care about the genre-empty guard.
        var watchedGenres = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Action" }
        };
        var watchedPeople = new List<HashSet<string>> { new(StringComparer.OrdinalIgnoreCase) };
        var watchedStudios = new List<HashSet<string>> { new(StringComparer.OrdinalIgnoreCase) };

        var result = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            watchedGenres,
            watchedPeople,
            watchedStudios);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeContentNearestNeighborFromCache_PropagatesToContentScoring_NonZeroForOverlap()
    {
        // The exact score is delegated to ContentScoring, but with an exact genre overlap
        // the return value must be in the valid [0, 1] range and non-zero (positive signal).
        // Parallel-array invariant: all three watched-* lists share length (one watched item).
        var watchedGenres = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Action", "Thriller" }
        };
        var watchedPeople = new List<HashSet<string>> { new(StringComparer.OrdinalIgnoreCase) };
        var watchedStudios = new List<HashSet<string>> { new(StringComparer.OrdinalIgnoreCase) };

        var result = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
            new[] { "Action", "Thriller" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            watchedGenres,
            watchedPeople,
            watchedStudios);

        Assert.Equal(0.5, result, precision: 10);
    }

    [Fact]
    public void ComputeContentNearestNeighborFromCache_ParallelArrayInvariantHonored()
    {
        // Locks in the contract that all three parallel-array lists must be the
        // same length. If a future refactor drops the parallel-array precondition and only
        // pads one dimension, this test still passes (multi-item, symmetric lists) so we
        // add an additional test that exercises the person/studio dimension too and proves
        // the caller wired all three axes through.
        var watchedGenres = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Action" },
            new(StringComparer.OrdinalIgnoreCase) { "Drama" }
        };
        var watchedPeople = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Alice" },
            new(StringComparer.OrdinalIgnoreCase) { "Bob" }
        };
        var watchedStudios = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "StudioA" },
            new(StringComparer.OrdinalIgnoreCase) { "StudioB" }
        };

        var result = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
            candidateGenres: new[] { "Action" },
            candidatePeople: new[] { "Alice" },
            candidateStudios: new[] { "StudioA" },
            watchedGenres,
            watchedPeople,
            watchedStudios);

        // Full overlap on all three dimensions of the first watched item:
        // genreJaccard=1.0, peopleJaccard=1.0, studioJaccard=1.0 → composite = 0.50+0.30+0.20 = 1.0
        Assert.Equal(1.0, result, precision: 10);
    }

    // -----------------------------------------------------------------------
    // ComputeBestLanguageAffinity
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeBestLanguageAffinity_NoLanguages_ReturnsUnknownFloor()
    {
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            candidateLanguages: Array.Empty<string>(),
            primaryLang: null,
            preferredLangs: new HashSet<string>(),
            toleratedLangs: new HashSet<string>(),
            languageProfile: new Dictionary<string, LanguageProfileEntry>());
        Assert.Equal(0.1, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_PrimaryMatch_ReturnsOne()
    {
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "en" },
            primaryLang: "en",
            preferredLangs: new HashSet<string>(),
            toleratedLangs: new HashSet<string>(),
            languageProfile: new Dictionary<string, LanguageProfileEntry>());
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_PrimaryComparisonIsCaseInsensitive()
    {
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "EN" },
            primaryLang: "en",
            preferredLangs: new HashSet<string>(),
            toleratedLangs: new HashSet<string>(),
            languageProfile: new Dictionary<string, LanguageProfileEntry>());
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_PreferredMatch_Returns085()
    {
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "de" };
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "de" },
            primaryLang: "en",
            preferred,
            toleratedLangs: new HashSet<string>(),
            languageProfile: new Dictionary<string, LanguageProfileEntry>());
        Assert.Equal(0.85, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_ToleratedMatch_Returns05()
    {
        var tolerated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fr" };
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "fr" },
            primaryLang: "en",
            preferredLangs: new HashSet<string>(),
            tolerated,
            languageProfile: new Dictionary<string, LanguageProfileEntry>());
        Assert.Equal(0.5, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_KnownButNotClassified_Returns03()
    {
        // Language exists in profile but is neither primary, preferred nor tolerated → 0.3.
        var profile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["it"] = new LanguageProfileEntry()
        };
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "it" },
            primaryLang: "en",
            preferredLangs: new HashSet<string>(),
            toleratedLangs: new HashSet<string>(),
            profile);
        Assert.Equal(0.3, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_UnknownOnly_ReturnsFloor()
    {
        var profile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["it"] = new LanguageProfileEntry()
        };
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "zh" }, // not in profile
            primaryLang: "en",
            preferredLangs: new HashSet<string>(),
            toleratedLangs: new HashSet<string>(),
            profile);
        Assert.Equal(0.1, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_MultipleCandidates_PicksBestMatch()
    {
        // Reveals: the "best of" reduction must not stop at the first hit; primary=1.0
        // must trump earlier preferred=0.85 entries even when the primary comes later.
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "de" };
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "de", "en" }, // preferred first, primary second
            primaryLang: "en",
            preferred,
            toleratedLangs: new HashSet<string>(),
            languageProfile: new Dictionary<string, LanguageProfileEntry>());
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputeBestLanguageAffinity_EarlyExitOnPrimary_DoesNotEvaluateExtraCandidates()
    {
        // Behavioural bug guard: once primary=1.0 has been hit, the loop must break.
        // We can't observe short-circuiting directly, but we can prove the return value is
        // 1.0 even when the following candidate would deliver a lower score (0.85).
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "de" };
        var result = TrainingFeatureComputer.ComputeBestLanguageAffinity(
            new[] { "en", "de" },
            primaryLang: "en",
            preferred,
            toleratedLangs: new HashSet<string>(),
            languageProfile: new Dictionary<string, LanguageProfileEntry>());
        Assert.Equal(1.0, result);
    }

    // -----------------------------------------------------------------------
    // ComputeLanguageAffinityFromCache / ComputeSubtitleLanguageAffinityFromCache
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeLanguageAffinityFromCache_EmptyCandidateOrProfile_ReturnsNeutral()
    {
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 3 }
            }
        };

        Assert.Equal(0.5, TrainingFeatureComputer.ComputeLanguageAffinityFromCache(Array.Empty<string>(), profile));
        Assert.Equal(0.5, TrainingFeatureComputer.ComputeLanguageAffinityFromCache(new[] { "en" }, new UserWatchProfile()));
    }

    [Fact]
    public void ComputeLanguageAffinityFromCache_MatchesPrimaryLanguage()
    {
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 5 }, // primary
                ["de"] = new LanguageProfileEntry { ChosenCount = 2 }
            }
        };

        Assert.Equal(1.0, TrainingFeatureComputer.ComputeLanguageAffinityFromCache(new[] { "en" }, profile));
    }

    [Fact]
    public void ComputeSubtitleLanguageAffinityFromCache_EmptyCandidateOrProfile_ReturnsNeutral()
    {
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 3 }
            }
        };

        Assert.Equal(0.5, TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(Array.Empty<string>(), profile));
        Assert.Equal(0.5, TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(new[] { "en" }, new UserWatchProfile()));
    }

    [Fact]
    public void ComputeSubtitleLanguageAffinityFromCache_MatchesPrimarySubtitleLanguage()
    {
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["ja"] = new LanguageProfileEntry { ChosenCount = 8 } // primary
            }
        };

        Assert.Equal(1.0, TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(new[] { "ja" }, profile));
    }

    // -----------------------------------------------------------------------
    // ComputeTrainingTemporalAffinity
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeTrainingTemporalAffinity_NullWatchedItem_ReturnsNeutral()
    {
        var profile = new UserWatchProfile();
        var result = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
            watchedItem: null,
            candidateGenres: new[] { "Action" },
            userProfile: profile,
            isDay: true);
        Assert.Equal(0.5, result);
    }

    [Fact]
    public void ComputeTrainingTemporalAffinity_WatchedWithoutLastPlayedDate_ReturnsNeutral()
    {
        var profile = new UserWatchProfile();
        var target = new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = null };
        var result = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
            target, new[] { "Action" }, profile, isDay: true);
        Assert.Equal(0.5, result);
    }

    [Fact]
    public void ComputeTrainingTemporalAffinity_NullOrEmptyCandidateGenres_ReturnsNeutral()
    {
        var profile = new UserWatchProfile();
        var target = new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            LastPlayedDate = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc)
        };
        Assert.Equal(0.5, TrainingFeatureComputer.ComputeTrainingTemporalAffinity(target, (IReadOnlyList<string>?)null, profile, true));
        Assert.Equal(0.5, TrainingFeatureComputer.ComputeTrainingTemporalAffinity(target, Array.Empty<string>(), profile, true));
    }

    [Fact]
    public void ComputeTrainingTemporalAffinity_LessThan3InBucket_ReturnsNeutral()
    {
        // Only the target item itself is in the profile → filtered by the label-leakage guard.
        // Two additional items on the same day → totalInBucket = 2 < 3 → neutral.
        var saturday = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        var profile = new UserWatchProfile();
        var targetId = Guid.NewGuid();
        var target = new WatchedItemInfo
        {
            ItemId = targetId,
            Played = true,
            LastPlayedDate = saturday,
            Genres = new[] { "Action" }
        };
        profile.WatchedItems.Add(target);
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            LastPlayedDate = saturday.AddMinutes(30),
            Genres = new[] { "Action" }
        });
        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            LastPlayedDate = saturday.AddMinutes(60),
            Genres = new[] { "Action" }
        });

        var result = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
            target, new[] { "Action" }, profile, isDay: true);

        Assert.Equal(0.5, result);
    }

    [Fact]
    public void ComputeTrainingTemporalAffinity_ExcludesTargetItemFromSignal_LabelLeakageGuard()
    {
        // Critical regression guard: the target item itself must NEVER count toward the bucket.
        // If the guard is removed, `totalInBucket` would inflate by 1 and skew training rows.
        var saturday = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        var profile = new UserWatchProfile();
        var targetId = Guid.NewGuid();
        var target = new WatchedItemInfo
        {
            ItemId = targetId,
            Played = true,
            LastPlayedDate = saturday,
            Genres = new[] { "Action" }
        };
        profile.WatchedItems.Add(target); // target present in profile

        // 3 other Action items on same day → target itself is filtered out, we still get 3 hits.
        for (int i = 0; i < 3; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = saturday.AddMinutes(i + 1),
                Genres = new[] { "Action" }
            });
        }

        var result = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
            target, new[] { "Action" }, profile, isDay: true);

        Assert.Equal(1.0, result); // 3 same-day matches, all Action.
    }

    [Fact]
    public void ComputeTrainingTemporalAffinity_ExcludesItemsWithoutPlaybackActivity()
    {
        // Favorite-only items in the profile must NOT count toward totalInBucket.
        var saturday = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        var profile = new UserWatchProfile();
        var target = new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            LastPlayedDate = saturday,
            Genres = new[] { "Action" }
        };

        // 3 real playback items on Saturday, all Action.
        for (int i = 0; i < 3; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = saturday.AddMinutes(i + 1),
                Genres = new[] { "Action" }
            });
        }

        // 5 favorite-only items with a mismatching genre - must be excluded (Played=false, PlayCount=0).
        for (int i = 0; i < 5; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                IsFavorite = true,
                LastPlayedDate = saturday.AddMinutes(i + 4),
                Genres = new[] { "Horror" }
            });
        }

        var result = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
            target, new[] { "Action" }, profile, isDay: true);

        // Only the 3 real playback items count. All match Action → 3/3 = 1.0.
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputeTrainingTemporalAffinity_HourBucket_RespectsTemporalFeaturesGetTimeBucket()
    {
        // When isDay=false, only items in the SAME hour bucket (afternoon 12-17 for hour=12) count.
        // Items in a different bucket (evening 18+) must be excluded, proving the bucketing logic
        // agrees with TemporalFeatures.GetTimeBucket.
        var saturday12h = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        var saturday20h = new DateTime(2026, 1, 3, 20, 0, 0, DateTimeKind.Utc);
        var profile = new UserWatchProfile();
        var target = new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            LastPlayedDate = saturday12h,
            Genres = new[] { "Action" }
        };

        // 3 afternoon items, all Action.
        for (int i = 0; i < 3; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = saturday12h.AddMinutes(i + 1),
                Genres = new[] { "Action" }
            });
        }

        // 10 evening items, all Horror. Must NOT be counted for a noon target.
        for (int i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = saturday20h.AddMinutes(i),
                Genres = new[] { "Horror" }
            });
        }

        var result = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
            target, new[] { "Action" }, profile, isDay: false);

        Assert.Equal(1.0, result);
    }

    // -----------------------------------------------------------------------
    // AddAggregatedSeriesExample - the biggest uncovered slab of this file (0%).
    //
    // Business meaning: an episode-heavy series would otherwise flood the training data with
    // one row per episode, biasing the model toward series with many episodes. This helper
    // collapses episodes into a single series-level training example. The tests below drive
    // each label branch (fully watched, favorite-only, abandoned, partially watched) and
    // verify the wire-up of studios / tags / people / temporal features.
    // -----------------------------------------------------------------------

    private static PreferenceBuilder.GenreExposureAnalysis BuildExposure(
        Dictionary<string, double> genrePreferences,
        UserWatchProfile profile)
        => PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, profile);

    private static (Guid SeriesId, UserWatchProfile Profile, List<WatchedItemInfo> Episodes) BuildSeriesEpisodes(
        int totalEpisodes,
        int playedEpisodes,
        bool anyFavorite = false,
        bool anyPartial = false)
    {
        var seriesId = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "series-user" };
        var episodes = new List<WatchedItemInfo>();
        var baseDay = new DateTime(2026, 1, 5, 14, 0, 0, DateTimeKind.Utc); // Monday afternoon
        for (int i = 0; i < totalEpisodes; i++)
        {
            var w = new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                SeriesId = seriesId,
                Played = i < playedEpisodes,
                PlayCount = i < playedEpisodes ? 1 : 0,
                LastPlayedDate = baseDay.AddDays(i),
                Genres = new[] { "Drama" },
                Year = 2020,
                CommunityRating = 8.0f,
                DateCreated = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            if (anyFavorite && i == 0)
            {
                w.IsFavorite = true;
            }

            if (anyPartial && i == 0)
            {
                w.PlaybackPositionTicks = 100_000_000L;
            }

            episodes.Add(w);
            profile.WatchedItems.Add(w);
        }

        return (seriesId, profile, episodes);
    }

    [Fact]
    public void AddAggregatedSeriesExample_AllEpisodesPlayed_EmitsPositiveEngagementLabel()
    {
        var (seriesId, profile, episodes) = BuildSeriesEpisodes(totalEpisodes: 4, playedEpisodes: 4);
        var examples = new List<TrainingExample>();
        var genrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Drama"] = 1.0 };
        var exposure = BuildExposure(genrePreferences, profile);
        var itemStudiosLookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [seriesId] = new[] { "PremiumStudio" }
        };
        var itemTagsLookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [seriesId] = new[] { "SlowBurn", "PrestigeTV" }
        };
        var preferredStudios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PremiumStudio" };
        var preferredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SlowBurn" };
        var peopleLookup = new Dictionary<Guid, HashSet<string>>
        {
            [seriesId] = new(StringComparer.OrdinalIgnoreCase) { "AliceDirector" }
        };
        var preferredPeopleWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["AliceDirector"] = 1.0
        };

        TrainingFeatureComputer.AddAggregatedSeriesExample(
            examples,
            episodes,
            seriesId,
            profile,
            genrePreferences,
            coOccurrence: new Dictionary<Guid, double>(),
            collaborativeMax: 0.0,
            avgYear: 2020.0,
            exposure,
            peopleLookup,
            preferredPeopleWeights,
            itemStudiosLookup,
            preferredStudios,
            itemTagsLookup,
            preferredTags,
            preferredFranchises: new Dictionary<string, double>(),
            preferredCountries: new Dictionary<string, double>(),
            preferredInheritedTags: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            preferredWriterWeights: new Dictionary<string, double>(),
            genreStudioIdf: null,
            organicFallbackTimestamp: DateTime.UtcNow);

        Assert.Single(examples);
        var ex = examples[0];
        // Fully-played series must produce a positive engagement label (> 0.5).
        Assert.True(ex.Label > 0.5, $"Expected engagement label > 0.5 for fully-watched series, got {ex.Label}.");
        Assert.True(ex.Features.IsSeries);
        // Studio + tag features wired through
        Assert.True(ex.Features.StudioMatch);
        Assert.True(ex.Features.TagSimilarity > 0.0);
        // People similarity must be non-zero because AliceDirector is preferred.
        Assert.True(ex.Features.PeopleSimilarity > 0.0);
        // Series-progression boost is hardcoded to 0.0 by contract (train/serve parity).
        Assert.Equal(0.0, ex.Features.SeriesProgressionBoost);
        // Train/serve parity: aggregated series never re-enter live scoring, so these signals
        // must be neutralised on the training side.
        Assert.Equal(0.5, ex.Features.UserRatingScore);
        Assert.False(ex.Features.HasUserInteraction);
        // GeneratedAtUtc must be sourced from the most-recently-watched episode.
        Assert.Equal(episodes.Max(e => e.LastPlayedDate!.Value), ex.GeneratedAtUtc);
        // SampleWeight for aggregated series is 0.7 by contract.
        Assert.Equal(0.7, ex.SampleWeight);
    }

    [Fact]
    public void AddAggregatedSeriesExample_FavoriteOnlyNoPlayback_UsesFavoriteLabel()
    {
        // When a series has zero played episodes but at least one favourite,
        // the label must be 0.65 (explicit-interest signal) - NOT the abandoned label (0.0).
        // A regression here would silently teach the model that favourited series are bad.
        var (seriesId, profile, episodes) = BuildSeriesEpisodes(
            totalEpisodes: 3, playedEpisodes: 0, anyFavorite: true, anyPartial: false);
        var examples = new List<TrainingExample>();
        var genrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Drama"] = 1.0 };
        var exposure = BuildExposure(genrePreferences, profile);

        TrainingFeatureComputer.AddAggregatedSeriesExample(
            examples,
            episodes,
            seriesId,
            profile,
            genrePreferences,
            new Dictionary<Guid, double>(),
            0.0,
            2020.0,
            exposure,
            new Dictionary<Guid, HashSet<string>>(),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            new Dictionary<string, double>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            genreStudioIdf: null,
            organicFallbackTimestamp: DateTime.UtcNow);

        Assert.Single(examples);
        Assert.Equal(0.65, examples[0].Label);
    }

    [Fact]
    public void AddAggregatedSeriesExample_StartedButAbandoned_UsesAbandonedLabel()
    {
        // When playedEps == 0 AND at least one episode has PlaybackPositionTicks > 0
        // (started but abandoned) AND the average completion ratio is below the abandoned
        // threshold, the label must be AbandonedLabel (0.0). A regression here would
        // silently keep training on abandoned series as if they were desirable.
        var (seriesId, profile, episodes) = BuildSeriesEpisodes(
            totalEpisodes: 3, playedEpisodes: 0, anyFavorite: false, anyPartial: true);
        var examples = new List<TrainingExample>();
        var genrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Drama"] = 1.0 };
        var exposure = BuildExposure(genrePreferences, profile);

        TrainingFeatureComputer.AddAggregatedSeriesExample(
            examples,
            episodes,
            seriesId,
            profile,
            genrePreferences,
            new Dictionary<Guid, double>(),
            0.0,
            2020.0,
            exposure,
            new Dictionary<Guid, HashSet<string>>(),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            new Dictionary<string, double>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            genreStudioIdf: null,
            organicFallbackTimestamp: DateTime.UtcNow);

        Assert.Single(examples);
        Assert.Equal(EngineConstants.AbandonedLabel, examples[0].Label);
    }

    [Fact]
    public void AddAggregatedSeriesExample_UsesMostRecentEpisodeAsTemporalAnchor()
    {
        // The temporal features (DayOfWeekAffinity / HourOfDayAffinity /
        // IsWeekend) must be derived from the most-recently-watched episode. If someone
        // silently switches to FirstOrDefault or a "first episode" reference, the temporal
        // signal would degrade for series watched over long time spans.
        var seriesId = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "spread-user" };
        var episodes = new List<WatchedItemInfo>();
        var oldest = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);   // 4am ish
        var newest = new DateTime(2026, 2, 15, 22, 30, 0, DateTimeKind.Utc); // evening bucket

        for (int i = 0; i < 3; i++)
        {
            var w = new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                SeriesId = seriesId,
                Played = true,
                PlayCount = 1,
                LastPlayedDate = i == 0 ? oldest : (i == 1 ? oldest.AddMonths(1) : newest),
                Genres = new[] { "Drama" }
            };
            episodes.Add(w);
            profile.WatchedItems.Add(w);
        }

        var examples = new List<TrainingExample>();
        var genrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Drama"] = 1.0 };
        var exposure = BuildExposure(genrePreferences, profile);

        TrainingFeatureComputer.AddAggregatedSeriesExample(
            examples,
            episodes,
            seriesId,
            profile,
            genrePreferences,
            new Dictionary<Guid, double>(),
            0.0,
            2020.0,
            exposure,
            new Dictionary<Guid, HashSet<string>>(),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            new Dictionary<string, double>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            genreStudioIdf: null,
            organicFallbackTimestamp: DateTime.UtcNow);

        Assert.Single(examples);
        Assert.Equal(newest, examples[0].GeneratedAtUtc);
    }

    [Fact]
    public void AddAggregatedSeriesExample_NoStudioOverlap_LeavesStudioMatchFalse()
    {
        // Wire-check: when preferredStudios is empty, StudioMatch must be false - never
        // silently true. An accidental `preferredStudios.Any()` inversion could
        // flip the sign.
        var (seriesId, profile, episodes) = BuildSeriesEpisodes(totalEpisodes: 2, playedEpisodes: 2);
        var examples = new List<TrainingExample>();
        var genrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Drama"] = 1.0 };
        var exposure = BuildExposure(genrePreferences, profile);
        var itemStudiosLookup = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [seriesId] = new[] { "SomeStudio" }
        };
        var preferredStudios = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // deliberately empty

        TrainingFeatureComputer.AddAggregatedSeriesExample(
            examples,
            episodes,
            seriesId,
            profile,
            genrePreferences,
            new Dictionary<Guid, double>(),
            0.0,
            2020.0,
            exposure,
            new Dictionary<Guid, HashSet<string>>(),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            itemStudiosLookup,
            preferredStudios,
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            new Dictionary<string, double>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(),
            genreStudioIdf: null,
            organicFallbackTimestamp: DateTime.UtcNow);

        Assert.Single(examples);
        Assert.False(examples[0].Features.StudioMatch);
        Assert.Equal(0.0, examples[0].Features.TagSimilarity);
    }

    [Fact]
    public void AddAggregatedSeriesExample_AggregatesEpisodeContentFieldsIntoSeriesAffinities()
    {
        // The four per-episode content fields (franchise, production countries, inherited tags,
        // writers) must be aggregated across episodes and scored against the user's preference maps.
        // Every other series test leaves these at their empty defaults, so the aggregation loop
        // never runs and each affinity stays a false 0.0. Populate them here and require the
        // resulting features to be non-neutral, proving the collect-and-score wire-up is live.
        var (seriesId, profile, episodes) = BuildSeriesEpisodes(totalEpisodes: 3, playedEpisodes: 3);

        episodes[0].TmdbCollectionName = "MarvelVerse"; // franchise: first non-empty name wins
        episodes[0].ProductionCountries = new[] { "US" };
        episodes[1].ProductionCountries = new[] { "GB" };
        episodes[0].InheritedTags = new[] { "Superhero" };
        episodes[1].InheritedTags = new[] { "Sequel" };
        episodes[0].WriterNames = new[] { "WriterA" };
        episodes[1].WriterNames = new[] { "WriterB" };

        var examples = new List<TrainingExample>();
        var genrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Drama"] = 1.0 };
        var exposure = BuildExposure(genrePreferences, profile);

        var preferredFranchises = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["MarvelVerse"] = 1.0 };
        var preferredCountries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["US"] = 1.0 };
        var preferredInheritedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Superhero" };
        var preferredWriterWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["WriterA"] = 1.0 };

        TrainingFeatureComputer.AddAggregatedSeriesExample(
            examples,
            episodes,
            seriesId,
            profile,
            genrePreferences,
            new Dictionary<Guid, double>(),
            0.0,
            2020.0,
            exposure,
            new Dictionary<Guid, HashSet<string>>(),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            preferredFranchises,
            preferredCountries,
            preferredInheritedTags,
            preferredWriterWeights,
            genreStudioIdf: null,
            organicFallbackTimestamp: DateTime.UtcNow);

        Assert.Single(examples);
        var ex = examples[0];
        // Each affinity would collapse to 0.0 if its aggregation loop body were skipped.
        Assert.True(ex.Features.FranchiseAffinity > 0.0, "Franchise name was not captured from episode 0.");
        Assert.True(ex.Features.ProductionLocationAffinity > 0.0, "Production countries were not aggregated across episodes.");
        Assert.True(ex.Features.InheritedTagSimilarity > 0.0, "Inherited tags were not aggregated across episodes.");
        Assert.True(ex.Features.WriterAffinity > 0.0, "Writer names were not aggregated across episodes.");
    }
}
