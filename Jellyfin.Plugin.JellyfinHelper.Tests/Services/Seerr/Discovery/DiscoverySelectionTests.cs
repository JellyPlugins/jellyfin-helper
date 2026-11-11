using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>Personal relevance floors, cold-start honesty, diverse lists and bounded collection repetition.</summary>
public sealed class DiscoverySelectionTests
{
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
    public void Select_ColdStartDoesNotInventPersonalEvidence()
    {
        var candidate = Candidate(1, 0.01);
        candidate.IsPersonalized = false;
        Assert.Single(DiscoverySelection.Select([candidate], 10));
        Assert.False(candidate.Clone().IsPersonalized);
        candidate.TmdbCollectionId = 5;
        Assert.Equal(5, candidate.Clone().TmdbCollectionId);
        var json = JsonSerializer.Serialize(candidate, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault });
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
