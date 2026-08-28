using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the pure-static private DetermineReason on SeerrDiscoveryService.
/// </summary>
public sealed class SeerrDiscoveryServiceReasonTests
{
    // Person branch - the strongest signal, gates on BOTH threshold AND membership.

    [Fact]
    public void DetermineReason_PersonSimilarityAndMatch_ReturnsPersonNamed()
    {
        // Happy path: preferred person is in the candidate cast AND threshold met.
        var features = new CandidateFeatures { PeopleSimilarity = 0.5 };
        var candidate = new TmdbDiscoverItem { KnownPeople = ["Christopher Nolan", "Cillian Murphy"] };
        var topGenres = new List<string> { "Drama" };
        var preferredPeople = new HashSet<string> { "Christopher Nolan" };

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, preferredPeople);

        Assert.Equal("reasonPersonNamed", reasonKey);
        Assert.Equal("Christopher Nolan", relatedInfo);
    }

    [Fact]
    public void DetermineReason_PersonSimilarityAt0Point3Exactly_DoesNotUsePerson()
    {
        // BUG GUARD: the gate is strictly `> 0.3`. An exact 0.3 must NOT trip the person branch.
        var features = new CandidateFeatures { PeopleSimilarity = 0.3, GenreSimilarity = 0.0 };
        var candidate = new TmdbDiscoverItem { KnownPeople = ["Christopher Nolan"] };
        var topGenres = new List<string>();
        var preferredPeople = new HashSet<string> { "Christopher Nolan" };

        var (reasonKey, _) = InvokeDetermineReason(features, candidate, topGenres, preferredPeople);

        Assert.NotEqual("reasonPersonNamed", reasonKey);
        Assert.Equal("reasonPopular", reasonKey);
    }

    [Fact]
    public void DetermineReason_PersonSimilarityJustAbove0Point3_UsesPerson()
    {
        // Complements the boundary test above: strictly above the gate must fire.
        var features = new CandidateFeatures { PeopleSimilarity = 0.3001 };
        var candidate = new TmdbDiscoverItem { KnownPeople = ["Denis Villeneuve"] };
        var preferredPeople = new HashSet<string> { "Denis Villeneuve" };

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, new List<string>(), preferredPeople);

        Assert.Equal("reasonPersonNamed", reasonKey);
        Assert.Equal("Denis Villeneuve", relatedInfo);
    }

    [Fact]
    public void DetermineReason_PersonSimilarityHigh_ButCandidateHasNoKnownPeople_FallsThrough()
    {
        // BUG GUARD: the person branch requires the CANDIDATE to have KnownPeople.
        var features = new CandidateFeatures { PeopleSimilarity = 0.9 };
        var candidate = new TmdbDiscoverItem { KnownPeople = null };
        var topGenres = new List<string> { "Drama" };
        var preferredPeople = new HashSet<string> { "Christopher Nolan" };

        var (reasonKey, _) = InvokeDetermineReason(features, candidate, topGenres, preferredPeople);

        Assert.NotEqual("reasonPersonNamed", reasonKey);
    }

    [Fact]
    public void DetermineReason_PersonSimilarityHigh_ButCandidateHasEmptyKnownPeople_FallsThrough()
    {
        // Empty-but-non-null KnownPeople must be treated the same as null - the `is { Count: > 0 }`
        // pattern must gate on the actual size.
        var features = new CandidateFeatures { PeopleSimilarity = 0.9 };
        var candidate = new TmdbDiscoverItem { KnownPeople = [] };
        var preferredPeople = new HashSet<string> { "Denis Villeneuve" };

        var (reasonKey, _) = InvokeDetermineReason(features, candidate, new List<string>(), preferredPeople);

        Assert.NotEqual("reasonPersonNamed", reasonKey);
    }

    [Fact]
    public void DetermineReason_PersonSimilarityHigh_KnownPeoplePresent_ButNoPreferredMatch_FallsThrough()
    {
        // BUG GUARD: the CRITICAL person-branch guard.
        var features = new CandidateFeatures { PeopleSimilarity = 0.9 };
        var candidate = new TmdbDiscoverItem { KnownPeople = ["Random Actor", "Another Random"] };
        var topGenres = new List<string>();
        var preferredPeople = new HashSet<string> { "Christopher Nolan" }; // none present in candidate

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, preferredPeople);

        Assert.NotEqual("reasonPersonNamed", reasonKey);
        Assert.Null(relatedInfo);
    }

    [Fact]
    public void DetermineReason_PersonPreferredPeopleSetIsEmpty_FallsThrough()
    {
        // Cold-start user (empty preferred people): even with high similarity and full cast,
        // the person branch must not fire - the "match" clause cannot succeed against ∅.
        var features = new CandidateFeatures { PeopleSimilarity = 0.9 };
        var candidate = new TmdbDiscoverItem { KnownPeople = ["Christopher Nolan"] };
        var preferredPeople = new HashSet<string>();

        var (reasonKey, _) = InvokeDetermineReason(features, candidate, new List<string>(), preferredPeople);

        Assert.NotEqual("reasonPersonNamed", reasonKey);
    }

    [Fact]
    public void DetermineReason_PersonMatchWinsOverStrongGenre()
    {
        // Priority guard: even if the genre similarity would independently trip its own branch, the person branch takes precedence.
        var features = new CandidateFeatures
        {
            PeopleSimilarity = 0.5,
            GenreSimilarity = 0.95, // would independently trigger reasonGenre
            RecencyScore = 0.9,
            CombinedCriticScore = 0.9
        };
        var candidate = new TmdbDiscoverItem { KnownPeople = ["Nolan"] };
        var preferredPeople = new HashSet<string> { "Nolan" };

        var (reasonKey, relatedInfo) = InvokeDetermineReason(
            features, candidate, new List<string> { "Sci-Fi" }, preferredPeople);

        Assert.Equal("reasonPersonNamed", reasonKey);
        Assert.Equal("Nolan", relatedInfo);
    }

    [Fact]
    public void DetermineReason_PersonMatchIsFirstInPreferredIntersection()
    {
        // BUG GUARD: when MULTIPLE cast members are in the preferred set, the helper returns the FIRST-in-KnownPeople-order that matches - this deterministic tie-break keeps the UI text stable across regenerations.
        var features = new CandidateFeatures { PeopleSimilarity = 0.9 };
        // KnownPeople order matters: FirstOrDefault iterates the list in order.
        var candidate = new TmdbDiscoverItem
        {
            KnownPeople = ["Cillian Murphy", "Christopher Nolan", "Robert Downey Jr."]
        };
        var preferredPeople = new HashSet<string> { "Christopher Nolan", "Robert Downey Jr." };

        var (_, relatedInfo) = InvokeDetermineReason(features, candidate, new List<string>(), preferredPeople);

        // Cillian is not preferred, so Christopher (2nd position) wins the tie-break over RDJ.
        Assert.Equal("Christopher Nolan", relatedInfo);
    }

    // Genre branch - gates on GenreSimilarity > 0.7 AND topGenres.Count > 0

    [Fact]
    public void DetermineReason_HighGenreSimilarity_WithTopGenres_ReturnsGenre()
    {
        var features = new CandidateFeatures { GenreSimilarity = 0.85 };
        var candidate = new TmdbDiscoverItem();
        var topGenres = new List<string> { "Sci-Fi", "Thriller" };

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, new HashSet<string>());

        Assert.Equal("reasonGenre", reasonKey);
        // Only the FIRST top-genre is surfaced - this must be the user's dominant preference.
        Assert.Equal("Sci-Fi", relatedInfo);
    }

    [Fact]
    public void DetermineReason_GenreSimilarityAt0Point7Exactly_DoesNotUseGenre()
    {
        // BUG GUARD: gate is `> 0.7`, not `>= 0.7`. Exact 0.7 must fall through.
        var features = new CandidateFeatures { GenreSimilarity = 0.7 };
        var candidate = new TmdbDiscoverItem();
        var topGenres = new List<string> { "Drama" };

        var (reasonKey, _) = InvokeDetermineReason(features, candidate, topGenres, new HashSet<string>());

        Assert.NotEqual("reasonGenre", reasonKey);
        Assert.Equal("reasonPopular", reasonKey);
    }

    [Fact]
    public void DetermineReason_GenreSimilarityJustAbove0Point7_UsesGenre()
    {
        var features = new CandidateFeatures { GenreSimilarity = 0.7001 };
        var candidate = new TmdbDiscoverItem();
        var topGenres = new List<string> { "Comedy" };

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, new HashSet<string>());

        Assert.Equal("reasonGenre", reasonKey);
        Assert.Equal("Comedy", relatedInfo);
    }

    [Fact]
    public void DetermineReason_HighGenreSimilarity_ButEmptyTopGenres_FallsThrough()
    {
        // BUG GUARD: high similarity without any surfaceable genre name would produce "reasonGenre" with a null hint, which renders as "Because you liked " (double space).
        var features = new CandidateFeatures { GenreSimilarity = 0.95 };
        var candidate = new TmdbDiscoverItem();
        var topGenres = new List<string>();

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, new HashSet<string>());

        Assert.NotEqual("reasonGenre", reasonKey);
        Assert.Null(relatedInfo);
    }

    // Trending branch - gates on Recency > 0.8 AND CombinedCriticScore > 0.7

    [Fact]
    public void DetermineReason_HighRecencyAndCritic_ReturnsTrending()
    {
        var features = new CandidateFeatures { RecencyScore = 0.9, CombinedCriticScore = 0.85 };
        var candidate = new TmdbDiscoverItem();

        var (reasonKey, relatedInfo) = InvokeDetermineReason(
            features, candidate, new List<string>(), new HashSet<string>());

        Assert.Equal("reasonTrending", reasonKey);
        // Trending is title-agnostic - the RelatedInfo hint must be null so the UI shows
        // just "Trending now" without an appended (misleading) piece of context.
        Assert.Null(relatedInfo);
    }

    [Fact]
    public void DetermineReason_RecencyAt0Point8Exactly_DoesNotUseTrending()
    {
        // BUG GUARD: strict `>` gate on both dimensions of the trending branch.
        var features = new CandidateFeatures { RecencyScore = 0.8, CombinedCriticScore = 0.9 };
        var candidate = new TmdbDiscoverItem();

        var (reasonKey, _) = InvokeDetermineReason(
            features, candidate, new List<string>(), new HashSet<string>());

        Assert.NotEqual("reasonTrending", reasonKey);
        Assert.Equal("reasonPopular", reasonKey);
    }

    [Fact]
    public void DetermineReason_CriticScoreAt0Point7Exactly_DoesNotUseTrending()
    {
        var features = new CandidateFeatures { RecencyScore = 0.9, CombinedCriticScore = 0.7 };
        var candidate = new TmdbDiscoverItem();

        var (reasonKey, _) = InvokeDetermineReason(
            features, candidate, new List<string>(), new HashSet<string>());

        Assert.NotEqual("reasonTrending", reasonKey);
    }

    [Fact]
    public void DetermineReason_HighRecency_LowCritic_FallsThrough()
    {
        // BUG GUARD: recency alone is not enough - a fresh but critically panned title must NOT be labelled "trending".
        var features = new CandidateFeatures { RecencyScore = 0.95, CombinedCriticScore = 0.3 };
        var candidate = new TmdbDiscoverItem();

        var (reasonKey, _) = InvokeDetermineReason(
            features, candidate, new List<string>(), new HashSet<string>());

        Assert.NotEqual("reasonTrending", reasonKey);
        Assert.Equal("reasonPopular", reasonKey);
    }

    [Fact]
    public void DetermineReason_LowRecency_HighCritic_FallsThrough()
    {
        // Complement: a 30-year-old critical darling is not "trending", however good it is.
        var features = new CandidateFeatures { RecencyScore = 0.1, CombinedCriticScore = 0.99 };
        var candidate = new TmdbDiscoverItem();

        var (reasonKey, _) = InvokeDetermineReason(
            features, candidate, new List<string>(), new HashSet<string>());

        Assert.NotEqual("reasonTrending", reasonKey);
    }

    // Priority overall - trending beats popular, genre beats trending, etc.

    [Fact]
    public void DetermineReason_GenreBranchTakesPrecedenceOverTrending()
    {
        // Both branches would independently fire - genre is higher priority so it wins.
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.9,
            RecencyScore = 0.95,
            CombinedCriticScore = 0.9
        };
        var candidate = new TmdbDiscoverItem();
        var topGenres = new List<string> { "Horror" };

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, new HashSet<string>());

        Assert.Equal("reasonGenre", reasonKey);
        Assert.Equal("Horror", relatedInfo);
    }

    [Fact]
    public void DetermineReason_AllSignalsNeutral_ReturnsPopular()
    {
        // Fallback: default-constructed features must resolve to reasonPopular with no hint.
        var features = new CandidateFeatures();
        var candidate = new TmdbDiscoverItem();

        var (reasonKey, relatedInfo) = InvokeDetermineReason(
            features, candidate, new List<string>(), new HashSet<string>());

        Assert.Equal("reasonPopular", reasonKey);
        Assert.Null(relatedInfo);
    }

    [Fact]
    public void DetermineReason_AllBranchesFire_PersonAlwaysWins()
    {
        // Explicit priority pinning: person > genre > trending. All three gates trip; only person surfaces.
        var features = new CandidateFeatures
        {
            PeopleSimilarity = 0.9,
            GenreSimilarity = 0.9,
            RecencyScore = 0.9,
            CombinedCriticScore = 0.9
        };
        var candidate = new TmdbDiscoverItem { KnownPeople = ["Villeneuve"] };
        var preferredPeople = new HashSet<string> { "Villeneuve" };
        var topGenres = new List<string> { "Sci-Fi" };

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, preferredPeople);

        Assert.Equal("reasonPersonNamed", reasonKey);
        Assert.Equal("Villeneuve", relatedInfo);
    }

    [Fact]
    public void DetermineReason_TrendingWinsOverPopular_WhenGenreCannotFire()
    {
        // Genre branch is guarded out (empty topGenres), so trending must be reached.
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.9, // gate fires on similarity BUT topGenres.Count == 0 blocks it
            RecencyScore = 0.9,
            CombinedCriticScore = 0.9
        };
        var candidate = new TmdbDiscoverItem();
        var topGenres = new List<string>();

        var (reasonKey, relatedInfo) = InvokeDetermineReason(features, candidate, topGenres, new HashSet<string>());

        Assert.Equal("reasonTrending", reasonKey);
        Assert.Null(relatedInfo);
    }

    // Reflection glue

    private static (string ReasonKey, string? RelatedInfo) InvokeDetermineReason(
        CandidateFeatures features,
        TmdbDiscoverItem candidate,
        List<string> topGenres,
        HashSet<string> preferredPeople)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "DetermineReason",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [features, candidate, topGenres, preferredPeople])!;
        // Value tuples are returned as boxed System.ValueTuple<string, string?> from Reflection.
        var type = result.GetType();
        var item1 = (string)type.GetField("Item1")!.GetValue(result)!;
        var item2 = (string?)type.GetField("Item2")!.GetValue(result);
        return (item1, item2);
    }
}
