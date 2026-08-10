using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

/// <summary>
///     Tests for <see cref="UserWatchProfile"/>. Focus on:
///     <list type="bullet">
///         <item>Cache invalidation semantics for lazily-computed properties.</item>
///         <item>Null-safe setters that coalesce to empty rather than propagating NRE from cache
///               deserialization.</item>
///         <item>Boundary behavior of <see cref="UserWatchProfile.TopPeople"/> (min-count filter,
///               tie-break, cap at 20).</item>
///     </list>
/// </summary>
public class UserWatchProfileTests
{
    // ---------------------------------------------------------------------
    // Default state
    // ---------------------------------------------------------------------

    [Fact]
    public void DefaultConstruction_YieldsEmptyCollections()
    {
        var profile = new UserWatchProfile();

        Assert.Empty(profile.GenreDistribution);
        Assert.Empty(profile.LanguageProfile);
        Assert.Empty(profile.SubtitleLanguageProfile);
        Assert.Empty(profile.PeopleProfile);
        Assert.Empty(profile.WatchedItems);
        Assert.Empty(profile.FavoriteSeriesIds);
        Assert.Empty(profile.PreferredLanguages);
        Assert.Empty(profile.ToleratedLanguages);
        Assert.Empty(profile.PreferredSubtitleLanguages);
        Assert.Empty(profile.ToleratedSubtitleLanguages);
        Assert.Empty(profile.TopPeople);
        Assert.Null(profile.PrimaryLanguage);
        Assert.Null(profile.PrimarySubtitleLanguage);
    }

    // ---------------------------------------------------------------------
    // GenreDistribution setter - case-insensitive normalization
    // ---------------------------------------------------------------------

    [Fact]
    public void GenreDistributionSetter_NullValue_CoalescesToEmptyCaseInsensitive()
    {
        var profile = new UserWatchProfile { GenreDistribution = null! };

        // Must not throw and must accept mixed-case lookup.
        Assert.NotNull(profile.GenreDistribution);
        Assert.Empty(profile.GenreDistribution);
        profile.GenreDistribution["Action"] = 5;
        Assert.Equal(5, profile.GenreDistribution["action"]);
        Assert.Equal(5, profile.GenreDistribution["ACTION"]);
    }

    [Fact]
    public void GenreDistributionSetter_CaseSensitiveInput_IsUpgradedToCaseInsensitive()
    {
        // If a caller passes a case-sensitive dictionary, the property
        // must copy it into an OrdinalIgnoreCase dictionary. Otherwise a mixed-case
        // lookup ("action" vs. "Action") would silently miss.
        var input = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Action"] = 3,
            ["Drama"] = 2
        };

        var profile = new UserWatchProfile { GenreDistribution = input };

