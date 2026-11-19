using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>Personal relevance floors, cold-start honesty, diverse lists and bounded collection repetition.</summary>
public sealed class DiscoverySelectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    [Fact]
    public void Select_DoesNotFillPersonalListsWithIrrelevantTitles()
    {
        var candidates = new[] { Candidate(1, 0.01), Candidate(2, 0.10), Candidate(3, 0.5) };
        var selected = DiscoverySelection.Select(candidates, 10);
        Assert.Equal([3, 2], selected.Select(item => item.TmdbId));
        Assert.Equal([1, 2, 3], candidates.Select(item => item.TmdbId));
    }

    [Fact]
    public void Select_DoesNotFloodAListWithOneCollection()
    {
        var candidates = Enumerable.Range(1, 6).Select(id => Candidate(id, 0.9, 99)).ToList();
        candidates.Add(Candidate(7, 0.6, 100, "Drama"));
        var selected = DiscoverySelection.Select(candidates, 10);
        Assert.Equal(2, selected.Count(item => item.TmdbCollectionId == 99));
        Assert.Contains(selected, item => item.TmdbId == 7);
        Assert.Equal(3, selected.Count);
    }

    [Fact]
    public void Select_UnknownCollectionsAreNotCollapsedIntoOneGroup()
    {
        var candidates = Enumerable.Range(1, 5).Select(id => Candidate(id, 0.8)).ToArray();
        Assert.Equal(5, DiscoverySelection.Select(candidates, 10).Count);
    }

    [Fact]
    public void Select_BalancesSimilarScoresWithDifferentGenres()
    {
        var first = Candidate(1, 0.9, genre: "Animation");
        var duplicateGenre = Candidate(2, 0.89, genre: "Animation");
        var different = Candidate(3, 0.85, genre: "Crime");
        Assert.Equal([1, 3], DiscoverySelection.Select([first, duplicateGenre, different], 2).Select(item => item.TmdbId));
        Assert.Equal(0.89, duplicateGenre.Score);
    }

    [Fact]
    public void Select_TraktKeepsItsSourceRankAndQualityPolicy()
    {
        var candidates = Enumerable.Range(1, 5).Select(id => Candidate(id, 0.01, 99)).ToArray();
        Assert.Equal([1, 2, 3], DiscoverySelection.Select(candidates, 3, preserveSourceRank: true).Select(item => item.TmdbId));
    }

    [Fact]
    public void OwnSelectionAndBackfillNeverReintroduceACapturedFamilyMismatch()
    {
        var unrelated = Candidate(1, 1);
        unrelated.ScoringFeatures = new CandidateFeatures { ContentFamilyMismatch = .5 };
        var matched = Candidate(2, .6);
        matched.ScoringFeatures = new CandidateFeatures { ContentFamilyMismatch = 0 };
        var unknown = Candidate(3, .5);
        Assert.Equal([2, 3], DiscoverySelection.Select([unrelated, matched, unknown], 20).Select(item => item.TmdbId));
        Assert.Equal(.5, unrelated.ScoringFeatures.ContentFamilyMismatch);
        Assert.Equal([1, 2, 3], DiscoverySelection.Select([unrelated, matched, unknown], 20, preserveSourceRank: true).Select(item => item.TmdbId));
    }

    [Fact]
    public void LikelyMismatchesAndInvalidContextRemainEligibleForFallbackSelection()
    {
        var likely = Candidate(1, .6);
        likely.ScoringFeatures = new CandidateFeatures { ContentFamilyMismatch = .25 };
        var invalid = Candidate(2, .5);
        invalid.ScoringFeatures = new CandidateFeatures { ContentFamilyMismatch = double.NaN };
        Assert.Equal([1, 2], DiscoverySelection.Select([likely, invalid], 5).Select(item => item.TmdbId));
    }

    [Fact]
    public void CoherentMatchesPrecedeGenericPopularityWithoutChangingTheirModelScores()
    {
        var popular = Candidate(1, .99);
        var related = Candidate(2, .62);
        related.ScoringFeatures = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7, ContentFitSupport = .78 };
        var selected = Assert.Single(DiscoverySelection.Select([popular, related], 1));
        Assert.Equal(2, selected.TmdbId);
        Assert.Equal(.62, selected.Score);
        Assert.Equal(1, Assert.Single(DiscoverySelection.Select([popular, related], 1, preserveSourceRank: true)).TmdbId);
    }

    [Fact]
    public void SupportedSecondaryTasteGetsAPlaceBeforeRepeatedPrimaryTasteCandidates()
    {
        var primary = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        var candidates = Enumerable.Range(1, 10).Select(id => Candidate(id, .95 - (id * .01))).ToList();
        foreach (var candidate in candidates) candidate.ScoringFeatures = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .8, ContentFitSupport = .8, ContentTasteKey = primary };
        var different = Candidate(11, .62);
        different.ScoringFeatures = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7, ContentFitSupport = .8, ContentTasteKey = secondary };
        candidates.Add(different);
        Assert.Contains(DiscoverySelection.Select(candidates, 3), candidate => candidate.TmdbId == 11);
    }

    [Fact]
    public void Select_SortsAfterDiverseSelectionWithoutReplacingItsMembership()
    {
        var first = Candidate(1, 0.9, genre: "Animation");
        var similar = Candidate(2, 0.89, genre: "Animation");
        var different = Candidate(3, 0.85, genre: "Crime");
        var excluded = Candidate(4, 0.7, genre: "Animation");
        var selected = DiscoverySelection.Select([first, similar, different, excluded], 3);
        Assert.Equal([1, 2, 3], selected.Select(item => item.TmdbId));
        Assert.Equal([0.9, 0.89, 0.85], selected.Select(item => item.Score));
        Assert.Equal([3, 2, 1], DiscoverySelection.Select([different, similar, first], 3, preserveSourceRank: true).Select(item => item.TmdbId));
    }

    [Fact]
    public void Select_ColdStartDoesNotInventPersonalEvidence()
    {
        var candidate = Candidate(1, 0.01);
        candidate.IsPersonalized = false;
        Assert.Single(DiscoverySelection.Select([candidate], 10));
        Assert.False(candidate.Clone().IsPersonalized);
        candidate.TmdbCollectionId = 5;
        Assert.Equal(5, candidate.Clone().TmdbCollectionId);
        var json = JsonSerializer.Serialize(candidate, JsonOptions);
        Assert.Contains("\"IsPersonalized\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_HandlesEmptyBoundsAndNullArguments()
    {
        Assert.Empty(DiscoverySelection.Select([], 10));
        Assert.Empty(DiscoverySelection.Select([Candidate(1, 0.5)], 0));
        Assert.Empty(DiscoverySelection.Select([Candidate(1, 0.5)], -1));
        Assert.Throws<ArgumentNullException>(() => DiscoverySelection.Select(null!, 1));
    }

    private static DiscoveryRecommendation Candidate(int id, double score, int collection = 0, string genre = "Adventure")
        => new() { TmdbId = id, Score = score, TmdbCollectionId = collection, Genres = [genre] };
}
