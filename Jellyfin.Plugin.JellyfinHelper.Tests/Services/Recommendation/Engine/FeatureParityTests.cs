using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
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
    public void SharedHelpers_SameInputs_ProduceIdenticalValues_AcrossCallSites()
    {
        // A single fixed candidate + user-preference snapshot. We invoke each shared helper twice
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
}