        Assert.Equal(3, profile.GenreDistribution["action"]);
        Assert.Equal(3, profile.GenreDistribution["ACTION"]);
        Assert.Equal(2, profile.GenreDistribution["drama"]);
    }

    [Fact]
    public void GenreDistributionSetter_CreatesDefensiveCopy()
    {
        // Mutating the original dictionary after assignment must not affect the profile.
        var input = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Action"] = 3
        };

        var profile = new UserWatchProfile { GenreDistribution = input };
        input["Action"] = 999;
        input["Comedy"] = 5;

        Assert.Equal(3, profile.GenreDistribution["Action"]);
        Assert.False(profile.GenreDistribution.ContainsKey("Comedy"));
    }

    // ---------------------------------------------------------------------
    // WatchedItems setter - null coalescing
    // ---------------------------------------------------------------------

    [Fact]
    public void WatchedItemsSetter_NullValue_CoalescesToEmpty()
    {
        var profile = new UserWatchProfile { WatchedItems = null! };
        Assert.NotNull(profile.WatchedItems);
        Assert.Empty(profile.WatchedItems);
    }

    // ---------------------------------------------------------------------
    // LanguageProfile - cache invalidation
    // ---------------------------------------------------------------------

    [Fact]
    public void PrimaryLanguage_CachedAndRecomputedOnSetter()
    {
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 3 },
                ["de"] = new LanguageProfileEntry { ChosenCount = 5 }
            }
        };

        Assert.Equal("de", profile.PrimaryLanguage);

        // Re-assign LanguageProfile - primary must be recomputed.
        profile.LanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = new LanguageProfileEntry { ChosenCount = 10 }
        };

        Assert.Equal("fr", profile.PrimaryLanguage);
    }

    [Fact]
    public void PrimaryLanguage_EmptyProfile_ReturnsNull()
    {
        var profile = new UserWatchProfile();
        Assert.Null(profile.PrimaryLanguage);
    }

    [Fact]
    public void LanguageProfileSetter_NullValue_ProducesEmptyCaseInsensitive()
    {
        var profile = new UserWatchProfile { LanguageProfile = null! };
        Assert.NotNull(profile.LanguageProfile);
        Assert.Empty(profile.LanguageProfile);
        profile.LanguageProfile["EN"] = new LanguageProfileEntry { ChosenCount = 1 };
        Assert.True(profile.LanguageProfile.ContainsKey("en"));
    }

    [Fact]
    public void LanguageProfileSetter_CaseSensitiveInput_IsNormalized()
    {
        var input = new Dictionary<string, LanguageProfileEntry>(StringComparer.Ordinal)
        {
            ["en"] = new LanguageProfileEntry { ChosenCount = 2 }
        };
        var profile = new UserWatchProfile { LanguageProfile = input };
        Assert.True(profile.LanguageProfile.ContainsKey("EN"));
    }

    [Fact]
    public void PreferredLanguages_OnlyIncludesChosen()
    {
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 3, ForcedCount = 1 },
                ["fr"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 5 }, // tolerated
                ["de"] = new LanguageProfileEntry { ChosenCount = 2, ForcedCount = 0 }
            }
        };

        Assert.Contains("en", profile.PreferredLanguages);
        Assert.Contains("de", profile.PreferredLanguages);
        Assert.DoesNotContain("fr", profile.PreferredLanguages);
    }

    [Fact]
    public void ToleratedLanguages_OnlyIncludesForcedNeverChosen()
    {
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 3, ForcedCount = 1 }, // NOT tolerated
                ["fr"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 5 }, // tolerated
                ["de"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 0 }  // neither
            }
        };

        Assert.Contains("fr", profile.ToleratedLanguages);
        Assert.DoesNotContain("en", profile.ToleratedLanguages);
        Assert.DoesNotContain("de", profile.ToleratedLanguages);
    }

    [Fact]
    public void PreferredAndToleratedLanguages_AreCaseInsensitive()
    {
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["EN"] = new LanguageProfileEntry { ChosenCount = 1 },
                ["Fr"] = new LanguageProfileEntry { ForcedCount = 1 }
            }
        };

        Assert.Contains("en", profile.PreferredLanguages);
        Assert.Contains("EN", profile.PreferredLanguages);
        Assert.Contains("fr", profile.ToleratedLanguages);
        Assert.Contains("FR", profile.ToleratedLanguages);
    }

    // ---------------------------------------------------------------------
    // SubtitleLanguageProfile - mirror the audio-profile invariants
    // ---------------------------------------------------------------------

    [Fact]
    public void PrimarySubtitleLanguage_ReflectsHighestWeightedScore()
    {
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 1, ForcedCount = 8 }, // 1 + 2.0 = 3.0
                ["de"] = new LanguageProfileEntry { ChosenCount = 2, ForcedCount = 0 }  // 2.0
            }
        };

        Assert.Equal("en", profile.PrimarySubtitleLanguage);
    }

    [Fact]
    public void SubtitleLanguageProfileSetter_NullValue_ProducesEmptyCaseInsensitive()
    {
        var profile = new UserWatchProfile { SubtitleLanguageProfile = null! };
        Assert.NotNull(profile.SubtitleLanguageProfile);
        Assert.Empty(profile.SubtitleLanguageProfile);
    }

    [Fact]
    public void SubtitleLanguageProfileSetter_InvalidatesCache()
    {
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageProfileEntry { ChosenCount = 3 }
            }
        };

        // Prime the cache.
        Assert.Equal("en", profile.PrimarySubtitleLanguage);
        Assert.Contains("en", profile.PreferredSubtitleLanguages);

        // Reassignment must invalidate all three caches.
        profile.SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["ja"] = new LanguageProfileEntry { ForcedCount = 4 }
        };

        Assert.Equal("ja", profile.PrimarySubtitleLanguage);
        Assert.Empty(profile.PreferredSubtitleLanguages);
        Assert.Contains("ja", profile.ToleratedSubtitleLanguages);
    }

    // ---------------------------------------------------------------------
    // PeopleProfile / TopPeople
    // ---------------------------------------------------------------------

    [Fact]
    public void PeopleProfileSetter_NullValue_CoalescesToEmptyCaseInsensitive()
    {
        var profile = new UserWatchProfile { PeopleProfile = null! };
        Assert.NotNull(profile.PeopleProfile);
        Assert.Empty(profile.PeopleProfile);
        profile.PeopleProfile["Christopher Nolan"] = 5;
        Assert.Equal(5, profile.PeopleProfile["christopher nolan"]);
    }

    [Fact]
    public void TopPeople_FiltersOutSingleAppearance()
    {
        // Appearances of exactly 1 must NOT enter TopPeople.
        // The min threshold is `>= 2`.
        var profile = new UserWatchProfile
        {
            PeopleProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Actor A"] = 5,
                ["Actor B"] = 2,
                ["Actor C"] = 1,   // must be filtered
                ["Actor D"] = 0    // must be filtered
            }
        };

        Assert.Contains("Actor A", profile.TopPeople);
        Assert.Contains("Actor B", profile.TopPeople);
        Assert.DoesNotContain("Actor C", profile.TopPeople);
        Assert.DoesNotContain("Actor D", profile.TopPeople);
    }

    [Fact]
    public void TopPeople_OrderedByFrequencyDescending()
    {
        var profile = new UserWatchProfile
        {
            PeopleProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Low"] = 2,
                ["High"] = 10,
                ["Mid"] = 5
            }
        };

        Assert.Equal(new[] { "High", "Mid", "Low" }, profile.TopPeople);
    }

    [Fact]
    public void TopPeople_TieBrokenByCaseInsensitiveName()
    {
        // Reveals: ordering must be deterministic for equal counts. Two people with
        // the same count must sort alphabetically to keep serialization stable across runs.
        var profile = new UserWatchProfile
        {
            PeopleProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["zack"] = 3,
                ["Alice"] = 3,
                ["bob"] = 3
            }
        };

        Assert.Equal(new[] { "Alice", "bob", "zack" }, profile.TopPeople);
    }

    [Fact]
    public void TopPeople_CapAt20()
    {
        // Reveals: even with 25 candidates, only top 20 must be returned.
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 25; i++)
        {
            // Counts descending so ordering is deterministic (25, 24, ..., 1)
            dict[$"Person{i:D2}"] = 25 - i + 1;
        }

        var profile = new UserWatchProfile { PeopleProfile = dict };

        Assert.Equal(20, profile.TopPeople.Count);
        // The lowest-frequency person (Person24 with count=2) must have been dropped.
        Assert.Contains("Person00", profile.TopPeople);
        Assert.DoesNotContain("Person24", profile.TopPeople);
    }

    [Fact]
    public void TopPeople_CachedAndInvalidatedOnReassignment()
    {
        var profile = new UserWatchProfile
        {
            PeopleProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Person A"] = 3
            }
        };

        Assert.Single(profile.TopPeople);

        profile.PeopleProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Person B"] = 4,
            ["Person C"] = 2
        };

        Assert.Equal(2, profile.TopPeople.Count);
        Assert.DoesNotContain("Person A", profile.TopPeople);
    }

    [Fact]
    public void TopPeople_EmptyProfile_ReturnsEmpty()
    {
        // Guard: empty PeopleProfile must short-circuit to [] rather than run LINQ.
        var profile = new UserWatchProfile();
        Assert.Empty(profile.TopPeople);
    }

    // ---------------------------------------------------------------------
    // MaxParentalRating - nullable semantics
    // ---------------------------------------------------------------------

    [Fact]
    public void MaxParentalRating_DefaultIsNull()
    {
        Assert.Null(new UserWatchProfile().MaxParentalRating);
    }

    [Fact]
    public void MaxParentalRating_CanBeSetAndRead()
    {
        var profile = new UserWatchProfile { MaxParentalRating = 13 };
        Assert.Equal(13, profile.MaxParentalRating);
    }

    // ---------------------------------------------------------------------
    // FavoriteSeriesIds - init-only HashSet
    // ---------------------------------------------------------------------

    [Fact]
    public void FavoriteSeriesIds_SupportsIdempotentAdd()
    {
        // HashSet semantics: adding the same GUID twice must not double-count.
        var profile = new UserWatchProfile();
        var id = Guid.NewGuid();
        profile.FavoriteSeriesIds.Add(id);
        profile.FavoriteSeriesIds.Add(id);

        Assert.Single(profile.FavoriteSeriesIds);
    }

    // ---------------------------------------------------------------------
    // Simple scalar properties round-trip
    // ---------------------------------------------------------------------

    [Fact]
    public void ScalarProperties_RoundTrip()
    {
        var userId = Guid.NewGuid();
        var activity = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "alice",
            WatchedMovieCount = 42,
            WatchedEpisodeCount = 137,
            WatchedSeriesCount = 8,
            TotalWatchTimeTicks = 10_000_000_000L,
            LastActivityDate = activity,
            FavoriteCount = 5,
            AverageCommunityRating = 7.3
        };

        Assert.Equal(userId, profile.UserId);
        Assert.Equal("alice", profile.UserName);
        Assert.Equal(42, profile.WatchedMovieCount);
        Assert.Equal(137, profile.WatchedEpisodeCount);
        Assert.Equal(8, profile.WatchedSeriesCount);
        Assert.Equal(10_000_000_000L, profile.TotalWatchTimeTicks);
        Assert.Equal(activity, profile.LastActivityDate);
        Assert.Equal(5, profile.FavoriteCount);
        Assert.Equal(7.3, profile.AverageCommunityRating);
    }
}
