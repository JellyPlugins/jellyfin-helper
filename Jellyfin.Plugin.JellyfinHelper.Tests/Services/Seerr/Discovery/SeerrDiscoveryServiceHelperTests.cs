using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the pure-static internal helpers on <see cref="SeerrDiscoveryService"/>:
///     <c>StampMediaType</c>, <c>BuildGenreIdList</c>, <c>GetPrimaryLanguageForDiscovery</c>,
///     <c>BuildPreferredPeopleSet</c>, <c>DeduplicateAndFilter</c>.
///     <para>
///         These helpers encode the "how do we sort/filter/normalise candidate items before we score
///         them?" rules of the discovery pipeline. They are exercised only indirectly by the
///         <c>SeerrDiscoveryServiceHttpTests</c> (which pump a scripted <see cref="System.Net.Http.HttpClient"/>
///         through the full generation flow) — so a subtle behaviour change in one of these helpers
///         silently changes what the frontend sees without any HTTP-level test failing.
///     </para>
///     <para>
///         All helpers are <c>private static</c> on a <c>sealed</c> class so we reach them via reflection.
///         The alternative — making them internal purely for testing — would leak implementation details
///         into <c>InternalsVisibleTo</c> consumers.
///     </para>
/// </summary>
public sealed class SeerrDiscoveryServiceHelperTests
{
    // ============================================================================
    // StampMediaType — defensive normalisation of items from typed TMDb endpoints.
    // ============================================================================

    [Fact]
    public void StampMediaType_EmptyList_DoesNotThrow()
    {
        // Contract: helper must accept an empty list cleanly (typed endpoint returned zero results).
        InvokeStampMediaType([], "tv");
    }

    [Fact]
    public void StampMediaType_AllItemsGetStamped()
    {
        var items = new List<TmdbDiscoverItem>
        {
            new() { Id = 1, MediaType = "movie" },
            new() { Id = 2, MediaType = "movie" },
            new() { Id = 3, MediaType = "movie" }
        };

        InvokeStampMediaType(items, "tv");

        Assert.All(items, i => Assert.Equal("tv", i.MediaType));
    }

    [Fact]
    public void StampMediaType_OverwritesExistingMediaType()
    {
        // BUG GUARD: the helper explicitly OVERWRITES rather than filling in only when missing.
        // The design is defensive — even when TMDb correctly emits mediaType, we must stamp our
        // known type. If a maintainer refactored this to "only fill when null/empty", cross-media
        // items (e.g. an anime series returned by a TV endpoint but tagged mediaType="movie" by
        // TMDb) would end up in the wrong bucket.
        var items = new List<TmdbDiscoverItem>
        {
            new() { Id = 1, MediaType = "movie" }
        };

        InvokeStampMediaType(items, "tv");

        Assert.Equal("tv", items[0].MediaType);
    }

    // ============================================================================
    // BuildGenreIdList — genre-name → TMDb-int → invariant-culture string.
    // ============================================================================

