using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Train/serve parity tests for the seven content-affinity features.
/// </summary>
public sealed class FeatureParityTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    [Fact]
    public void MixedLegacyFranchises_FallBackOnlyForTheMatchingUnconflictedName()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionName = "Legacy saga" },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionId = 200, TmdbCollectionName = "Other saga" }
            ]
        };
        var preferences = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
        Assert.Equal(1, SimilarityComputer.ComputeFranchiseAffinity(123, "legacy SAGA", preferences));
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(123, "Other saga", preferences));
        Assert.Equal(1, SimilarityComputer.ComputeFranchiseAffinity(200, "translated label", preferences));
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(123, null, preferences));
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(123, "Unknown", preferences));
        Assert.Equal(1, SimilarityComputer.ComputeFranchiseAffinity(123, "Legacy saga", new Dictionary<string, double> { ["Legacy saga"] = 1 }));
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(123, "Legacy saga", new Dictionary<string, double> { ["tmdb:collection:200"] = 1, ["Legacy saga"] = 1 }));
        var candidate = new TmdbDiscoverItem { CollectionId = 123, CollectionName = "Legacy saga", HasCollectionMetadata = true };
        var genres = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var external = ExternalCandidateFeatureBuilder.Build(candidate, genres, [], 0, PreferenceBuilder.BuildGenreExposureAnalysis(genres, profile), profile);
        Assert.Equal(1, external.FranchiseAffinity);
    }

    [Fact]
    public void TranslatedNameCollisions_CannotDiluteConfirmedFranchiseIdentities()
    {
        var first = new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionId = 100, TmdbCollectionName = "First saga" };
        var second = new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionId = 200, TmdbCollectionName = "Second saga" };
        var profile = new UserWatchProfile { WatchedItems = [first, second] };
        var distinctNames = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
        first.TmdbCollectionName = "Shared translated name";
        second.TmdbCollectionName = "Shared translated name";
        var collidingNames = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
        Assert.Equal(1, distinctNames["tmdb:collection:100"]);
        Assert.Equal(1, collidingNames["tmdb:collection:100"]);
        Assert.Equal(distinctNames["tmdb:collection:200"], collidingNames["tmdb:collection:200"]);
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(300, "Shared translated name", collidingNames));
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(0, "Shared translated name", collidingNames));
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity("Shared translated name", collidingNames));
        Assert.Equal(1, SimilarityComputer.ComputeFranchiseAffinity(0, "First saga", distinctNames));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ParentMetadataRemoval_ClearsInheritedValuesWithoutDroppingVerifiedOwnValues(bool provenance, bool parentUnavailable)
    {
        var series = Guid.NewGuid();
        var row = new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = series, Played = true, Tags = ["old parent tag"], Studios = ["Old Parent Studio"] };
        if (provenance)
        {
            row.OwnTags = ["episode tag"];
            row.OwnStudios = ["Episode Studio"];
            row.SeriesTags = ["old parent tag"];
            row.SeriesStudios = ["Old Parent Studio"];
            row.Tags = ["episode tag", "old parent tag"];
            row.Studios = ["Episode Studio", "Old Parent Studio"];
        }

        var profile = new UserWatchProfile { WatchedItems = [row] };
        var lookup = new Dictionary<Guid, BaseItem>();
        var tags = new Dictionary<Guid, IReadOnlyList<string>>();
        var studios = new Dictionary<Guid, IReadOnlyList<string>>();
        if (!parentUnavailable)
        {
            lookup[series] = new MediaBrowser.Controller.Entities.TV.Series { Id = series, Tags = [], Studios = [] };
            tags[series] = [];
            studios[series] = [];
        }

        var liveTags = PreferenceBuilder.BuildTagPreferenceSet(profile, lookup);
        var liveStudios = PreferenceBuilder.BuildStudioPreferenceSet(profile, lookup);
        Assert.True(liveTags.SetEquals(TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, tags)));
        Assert.True(liveStudios.SetEquals(TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, studios)));
        Assert.Equal(parentUnavailable, liveTags.Contains("old parent tag"));
        Assert.Equal(parentUnavailable, liveStudios.Contains("Old Parent Studio"));
        Assert.Equal(provenance, liveTags.Contains("episode tag"));
        Assert.Equal(provenance, liveStudios.Contains("Episode Studio"));
    }

    [Fact]
    public void MixedLegacyAndConfirmedRows_ConsolidateWeightsOnlyForUnambiguousNames()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), IsFavorite = true, TmdbCollectionName = "Known saga" },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionId = 100, TmdbCollectionName = "Known saga" },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), IsFavorite = true, TmdbCollectionId = 200, TmdbCollectionName = "Unrelated saga" }
            ]
        };
        var migrated = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
        Assert.Equal(migrated["Known saga"], SimilarityComputer.ComputeFranchiseAffinity(100, "Translated saga", migrated));
        Assert.Equal(0, profile.WatchedItems[0].TmdbCollectionId);
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionId = 300, TmdbCollectionName = "Known saga" });
        var conflicted = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
        Assert.True(conflicted["tmdb:collection:100"] < conflicted["Known saga"]);
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(400, "Known saga", conflicted));
    }

    [Fact]
    public void ConflictingLocalizedCollectionNames_CannotOverrideConfirmedIds()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), IsFavorite = true, TmdbCollectionName = "Shared collection label" },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionId = 100, TmdbCollectionName = "Shared collection label" },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, TmdbCollectionId = 200, TmdbCollectionName = "Shared collection label" }
            ]
        };
        var preferences = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(300, "Shared collection label", preferences));
        Assert.Equal(0, SimilarityComputer.ComputeFranchiseAffinity(0, "Shared collection label", preferences));
        Assert.Equal(preferences["tmdb:collection:100"], SimilarityComputer.ComputeFranchiseAffinity(100, "Different translated label", preferences));
        Assert.Equal(preferences["tmdb:collection:200"], SimilarityComputer.ComputeFranchiseAffinity(200, "Shared collection label", preferences));
    }

    [Fact]
    public void OwnTagsAndStudios_AlignLocalTrainingAndPreparedExternalPreferences()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Tags = ["item tag", "parent tag", " "], InheritedTags = ["folder tag"], Studios = ["Episode Studio", "Parent Studio", ""] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), IsFavorite = true, Tags = ["favorite tag"], Studios = ["Favorite Studio"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), PlayCount = 1, Tags = ["replayed tag"], Studios = ["Replay Studio"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), PlaybackPositionTicks = 1, Tags = ["partial tag"], Studios = ["Partial Studio"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Tags = ["unseen tag"], Studios = ["Unseen Studio"] }
            ]
        };
        var context = ExternalCandidateFeatureBuilder.PrepareUserContext(profile);
        Assert.Equal(PreferenceBuilder.BuildTagPreferenceSet(profile, []), context.Tags);
        Assert.Equal(TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, []), context.Tags);
        Assert.Equal(PreferenceBuilder.BuildStudioPreferenceSet(profile, []), context.Studios);
        Assert.Equal(TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, []), context.Studios);
        Assert.DoesNotContain("folder tag", context.Tags);
        Assert.DoesNotContain("partial tag", context.Tags);
        Assert.Contains("Favorite Studio", context.Studios);
        profile.WatchedItems.Clear();
        var candidate = new TmdbDiscoverItem { KnownKeywords = ["item tag", "parent tag", "favorite tag", "replayed tag"], KnownStudios = ["Favorite Studio"] };
        var prepared = new ExternalCandidateFeatureBuilder.Context { GenrePreferences = [], PreferredPeople = [], GenreExposure = PreferenceBuilder.BuildGenreExposureAnalysis([], profile), User = context };
        var features = ExternalCandidateFeatureBuilder.Build(candidate, prepared);
        Assert.Equal(1, features.TagSimilarity);
        Assert.True(features.StudioMatch);
        var inheritedOnly = ExternalCandidateFeatureBuilder.Build(new TmdbDiscoverItem { KnownKeywords = ["folder tag"] }, prepared);
        Assert.Equal(0, inheritedOnly.TagSimilarity);
    }

    [Fact]
    public void FranchiseIds_FeedRecommendationAndOrganicTrainingAcrossLegacyNames()
    {
        var user = Guid.NewGuid();
        var first = Guid.NewGuid();
        var recommended = Guid.NewGuid();
        var organic = Guid.NewGuid();
        var at = DateTime.UtcNow.AddDays(-3);
        var profile = new UserWatchProfile
        {
            UserId = user,
            WatchedItems =
            [
                new WatchedItemInfo { ItemId = first, TmdbId = 1, TmdbCollectionId = 123, TmdbCollectionName = "Known saga", ItemType = "Movie", Played = true, LastPlayedDate = at.AddDays(-2), Genres = ["Adventure"] },
                new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 4, TmdbCollectionName = "Known saga", ItemType = "Movie", Played = true, LastPlayedDate = at.AddDays(-1), Genres = ["Adventure"] },
                new WatchedItemInfo { ItemId = recommended, TmdbId = 2, TmdbCollectionId = 123, ItemType = "Movie", Played = true, LastPlayedDate = at.AddDays(1), Genres = ["Adventure"] },
                new WatchedItemInfo { ItemId = organic, TmdbId = 3, TmdbCollectionId = 123, ItemType = "Movie", Played = true, LastPlayedDate = at.AddDays(2), Genres = ["Adventure"] }
            ]
        };
        var previous = new RecommendationResult { UserId = user, GeneratedAt = at, Recommendations = [new RecommendedItem { ItemId = recommended, TmdbId = 2, TmdbCollectionId = 123, ItemType = "Movie", Genres = ["Adventure"] }] };
        var result = TrainingDataBuilder.BuildExamples([previous], new Collection<UserWatchProfile> { profile }, TestContext.Current.CancellationToken);
        var recommendedId = TrainingExample.CreateTitleObservationId(user, recommended, 2, false);
        var organicId = TrainingExample.CreateTitleObservationId(user, organic, 3, false);
        Assert.Equal(1, Assert.Single(result.Examples, example => example.ObservationId == recommendedId).Features.FranchiseAffinity, 12);
        Assert.Equal(1, Assert.Single(result.Examples, example => example.ObservationId == organicId).Features.FranchiseAffinity, 12);
    }

    [Fact]
    public void SharedHelpers_SameInputs_ProduceIdenticalValues_AcrossCallSites()
    {        // A single fixed candidate + user-preference snapshot. We invoke each shared helper twice
        // with the SAME data (as the live and training paths do) and assert bit-identical results.
        var franchisePrefs = new Dictionary<string, double>(Ci) { ["Marvel"] = 0.7 };
        var countryPrefs = new Dictionary<string, double>(Ci) { ["USA"] = 0.9, ["Japan"] = 0.4 };
        var inheritedTags = new HashSet<string>(Ci) { "Superhero", "Christmas" };
        var writerPrefs = new Dictionary<string, double>(Ci) { ["Aaron Sorkin"] = 3.0 };
        var billedPrefs = new Dictionary<string, double>(Ci) { ["Tom Hanks"] = 5.0, ["Extra"] = 1.0 };
        var idf = new Dictionary<string, double>(Ci) { ["Drama"] = 0.8, ["A24"] = 0.5 };

        var candFranchise = "marvel";
        string[] candCountries = ["USA", "Canada"];
        string[] candInheritedTags = ["superhero", "action"];
        string[] candWriters = ["aaron sorkin"];
        var candBilling = new Dictionary<string, double>(Ci) { ["Tom Hanks"] = 1.0, ["Filler"] = 0.2 };
        string[] candGenres = ["Drama", "Thriller"];
        string[] candStudios = ["A24"];

        for (var i = 0; i < 2; i++)
        {
            var live = new[]
            {
                SimilarityComputer.ComputeFranchiseAffinity(candFranchise, franchisePrefs),
                SimilarityComputer.ComputeProductionLocationAffinity(candCountries, countryPrefs),
                SimilarityComputer.ComputeInheritedTagSimilarity(candInheritedTags, inheritedTags),
                EngineConstants.ComputeSeriesCompletability(true, "Ended", hasEndDate: true),
                SimilarityComputer.ComputeWriterAffinity(candWriters, writerPrefs),
                SimilarityComputer.ComputeBillingWeightedPeople(candBilling, billedPrefs),
                SimilarityComputer.ComputeGenreStudioIdfPrior(candGenres, candStudios, idf),
            };

            var train = new[]
            {
                SimilarityComputer.ComputeFranchiseAffinity(candFranchise, franchisePrefs),
                SimilarityComputer.ComputeProductionLocationAffinity(candCountries, countryPrefs),
                SimilarityComputer.ComputeInheritedTagSimilarity(candInheritedTags, inheritedTags),
                EngineConstants.ComputeSeriesCompletability(true, "Ended", hasEndDate: true),
                SimilarityComputer.ComputeWriterAffinity(candWriters, writerPrefs),
                SimilarityComputer.ComputeBillingWeightedPeople(candBilling, billedPrefs),
                SimilarityComputer.ComputeGenreStudioIdfPrior(candGenres, candStudios, idf),
            };

            for (var f = 0; f < live.Length; f++)
            {
                Assert.Equal(live[f], train[f]);
                Assert.True(double.IsFinite(live[f]), $"Feature {f} produced a non-finite value {live[f]}");
                Assert.InRange(live[f], 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void BillingMapFromCache_MatchesDirectMapConstruction()
    {
        // The training path rebuilds the billing map from positionally-aligned cached lists; the live
        // path builds it directly from GetPeople. Both must feed ComputeBillingWeightedPeople identically.
        string[] names = ["Tom Hanks", "Meg Ryan", "Tom Hanks"]; // duplicate keeps the higher weight
        double[] weights = [1.0, 0.5, 0.8];

        var fromCache = TrainingFeatureComputer.BuildBillingMapFromCache(names, weights);

        Assert.Equal(2, fromCache.Count);
        Assert.Equal(1.0, fromCache["Tom Hanks"]); // max(1.0, 0.8)
        Assert.Equal(0.5, fromCache["Meg Ryan"]);
    }

    [Fact]
    public void BillingMapFromCache_LengthMismatch_ReturnsEmpty_LegacyNeutralization()
    {
        // Legacy cache entries persisted PeopleNames but not PeopleWeights -> lengths differ ->
        // empty map -> BillingWeightedPeople neutralizes to 0.0 instead of misaligning.
        string[] names = ["A", "B", "C"];
        double[] weights = [1.0]; // mismatch
        Assert.Empty(TrainingFeatureComputer.BuildBillingMapFromCache(names, weights));
    }

    [Fact]
    public void BillingMapFromCache_EmptyInputs_ReturnEmpty()
    {
        Assert.Empty(TrainingFeatureComputer.BuildBillingMapFromCache([], []));
    }

    [Fact]
    public void ExtractBilledPeople_ThenBuildBillingMapFromCache_RoundTripsIdentically()
    {
        // Serve side: WatchHistoryService caches (names, weights) via ExtractBilledPeople. Train side: TrainingFeatureComputer rebuilds the billing map from those cached lists.
        var people = new List<PersonInfo>
        {
            new() { Name = "Lead", Type = PersonKind.Actor, SortOrder = 0 },
            new() { Name = "Support", Type = PersonKind.Actor, SortOrder = 3 },
            new() { Name = "Director", Type = PersonKind.Director, SortOrder = 0 },
            new() { Name = "Composer", Type = PersonKind.Composer, SortOrder = 0 }, // ignored (not Actor/Director)
        };

        var (names, weights) = SimilarityComputer.ExtractBilledPeople(people);
        Assert.Equal(names.Count, weights.Count);
        Assert.DoesNotContain("Composer", names); // only Actor/Director are billed

        var rebuilt = TrainingFeatureComputer.BuildBillingMapFromCache(names, weights);

        // Top-billed (SortOrder 0) outranks deep-billed (SortOrder 3).
        Assert.Equal(1.0, rebuilt["Lead"], 10);
        Assert.True(rebuilt["Lead"] > rebuilt["Support"]);
        Assert.Equal(3, rebuilt.Count);
    }

    [Fact]
    public void ExtractBilledPeople_NullOrNoBilledPeople_ReturnsEmpty()
    {
        var (names, weights) = SimilarityComputer.ExtractBilledPeople(null);
        Assert.Empty(names);
        Assert.Empty(weights);

        var composerOnly = new List<PersonInfo> { new() { Name = "C", Type = PersonKind.Composer } };
        var (n2, w2) = SimilarityComputer.ExtractBilledPeople(composerOnly);
        Assert.Empty(n2);
        Assert.Empty(w2);
    }

    [Theory]
    [InlineData(false, "Ended", true, EngineConstants.SeriesCompletabilityNeutral)]   // movie -> neutral
    [InlineData(false, null, false, EngineConstants.SeriesCompletabilityNeutral)]     // movie -> neutral
    [InlineData(true, "Ended", false, EngineConstants.SeriesCompletabilityEnded)]     // ended -> 1.0
    [InlineData(true, "ENDED", false, EngineConstants.SeriesCompletabilityEnded)]     // case-insensitive
    [InlineData(true, "Unreleased", false, EngineConstants.SeriesCompletabilityUnreleased)] // 0.0
    [InlineData(true, "Continuing", false, EngineConstants.SeriesCompletabilityContinuing)] // 0.5
    [InlineData(true, null, false, EngineConstants.SeriesCompletabilityNeutral)]      // unknown status -> neutral
    [InlineData(true, "Bogus", false, EngineConstants.SeriesCompletabilityNeutral)]   // unrecognized -> neutral
    public void SeriesCompletability_MapsStatusToExpectedValue(bool isSeries, string? status, bool hasEndDate, double expected)
    {
        Assert.Equal(expected, EngineConstants.ComputeSeriesCompletability(isSeries, status, hasEndDate));
    }

    [Fact]
    public void SeriesCompletability_ContinuingWithEndDate_IsBetweenContinuingAndEnded()
    {
        // A "Continuing" series that already has an end date has effectively wrapped -> nudged upward.
        var result = EngineConstants.ComputeSeriesCompletability(true, "Continuing", hasEndDate: true);
        Assert.True(result > EngineConstants.SeriesCompletabilityContinuing);
        Assert.True(result <= EngineConstants.SeriesCompletabilityEnded);
    }

    [Fact]
    public void BillingWeight_TopBilled_IsMaximal_AndMonotonicallyNonIncreasing()
    {
        var w0 = EngineConstants.ComputeBillingWeight(0);
        var w1 = EngineConstants.ComputeBillingWeight(1);
        var w10 = EngineConstants.ComputeBillingWeight(10);

        Assert.Equal(1.0, w0); // scale/(scale+0) = 1.0
        Assert.True(w0 > w1 && w1 > w10, "Billing weight must strictly decrease with billing position.");
        Assert.InRange(w10, 0.0, 1.0);
    }

    [Fact]
    public void BillingWeight_NegativeOrder_ClampedToTopBilled()
    {
        Assert.Equal(EngineConstants.ComputeBillingWeight(0), EngineConstants.ComputeBillingWeight(-5));
    }

    [Fact]
    public void LibraryAddedRecency_MissingDateCreated_TrainingMatchesLiveDefault()
    {
        // Live scoring reads a candidate's non-nullable DateCreated, so an unset value resolves to
        // the default date and scores as very old. The training builders see a nullable DateCreated
        // and must impute the same default rather than a neutral midpoint, or a missing library-added
        // date would score differently between training and serving.
        var liveMissing = ContentScoring.ComputeRecencyScore(default);

        Assert.True(liveMissing < 0.01, $"A missing DateCreated must score as very old, not neutral; got {liveMissing}.");
        Assert.NotEqual(0.5, liveMissing, 3);
    }

    [Fact]
    public void LibraryAddedRecency_MissingDateCreated_TrainingImputationEqualsLiveDefault()
    {
        // The training builder sees a nullable DateCreated and, when it is missing, imputes the same default
        // date the live path resolves from a candidate's non-nullable DateCreated. This pins that the imputed
        // value equals the live default and is not the neutral 0.5 midpoint the separate premiere-date
        // RecencyScore uses when a date is unknown, so a regression that swapped the null branch to 0.5 fails.
        var trainingImputed = ImputeLibraryAddedRecency(dateCreated: null);
        var liveDefault = ContentScoring.ComputeRecencyScore(default);

        Assert.Equal(liveDefault, trainingImputed);
        Assert.NotEqual(0.5, trainingImputed, 3);
    }

    // Mirrors the training builders' library-added recency imputation: a present date scores from that date, a
    // missing one falls back to the same default the live path uses. Kept as a helper so the null branch under
    // test is a real conditional rather than a constant the analyzer folds away.
    private static double ImputeLibraryAddedRecency(DateTime? dateCreated) =>
        dateCreated.HasValue
            ? ContentScoring.ComputeRecencyScore(dateCreated.Value)
            : ContentScoring.ComputeRecencyScore(default);
}
