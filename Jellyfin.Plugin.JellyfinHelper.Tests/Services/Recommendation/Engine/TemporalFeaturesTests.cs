using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="TemporalFeatures"/>. Covers <c>ResolveIsWeekend</c> (parity contract
///     across live scoring, Phase 1 recommendation-history examples, Phase 2 organic watches,
///     Phase 3 random negatives, and aggregated series examples), <c>GetTimeBucket</c>,
///     <c>ComputeDayOfWeekAffinity</c>, and <c>ComputeHourOfDayAffinity</c>.
/// </summary>
public class TemporalFeaturesTests
{
    // Anchor timestamps for deterministic assertions.
    // Friday 2026-01-02 12:00 UTC and Saturday 2026-01-03 12:00 UTC.
    private static readonly DateTime FridayNoonUtc = new(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SaturdayNoonUtc = new(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ResolveIsWeekend_UserProfileNull_Throws()
        => Assert.Throws<ArgumentNullException>(() => TemporalFeatures.ResolveIsWeekend(null!));

    [Fact]
    public void ResolveIsWeekend_LastActivityFriday_ReturnsFalse()
    {
        var profile = new UserWatchProfile { LastActivityDate = FridayNoonUtc };
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_LastActivitySaturday_ReturnsTrue()
    {
        var profile = new UserWatchProfile { LastActivityDate = SaturdayNoonUtc };
        Assert.True(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_LastActivitySunday_ReturnsTrue()
    {
        var profile = new UserWatchProfile
        {
            LastActivityDate = new DateTime(2026, 1, 4, 12, 0, 0, DateTimeKind.Utc)
        };
        Assert.True(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_LastActivityWinsOverOverride()
    {
        var profile = new UserWatchProfile { LastActivityDate = FridayNoonUtc };
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ResolveIsWeekend_NoAnchor_FallsBackToOverride()
    {
        var profile = new UserWatchProfile { LastActivityDate = null };
        Assert.True(TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc));
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile, FridayNoonUtc));
    }

    [Fact]
    public void ResolveIsWeekend_NoAnchorAndNoOverride_ReturnsFalseDeterministically()
    {
        var profile = new UserWatchProfile { LastActivityDate = null };
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_ConsistencyAcrossAllCallSites_UserAnchoredFriday()
    {
        var profile = new UserWatchProfile { LastActivityDate = FridayNoonUtc };
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile));
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ResolveIsWeekend_ConsistencyAcrossAllCallSites_UserAnchoredSaturday()
    {
        var profile = new UserWatchProfile { LastActivityDate = SaturdayNoonUtc };
        Assert.True(TemporalFeatures.ResolveIsWeekend(profile));
        Assert.True(TemporalFeatures.ResolveIsWeekend(profile, FridayNoonUtc));
    }

    // GetTimeBucket

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 2)]
    [InlineData(17, 2)]
    [InlineData(18, 3)]
    [InlineData(23, 3)]
    public void GetTimeBucket_MapsHourToCorrectBucket(int hour, int expectedBucket)
        => Assert.Equal(expectedBucket, TemporalFeatures.GetTimeBucket(hour));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(24, 3)]
    [InlineData(100, 3)]
    public void GetTimeBucket_HandlesOutOfRangeInputs(int hour, int expectedBucket)
    {
        // Documents current behavior: no range validation, uses fallthrough.
        Assert.Equal(expectedBucket, TemporalFeatures.GetTimeBucket(hour));
    }

    // ComputeDayOfWeekAffinity

    [Fact]
    public void ComputeDayOfWeekAffinity_CandidateWithNoGenres_ReturnsNeutral()
    {
        var candidate = new Movie { Name = "NoGenre" };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 15, new[] { "Action" });
        Assert.Equal(0.5, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_CandidateWithEmptyGenres_ReturnsNeutral()
    {
        var candidate = new Movie { Name = "Empty", Genres = Array.Empty<string>() };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 15, new[] { "Action" });
        Assert.Equal(0.5, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_ProfileWithFewerThan10Items_ReturnsNeutral()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 9, new[] { "Action" });
        Assert.Equal(0.5, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_LessThan3ItemsOnSameDay_ReturnsNeutral()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        // 2 items on Saturday
        profile.WatchedItems.Add(WI(SaturdayNoonUtc, new[] { "Action" }));
        profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(30), new[] { "Action" }));
        // 8 items on Friday
        for (int i = 0; i < 8; i++)
        {
            profile.WatchedItems.Add(WI(FridayNoonUtc.AddMinutes(i * 10.0), new[] { "Action" }));
        }
        Assert.Equal(0.5, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_AllSameDayGenreMatch_Returns1()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 10, new[] { "Action" });
        Assert.Equal(1.0, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_NoSameDayGenreMatch_Returns0()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Horror" } };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 10, new[] { "Action" });
        Assert.Equal(0.0, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_IgnoresItemsWithoutPlayedDate()
    {
        // Items lacking LastPlayedDate must be skipped entirely - not counted as
        // "same day zero-match" (that would erroneously drag the score toward 0).
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        // 10 items with matching genre on Saturday -> should still return 1.0
        for (int i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(i), new[] { "Action" }));
        }
        // 5 "orphan" items without LastPlayedDate but with playCount / non-Action genres.
        for (int i = 0; i < 5; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                Played = true,
                LastPlayedDate = null,
                Genres = new[] { "Horror" }
            });
        }
        Assert.Equal(1.0, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_IgnoresItemsWithoutPlaybackActivity()
    {
        // Favorite-only items (no play data) must be excluded from temporal signal.
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        // 10 real playback items on Saturday, matching Action -> should return 1.0.
        for (int i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(i), new[] { "Action" }));
        }
        // 5 favorite-only (no playback activity) items with a non-matching genre - must be ignored.
        for (int i = 0; i < 5; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                Played = false,
                PlayCount = 0,
                PlaybackPositionTicks = 0,
                IsFavorite = true,
                LastPlayedDate = SaturdayNoonUtc.AddMinutes(30 + i),
                Genres = new[] { "Horror" }
            });
        }
        Assert.Equal(1.0, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_MixedGenres_ReturnsFraction()
    {
        // 10 items on Saturday: 4 Action, 6 Horror. Candidate is Action -> 0.4.
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        for (int i = 0; i < 4; i++)
        {
            profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(i), new[] { "Action" }));
        }
        for (int i = 0; i < 6; i++)
        {
            profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(10 + i), new[] { "Horror" }));
        }
        var result = TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc);
        Assert.Equal(0.4, result, precision: 5);
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_CaseInsensitiveGenreMatch()
    {
        // Reveals: comparisons must use OrdinalIgnoreCase to match user profile
        // that stores mixed-case genres.
        var candidate = new Movie { Name = "Test", Genres = new[] { "action" } };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 10, new[] { "ACTION" });
        Assert.Equal(1.0, TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeDayOfWeekAffinity_UsesUtcNowWhenNowIsNull()
    {
        // The `now` parameter defaults to DateTime.UtcNow. We can't pin that,
        // but we can verify it does not throw and returns a valid score in [0,1].
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = BuildProfileWithItemsOn(DateTime.UtcNow, 15, new[] { "Action" });
        var result = TemporalFeatures.ComputeDayOfWeekAffinity(candidate, profile);
        Assert.InRange(result, 0.0, 1.0);
    }

    // ComputeHourOfDayAffinity

    [Fact]
    public void ComputeHourOfDayAffinity_CandidateWithNoGenres_ReturnsNeutral()
    {
        var candidate = new Movie { Name = "NoGenre" };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 15, new[] { "Action" });
        Assert.Equal(0.5, TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeHourOfDayAffinity_ProfileWithFewerThan10Items_ReturnsNeutral()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = BuildProfileWithItemsOn(SaturdayNoonUtc, 9, new[] { "Action" });
        Assert.Equal(0.5, TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeHourOfDayAffinity_LessThan3ItemsInBucket_ReturnsNeutral()
    {
        // 12:00 UTC = afternoon bucket (12-17). Only 2 items in that bucket -> neutral.
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(WI(SaturdayNoonUtc, new[] { "Action" }));           // afternoon
        profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(30), new[] { "Action" })); // afternoon
        // 8 items in a different bucket (night, hour 0)
        var night = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 8; i++)
        {
            profile.WatchedItems.Add(WI(night.AddMinutes(i * 5.0), new[] { "Action" }));
        }
        Assert.Equal(0.5, TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeHourOfDayAffinity_AllSameBucketGenreMatch_Returns1()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        // All 10 items in the afternoon bucket (12-17)
        for (int i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(i), new[] { "Action" }));
        }
        Assert.Equal(1.0, TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeHourOfDayAffinity_NoSameBucketGenreMatch_Returns0()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Horror" } };
        var profile = new UserWatchProfile();
        for (int i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(i), new[] { "Action" }));
        }
        Assert.Equal(0.0, TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeHourOfDayAffinity_IgnoresItemsWithoutPlayedDate()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        for (int i = 0; i < 10; i++)
        {
            profile.WatchedItems.Add(WI(SaturdayNoonUtc.AddMinutes(i), new[] { "Action" }));
        }
        for (int i = 0; i < 5; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                Played = true,
                LastPlayedDate = null,
                Genres = new[] { "Horror" }
            });
        }
        Assert.Equal(1.0, TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile, SaturdayNoonUtc));
    }

    [Fact]
    public void ComputeHourOfDayAffinity_BucketCrossingHours_GroupedCorrectly()
    {
        // Items at hours 12, 14, 17 all fall into afternoon bucket (12-17).
        // Item at hour 18 falls into evening bucket -> must be excluded.
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = new UserWatchProfile();
        // 3 afternoon items with match
        profile.WatchedItems.Add(WI(new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc), new[] { "Action" }));
        profile.WatchedItems.Add(WI(new DateTime(2026, 1, 3, 14, 0, 0, DateTimeKind.Utc), new[] { "Action" }));
        profile.WatchedItems.Add(WI(new DateTime(2026, 1, 3, 17, 59, 0, DateTimeKind.Utc), new[] { "Action" }));
        // 7 items in evening bucket - must NOT be counted for a noon reference.
        for (int i = 0; i < 7; i++)
        {
            profile.WatchedItems.Add(WI(new DateTime(2026, 1, 3, 20, i, 0, DateTimeKind.Utc), new[] { "Horror" }));
        }

        var result = TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile, SaturdayNoonUtc);
        // All 3 afternoon items match Action -> 3/3 = 1.0. Evening items excluded.
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputeHourOfDayAffinity_UsesUtcNowWhenNowIsNull()
    {
        var candidate = new Movie { Name = "Test", Genres = new[] { "Action" } };
        var profile = BuildProfileWithItemsOn(DateTime.UtcNow, 15, new[] { "Action" });
        var result = TemporalFeatures.ComputeHourOfDayAffinity(candidate, profile);
        Assert.InRange(result, 0.0, 1.0);
    }

    // Helpers

    private static WatchedItemInfo WI(DateTime playedAt, string[] genres)
        => new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            PlayCount = 1,
            LastPlayedDate = playedAt,
            Genres = genres
        };

    private static UserWatchProfile BuildProfileWithItemsOn(DateTime anchor, int count, string[] genres)
    {
        var profile = new UserWatchProfile();
        for (int i = 0; i < count; i++)
        {
            profile.WatchedItems.Add(WI(anchor.AddMinutes(i), genres));
        }
        return profile;
    }
}