    [Fact]
    public void BuildGenreIdList_EmptyInput_ReturnsEmpty()
    {
        var result = InvokeBuildGenreIdList([], _ => 1);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildGenreIdList_AllMapperReturnNull_ReturnsEmpty()
    {
        // BUG GUARD: unknown genres must be silently dropped, not passed through as "0" or null.
        var result = InvokeBuildGenreIdList(["Foo", "Bar"], _ => (int?)null);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildGenreIdList_PartialMapping_KeepsOnlyResolved()
    {
        Func<string, int?> mapper = s => s switch
        {
            "Action" => 28,
            "Comedy" => 35,
            _ => null
        };
        var result = InvokeBuildGenreIdList(["Action", "Unknown", "Comedy", "MoreUnknown"], mapper);
        Assert.Equal(["28", "35"], result);
    }

    [Fact]
    public void BuildGenreIdList_UsesInvariantCulture()
    {
        // BUG GUARD: on some locales integer.ToString() would add thousands separators or use non-Arabic
        // digits. That would produce invalid URL segments like "1,000" for genre id 1000 and break Seerr.
        // We force the mapper to return a big number and verify no separators appear.
        var result = InvokeBuildGenreIdList(["G"], _ => 12345);
        Assert.Single(result);
        Assert.Equal("12345", result[0]);
        Assert.DoesNotContain(",", result[0], StringComparison.Ordinal);
        Assert.DoesNotContain(".", result[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGenreIdList_PreservesInputOrder()
    {
        // Order matters: the caller iterates in-order and the first non-null id becomes
        // the "page 2" candidate. Reordering the output would break that guarantee.
        Func<string, int?> mapper = s => s.Length; // deterministic mapper
        var result = InvokeBuildGenreIdList(["a", "bb", "ccc"], mapper);
        Assert.Equal(["1", "2", "3"], result);
    }

    // ============================================================================
    // GetPrimaryLanguageForDiscovery — user's primary language for /discover/xxx/language/{lang}
    // Requires ChosenCount >= 3 to filter out "forced by only option available".
    // ============================================================================

    [Fact]
    public void GetPrimaryLanguageForDiscovery_NoLanguageProfile_ReturnsNull()
    {
        // With an empty LanguageProfile the derived PrimaryLanguage getter returns null,
        // and the helper must short-circuit before hitting the ChosenCount gate.
        var profile = new UserWatchProfile();
        Assert.Null(InvokeGetPrimaryLanguageForDiscovery(profile));
    }

    [Fact]
    public void GetPrimaryLanguageForDiscovery_ChosenCountBelowThreshold_ReturnsNull()
    {
        // 2 < 3 → below threshold → treat as "forced by lack of options" → null.
        // We seed a single-entry LanguageProfile so PrimaryLanguage resolves to "de";
        // the helper then reads its ChosenCount and rejects.
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 2 };

        Assert.Null(InvokeGetPrimaryLanguageForDiscovery(profile));
    }

    [Fact]
    public void GetPrimaryLanguageForDiscovery_ChosenCountAtThreshold_ReturnsLowercased()
    {
        // Exactly 3 hits the threshold — boundary condition. Any refactor to strict > 3 breaks here.
        // Also verifies the ToLowerInvariant contract: we seed uppercase "DE", expect lowercase "de".
        var profile = new UserWatchProfile();
        profile.LanguageProfile["DE"] = new LanguageProfileEntry { ChosenCount = 3 };

        Assert.Equal("de", InvokeGetPrimaryLanguageForDiscovery(profile));
    }

    [Fact]
    public void GetPrimaryLanguageForDiscovery_ChosenCountAboveThreshold_ReturnsLowercased()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["en"] = new LanguageProfileEntry { ChosenCount = 100 };

        Assert.Equal("en", InvokeGetPrimaryLanguageForDiscovery(profile));
    }

    [Fact]
    public void GetPrimaryLanguageForDiscovery_MultipleLanguages_PicksHighestWeightedScore()
    {
        // Regression scenario: derived PrimaryLanguage picks argmax by WeightedScore.
        // When "en" has higher chosen count than "de", the helper must return "en".
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 3 };
        profile.LanguageProfile["en"] = new LanguageProfileEntry { ChosenCount = 20 };

        Assert.Equal("en", InvokeGetPrimaryLanguageForDiscovery(profile));
    }

    // ============================================================================
    // BuildPreferredPeopleSet — top-N people with case-insensitive comparer.
    // ============================================================================

    [Fact]
    public void BuildPreferredPeopleSet_EmptyPeopleProfile_ReturnsEmptyCaseInsensitiveSet()
    {
        var profile = new UserWatchProfile();
        var set = InvokeBuildPreferredPeopleSet(profile);
        Assert.Empty(set);
        // Case-insensitive comparer must be preserved even for empty sets so downstream
        // matches ("Christopher NOLAN" vs "christopher nolan") work correctly.
        Assert.Equal(StringComparer.OrdinalIgnoreCase, set.Comparer);
    }

    [Fact]
    public void BuildPreferredPeopleSet_PopulatedProfile_UsesTopPeople()
    {
        // Populate PeopleProfile so TopPeople surfaces content.
        var profile = new UserWatchProfile();
        profile.PeopleProfile["Christopher Nolan"] = 10;
        profile.PeopleProfile["Cillian Murphy"] = 8;
        profile.PeopleProfile["Ken Watanabe"] = 5;

        var set = InvokeBuildPreferredPeopleSet(profile);

        Assert.NotEmpty(set);
        Assert.Contains("christopher nolan", set); // case-insensitive contains
    }

    [Fact]
    public void BuildPreferredPeopleSet_IsCaseInsensitive_LookupSucceedsWithDifferentCasing()
    {
        var profile = new UserWatchProfile();
        profile.PeopleProfile["Zendaya"] = 5;

        var set = InvokeBuildPreferredPeopleSet(profile);

        Assert.Contains("ZENDAYA", set);
        Assert.Contains("zendaya", set);
        Assert.Contains("Zendaya", set);
    }

    // ============================================================================
    // DeduplicateAndFilter — the discovery pipeline's core filter.
    // Signature: (List<TmdbDiscoverItem>, HashSet<(int, string)>, int? maxParentalRating,
    //            double minVoteAverage, double avgYear, bool isChildAccount)
    // ============================================================================

    [Fact]
    public void DeduplicateAndFilter_EmptyCandidates_ReturnsEmpty()
    {
        var result = InvokeDeduplicateAndFilter([], [], null, 5.0, 0, false);
        Assert.Empty(result);
    }

    [Fact]
    public void DeduplicateAndFilter_DropsItemsWithIdZeroOrNegative()
    {
        // Id <= 0 is unresolvable — TMDb never returns such IDs on a healthy connection,
        // but a corrupt cache could. Silently dropping them is safer than surfacing garbage.
        var candidates = new List<TmdbDiscoverItem>
        {
            new() { Id = 0, VoteAverage = 8.0 },
            new() { Id = -5, VoteAverage = 8.0 },
            new() { Id = 42, VoteAverage = 8.0 }
        };
        var result = InvokeDeduplicateAndFilter(candidates, [], null, 5.0, 0, false);
        Assert.Single(result);
        Assert.Equal(42, result[0].Id);
    }

    [Fact]
    public void DeduplicateAndFilter_DropsItemsBelowMinVoteAverage()
    {
        var candidates = new List<TmdbDiscoverItem>
        {
            new() { Id = 1, VoteAverage = 4.9, MediaType = "movie" }, // below threshold
            new() { Id = 2, VoteAverage = 5.0, MediaType = "movie" }, // at threshold (kept, "< 5.0" is strict)
            new() { Id = 3, VoteAverage = 7.5, MediaType = "movie" }
        };
        var result = InvokeDeduplicateAndFilter(candidates, [], null, 5.0, 0, false);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == 2);
        Assert.Contains(result, r => r.Id == 3);
    }

    [Fact]
    public void DeduplicateAndFilter_DropsItemsInExcludedSet()
    {
        // Excluded via (Id, MediaType) tuple — verifies both keys participate in the lookup.
        var candidates = new List<TmdbDiscoverItem>
        {
            new() { Id = 1, VoteAverage = 8.0, MediaType = "movie" },
            new() { Id = 1, VoteAverage = 8.0, MediaType = "tv" }, // same Id, different MediaType — must survive
            new() { Id = 2, VoteAverage = 8.0, MediaType = "movie" }
        };
        var excluded = new HashSet<(int, string)> { (1, "movie") };
        var result = InvokeDeduplicateAndFilter(candidates, excluded, null, 5.0, 0, false);

        // Only the movie with Id=1 was excluded; TV with Id=1 must remain.
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == 1 && r.MediaType == "tv");
        Assert.Contains(result, r => r.Id == 2);
    }

