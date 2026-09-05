using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Behavioral tests for the six shared content-affinity similarity helpers added to SimilarityComputer (Franchise, ProductionLocation, InheritedTag, Writer, BillingWeightedPeople, GenreStudioIdf).
/// </summary>
public sealed class FeatureAffinityComputerTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    [Fact]
    public void FranchiseAffinity_NullCandidateFranchise_ReturnsZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Marvel"] = 1.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeFranchiseAffinity(null, prefs));
    }

    [Fact]
    public void FranchiseAffinity_WhitespaceCandidateFranchise_ReturnsZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Marvel"] = 1.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeFranchiseAffinity("   ", prefs));
    }

    [Fact]
    public void FranchiseAffinity_EmptyPreferenceMap_ReturnsZero()
    {
        Assert.Equal(0.0, SimilarityComputer.ComputeFranchiseAffinity("Marvel", new Dictionary<string, double>(Ci)));
    }

    [Fact]
    public void FranchiseAffinity_UnknownFranchise_ReturnsZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Marvel"] = 1.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeFranchiseAffinity("Star Wars", prefs));
    }

    [Fact]
    public void FranchiseAffinity_KnownFranchise_ReturnsWeight_CaseInsensitive()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Marvel"] = 0.8 };
        Assert.Equal(0.8, SimilarityComputer.ComputeFranchiseAffinity("marvel", prefs));
    }

    [Fact]
    public void ProductionLocationAffinity_NullOrEmptyCandidate_ReturnsZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Japan"] = 1.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeProductionLocationAffinity(null, prefs));
        Assert.Equal(0.0, SimilarityComputer.ComputeProductionLocationAffinity([], prefs));
    }

    [Fact]
    public void ProductionLocationAffinity_EmptyPreferences_ReturnsZero()
    {
        Assert.Equal(0.0, SimilarityComputer.ComputeProductionLocationAffinity(["Japan"], new Dictionary<string, double>(Ci)));
    }

    [Fact]
    public void ProductionLocationAffinity_AllWhitespaceCandidate_ReturnsZero_NoDivideByZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Japan"] = 1.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeProductionLocationAffinity(["  ", ""], prefs));
    }

    [Fact]
    public void ProductionLocationAffinity_FullMatch_ReturnsPreferenceWeight()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Japan"] = 0.9 };
        Assert.Equal(0.9, SimilarityComputer.ComputeProductionLocationAffinity(["japan"], prefs));
    }

    [Fact]
    public void ProductionLocationAffinity_PartialMatch_AveragesOverCandidateCountries()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Japan"] = 1.0 };
        // 1 of 2 candidate countries matches (weight 1.0) -> 1.0 / 2 = 0.5.
        Assert.Equal(0.5, SimilarityComputer.ComputeProductionLocationAffinity(["Japan", "USA"], prefs));
    }

    [Fact]
    public void InheritedTagSimilarity_NullOrEmptyInputs_ReturnZero()
    {
        var prefs = new HashSet<string>(Ci) { "Christmas" };
        Assert.Equal(0.0, SimilarityComputer.ComputeInheritedTagSimilarity(null, prefs));
        Assert.Equal(0.0, SimilarityComputer.ComputeInheritedTagSimilarity([], prefs));
        Assert.Equal(0.0, SimilarityComputer.ComputeInheritedTagSimilarity(["Christmas"], new HashSet<string>(Ci)));
    }

    [Fact]
    public void InheritedTagSimilarity_Jaccard_CaseInsensitive()
    {
        var prefs = new HashSet<string>(Ci) { "Christmas", "Holiday" };
        // candidate {christmas, action}; intersection {christmas}=1; union {christmas,holiday,action}=3 -> 1/3.
        var result = SimilarityComputer.ComputeInheritedTagSimilarity(["christmas", "action"], prefs);
        Assert.Equal(1.0 / 3.0, result, 10);
    }

    [Fact]
    public void WriterAffinity_NullOrEmptyInputs_ReturnZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Aaron Sorkin"] = 3.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeWriterAffinity(null, prefs));
        Assert.Equal(0.0, SimilarityComputer.ComputeWriterAffinity([], prefs));
        Assert.Equal(0.0, SimilarityComputer.ComputeWriterAffinity(["Aaron Sorkin"], new Dictionary<string, double>(Ci)));
    }

    [Fact]
    public void WriterAffinity_AllWhitespaceCandidate_ReturnsZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Aaron Sorkin"] = 3.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeWriterAffinity(["  ", ""], prefs));
    }

    [Fact]
    public void WriterAffinity_MatchingWriter_ReturnsPositive()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Aaron Sorkin"] = 3.0 };
        var result = SimilarityComputer.ComputeWriterAffinity(["aaron sorkin"], prefs);
        Assert.True(result > 0.0, $"Expected positive writer affinity, got {result}");
        Assert.InRange(result, 0.0, 1.0);
    }

    [Fact]
    public void BillingWeightedPeople_EmptyInputs_ReturnZero()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Tom Hanks"] = 5.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeBillingWeightedPeople(new Dictionary<string, double>(Ci), prefs));
        Assert.Equal(0.0, SimilarityComputer.ComputeBillingWeightedPeople(new Dictionary<string, double>(Ci) { ["Tom Hanks"] = 1.0 }, new Dictionary<string, double>(Ci)));
    }

    [Fact]
    public void BillingWeightedPeople_NoOverlap_ReturnsZero()
    {
        var candidate = new Dictionary<string, double>(Ci) { ["Unknown Actor"] = 1.0 };
        var prefs = new Dictionary<string, double>(Ci) { ["Tom Hanks"] = 5.0 };
        Assert.Equal(0.0, SimilarityComputer.ComputeBillingWeightedPeople(candidate, prefs));
    }

    [Fact]
    public void BillingWeightedPeople_TopBilledMatch_ScoresHigherThanDeepCastMatch()
    {
        var prefs = new Dictionary<string, double>(Ci) { ["Star"] = 1.0, ["Extra"] = 1.0 };

        // Candidate A: the user's favoured person is top-billed (weight 1.0), plus a filler at 0.1.
        var candTop = new Dictionary<string, double>(Ci) { ["Star"] = 1.0, ["Filler"] = 0.1 };
        // Candidate B: the user's favoured person is deep-cast (weight 0.1), plus a lead at 1.0.
        var candDeep = new Dictionary<string, double>(Ci) { ["Lead"] = 1.0, ["Star"] = 0.1 };

        var top = SimilarityComputer.ComputeBillingWeightedPeople(candTop, prefs);
        var deep = SimilarityComputer.ComputeBillingWeightedPeople(candDeep, prefs);

        Assert.True(top > deep, $"Top-billed match ({top}) should outscore deep-cast match ({deep}).");
        Assert.InRange(top, 0.0, 1.0);
        Assert.InRange(deep, 0.0, 1.0);
    }

    [Fact]
    public void GenreStudioIdfPrior_NullOrEmptyTable_ReturnsZero()
    {
        Assert.Equal(0.0, SimilarityComputer.ComputeGenreStudioIdfPrior(["Drama"], ["A24"], null));
        Assert.Equal(0.0, SimilarityComputer.ComputeGenreStudioIdfPrior(["Drama"], ["A24"], new Dictionary<string, double>(Ci)));
    }

    [Fact]
    public void GenreStudioIdfPrior_NoCandidateTerms_ReturnsZero_NoDivideByZero()
    {
        var idf = new Dictionary<string, double>(Ci) { ["Drama"] = 0.5 };
        Assert.Equal(0.0, SimilarityComputer.ComputeGenreStudioIdfPrior(null, null, idf));
        Assert.Equal(0.0, SimilarityComputer.ComputeGenreStudioIdfPrior([], [], idf));
        Assert.Equal(0.0, SimilarityComputer.ComputeGenreStudioIdfPrior(["  "], [""], idf));
    }

    [Fact]
    public void GenreStudioIdfPrior_AveragesKnownTerms_UnknownTermsCountAsZero()
    {
        var idf = new Dictionary<string, double>(Ci) { ["RareGenre"] = 1.0, ["A24"] = 0.6 };
        // Candidate genres {RareGenre(1.0), CommonGenre(unknown->0)} + studios {A24(0.6)}:
        // sum = 1.0 + 0.0 + 0.6 = 1.6 over 3 counted terms -> 0.5333...
        var result = SimilarityComputer.ComputeGenreStudioIdfPrior(["RareGenre", "CommonGenre"], ["A24"], idf);
        Assert.Equal(1.6 / 3.0, result, 10);
    }

    [Fact]
    public void GenreStudioIdfPrior_ResultAlwaysInUnitRange()
    {
        var idf = new Dictionary<string, double>(Ci) { ["G"] = 1.0 };
        var result = SimilarityComputer.ComputeGenreStudioIdfPrior(["G"], ["G"], idf);
        Assert.InRange(result, 0.0, 1.0);
    }
}