    [Fact]
    public void DeduplicateAndFilter_DeduplicatesOnIdAndMediaType()
    {
        // BUG GUARD: TMDb assigns separate ID spaces to movies and TV. Deduplication on Id alone
        // would collapse legitimately-different items (e.g. movie #42 and TV #42) into one.
        var candidates = new List<TmdbDiscoverItem>
        {
            new() { Id = 42, VoteAverage = 8.0, MediaType = "movie" },
            new() { Id = 42, VoteAverage = 8.0, MediaType = "movie" }, // duplicate — must be dropped
            new() { Id = 42, VoteAverage = 8.0, MediaType = "tv" }     // different MediaType — must survive
        };
        var result = InvokeDeduplicateAndFilter(candidates, [], null, 5.0, 0, false);
        Assert.Equal(2, result.Count);
        Assert.Single(result, r => r.MediaType == "movie");
        Assert.Single(result, r => r.MediaType == "tv");
    }

    [Fact]
    public void DeduplicateAndFilter_MediaTypeNull_TreatedAsMovie()
    {
        // The dedup key uses `candidate.MediaType ?? "movie"` — a null MediaType must
        // fall into the "movie" bucket, not create a phantom third bucket.
        var candidates = new List<TmdbDiscoverItem>
        {
            new() { Id = 7, VoteAverage = 8.0, MediaType = null! },
            new() { Id = 7, VoteAverage = 8.0, MediaType = "movie" } // exact duplicate of above under the fallback
        };
        var result = InvokeDeduplicateAndFilter(candidates, [], null, 5.0, 0, false);
        Assert.Single(result);
    }

    [Fact]
    public void DeduplicateAndFilter_MediaTypeCaseInsensitive_TreatedAsSame()
    {
        // ToLowerInvariant() on the key — different casings of "MOVIE" vs "movie" must dedup.
        var candidates = new List<TmdbDiscoverItem>
        {
            new() { Id = 9, VoteAverage = 8.0, MediaType = "MOVIE" },
            new() { Id = 9, VoteAverage = 8.0, MediaType = "movie" },
            new() { Id = 9, VoteAverage = 8.0, MediaType = "Movie" }
        };
        var result = InvokeDeduplicateAndFilter(candidates, [], null, 5.0, 0, false);
        Assert.Single(result);
    }

    [Fact]
    public void DeduplicateAndFilter_ChildAccountBypassesYearFilter()
    {
        // Year-based filter is intentionally disabled for child accounts (child films are
        // often decades old — Disney classics etc. — and dropping them would be wrong).
        var oldClassic = new TmdbDiscoverItem
        {
            Id = 100,
            VoteAverage = 8.5,
            MediaType = "movie",
            ReleaseDate = new DateTime(1965, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var result = InvokeDeduplicateAndFilter(
            [oldClassic],
            [],
            maxParentalRating: null,
            minVoteAverage: 5.0,
            avgYear: 2020, // modern viewer
            isChildAccount: true);
        Assert.Single(result);
        Assert.Equal(100, result[0].Id);
    }

    [Fact]
    public void DeduplicateAndFilter_NormalAccount_ModernViewer_DropsOldFilms()
    {
        // Non-child account with avgYear near "now" gets a 12-year window; anything older is dropped.
        var currentYear = DateTime.UtcNow.Year;
        var oldMovie = new TmdbDiscoverItem
        {
            Id = 1,
            VoteAverage = 8.5,
            MediaType = "movie",
            ReleaseDate = new DateTime(currentYear - 20, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var freshMovie = new TmdbDiscoverItem
        {
            Id = 2,
            VoteAverage = 8.5,
            MediaType = "movie",
            ReleaseDate = new DateTime(currentYear - 2, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var result = InvokeDeduplicateAndFilter(
            [oldMovie, freshMovie],
            [],
            maxParentalRating: null,
            minVoteAverage: 5.0,
            avgYear: currentYear - 3, // >= currentYear - 6 → 12-year window kicks in
            isChildAccount: false);
        Assert.Single(result);
        Assert.Equal(2, result[0].Id);
    }

    [Fact]
    public void DeduplicateAndFilter_NoAvgYear_NoYearFilterApplied()
    {
        // BUG GUARD: when avgYear == 0 (cold-start / no watch history) the year filter MUST be
        // disabled — otherwise cold-start users would get zero results from any pre-2000 title.
        var reallyOldMovie = new TmdbDiscoverItem
        {
            Id = 1,
            VoteAverage = 8.5,
            MediaType = "movie",
            ReleaseDate = new DateTime(1939, 1, 1, 0, 0, 0, DateTimeKind.Utc) // Wizard of Oz vibes
        };
        var result = InvokeDeduplicateAndFilter(
            [reallyOldMovie],
            [],
            maxParentalRating: null,
            minVoteAverage: 5.0,
            avgYear: 0,
            isChildAccount: false);
        Assert.Single(result);
    }

    // ============================================================================
    // Reflection glue
    // ============================================================================

    private static void InvokeStampMediaType(List<TmdbDiscoverItem> items, string mediaType)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "StampMediaType",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(null, [items, mediaType]);
    }

    private static List<string> InvokeBuildGenreIdList(IEnumerable<string> genres, Func<string, int?> mapper)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "BuildGenreIdList",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (List<string>)method!.Invoke(null, [genres, mapper])!;
    }

    private static string? InvokeGetPrimaryLanguageForDiscovery(UserWatchProfile profile)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "GetPrimaryLanguageForDiscovery",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [profile]);
    }

    private static HashSet<string> InvokeBuildPreferredPeopleSet(UserWatchProfile profile)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "BuildPreferredPeopleSet",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (HashSet<string>)method!.Invoke(null, [profile])!;
    }

    private static List<TmdbDiscoverItem> InvokeDeduplicateAndFilter(
        List<TmdbDiscoverItem> candidates,
        HashSet<(int, string)> excluded,
        int? maxParentalRating,
        double minVoteAverage,
        double avgYear,
        bool isChildAccount)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "DeduplicateAndFilter",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        // The production signature is HashSet<(int TmdbId, string MediaType)> — tuple element names
        // are metadata only, so a plain HashSet<(int, string)> is castable. Reflection.Invoke doesn't
        // care about the names either. We box everything into object? for the parameter array.
        return (List<TmdbDiscoverItem>)method!.Invoke(null,
            [candidates, excluded, maxParentalRating, minVoteAverage, avgYear, isChildAccount])!;
    }
}
