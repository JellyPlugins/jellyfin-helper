using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Activity;

/// <summary>
///     Tests for the activity DTOs UserItemActivity, UserActivitySummary, and UserActivityResult.
/// </summary>
public class UserActivityDtoTests
{
    // UserItemActivity

    [Fact]
    public void UserItemActivity_Defaults_AreZeroEmptyAndFalse()
    {
        var activity = new UserItemActivity();

        Assert.Equal(Guid.Empty, activity.UserId);
        Assert.Equal(string.Empty, activity.UserName);
        Assert.Equal(0, activity.PlayCount);
        Assert.Null(activity.LastPlayedDate);
        Assert.Equal(0L, activity.PlaybackPositionTicks);
        Assert.Equal(0.0, activity.CompletionPercent);
        Assert.False(activity.Played);
        Assert.False(activity.IsFavorite);
        Assert.Null(activity.UserRating);
    }

    [Fact]
    public void UserItemActivity_AllPropertiesRoundTrip()
    {
        var userId = Guid.NewGuid();
        var lastPlayed = new DateTime(2024, 3, 15, 10, 0, 0, DateTimeKind.Utc);

        var activity = new UserItemActivity
        {
            UserId = userId,
            UserName = "alice",
            PlayCount = 5,
            LastPlayedDate = lastPlayed,
            PlaybackPositionTicks = 12_345_678L,
            CompletionPercent = 73.5,
            Played = true,
            IsFavorite = true,
            UserRating = 9.0
        };

        Assert.Equal(userId, activity.UserId);
        Assert.Equal("alice", activity.UserName);
        Assert.Equal(5, activity.PlayCount);
        Assert.Equal(lastPlayed, activity.LastPlayedDate);
        Assert.Equal(12_345_678L, activity.PlaybackPositionTicks);
        Assert.Equal(73.5, activity.CompletionPercent);
        Assert.True(activity.Played);
        Assert.True(activity.IsFavorite);
        Assert.Equal(9.0, activity.UserRating);
    }

    [Fact]
    public void UserItemActivity_LastPlayedDate_UtcValue_IsUnchanged()
    {
        var utc = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var activity = new UserItemActivity { LastPlayedDate = utc };

        Assert.Equal(utc, activity.LastPlayedDate);
        Assert.Equal(DateTimeKind.Utc, activity.LastPlayedDate!.Value.Kind);
    }

    [Fact]
    public void UserItemActivity_LastPlayedDate_LocalValue_IsNormalisedToUtc()
    {
        var local = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Local);
        var activity = new UserItemActivity { LastPlayedDate = local };

        // The setter must call DateTimeNormalization.ToUtc, so the stored kind must be Utc.
        Assert.Equal(DateTimeKind.Utc, activity.LastPlayedDate!.Value.Kind);
    }

    [Fact]
    public void UserItemActivity_LastPlayedDate_UnspecifiedKind_IsReinterpretedAsUtc()
    {
        var unspecified = new DateTime(2024, 6, 15, 8, 0, 0, DateTimeKind.Unspecified);
        var activity = new UserItemActivity { LastPlayedDate = unspecified };

        Assert.Equal(DateTimeKind.Utc, activity.LastPlayedDate!.Value.Kind);
        // Value must be unchanged (no offset applied for Unspecified).
        Assert.Equal(unspecified.Ticks, activity.LastPlayedDate.Value.Ticks);
    }

    [Fact]
    public void UserItemActivity_LastPlayedDate_SetToNull_RemainsNull()
    {
        var activity = new UserItemActivity { LastPlayedDate = DateTime.UtcNow };
        activity.LastPlayedDate = null;

        Assert.Null(activity.LastPlayedDate);
    }

    [Fact]
    public void UserItemActivity_UserRating_Null_IsAccepted()
    {
        var activity = new UserItemActivity { UserRating = 8.0 };
        activity.UserRating = null;

        Assert.Null(activity.UserRating);
    }

    [Fact]
    public void UserItemActivity_TwoInstancesWithSameValues_AreNotEqualByValue()
    {
        // Guard against accidental conversion to a record type.
        var a = new UserItemActivity { UserId = Guid.Empty, UserName = "x", PlayCount = 1 };
        var b = new UserItemActivity { UserId = Guid.Empty, UserName = "x", PlayCount = 1 };

        Assert.False(ReferenceEquals(a, b));
        Assert.False(a.Equals(b), "UserItemActivity must use reference equality, not record semantics");
        Assert.True(a.Equals(a));
    }

    // UserActivitySummary

    [Fact]
    public void UserActivitySummary_Defaults_AreZeroEmptyAndEmptyCollections()
    {
        var summary = new UserActivitySummary();

        Assert.Equal(Guid.Empty, summary.ItemId);
        Assert.Equal(string.Empty, summary.ItemName);
        Assert.Equal(string.Empty, summary.ItemType);
        Assert.Null(summary.SeriesName);
        Assert.Null(summary.EpisodeLabel);
        Assert.Null(summary.Year);
        Assert.NotNull(summary.Genres);
        Assert.Empty(summary.Genres);
        Assert.Null(summary.CommunityRating);
        Assert.Equal(0L, summary.RuntimeTicks);
        Assert.Equal(0, summary.TotalPlayCount);
        Assert.Equal(0, summary.UniqueViewers);
        Assert.Null(summary.MostRecentWatch);
        Assert.Equal(0.0, summary.AverageCompletionPercent);
        Assert.Equal(0, summary.FavoriteCount);
        Assert.NotNull(summary.UserActivities);
        Assert.Empty(summary.UserActivities);
    }

    [Fact]
    public void UserActivitySummary_AllPropertiesRoundTrip()
    {
        var itemId = Guid.NewGuid();
        var mostRecent = new DateTime(2024, 12, 1, 9, 0, 0, DateTimeKind.Utc);

        var summary = new UserActivitySummary
        {
            ItemId = itemId,
            ItemName = "Inception",
            ItemType = "Movie",
            SeriesName = null,
            EpisodeLabel = null,
            Year = 2010,
            Genres = ["Action", "Sci-Fi"],
            CommunityRating = 8.8f,
            RuntimeTicks = 8_640_000_000L,
            TotalPlayCount = 42,
            UniqueViewers = 7,
            MostRecentWatch = mostRecent,
            AverageCompletionPercent = 91.3,
            FavoriteCount = 3
        };

        Assert.Equal(itemId, summary.ItemId);
        Assert.Equal("Inception", summary.ItemName);
        Assert.Equal("Movie", summary.ItemType);
        Assert.Null(summary.SeriesName);
        Assert.Equal(2010, summary.Year);
        Assert.Equal(2, summary.Genres.Length);
        Assert.Equal(8.8f, summary.CommunityRating);
        Assert.Equal(8_640_000_000L, summary.RuntimeTicks);
        Assert.Equal(42, summary.TotalPlayCount);
        Assert.Equal(7, summary.UniqueViewers);
        Assert.Equal(mostRecent, summary.MostRecentWatch);
        Assert.Equal(91.3, summary.AverageCompletionPercent);
        Assert.Equal(3, summary.FavoriteCount);
    }

    [Fact]
    public void UserActivitySummary_EpisodeFields_RoundTripForEpisodeItems()
    {
        var summary = new UserActivitySummary
        {
            ItemType = "Episode",
            SeriesName = "Breaking Bad",
            EpisodeLabel = "S03E06"
        };

        Assert.Equal("Breaking Bad", summary.SeriesName);
        Assert.Equal("S03E06", summary.EpisodeLabel);
    }

    [Fact]
    public void UserActivitySummary_MostRecentWatch_UtcValue_IsUnchanged()
    {
        var utc = new DateTime(2024, 5, 20, 18, 0, 0, DateTimeKind.Utc);
        var summary = new UserActivitySummary { MostRecentWatch = utc };

        Assert.Equal(utc, summary.MostRecentWatch);
        Assert.Equal(DateTimeKind.Utc, summary.MostRecentWatch!.Value.Kind);
    }

    [Fact]
    public void UserActivitySummary_MostRecentWatch_LocalValue_IsNormalisedToUtc()
    {
        var local = new DateTime(2024, 5, 20, 18, 0, 0, DateTimeKind.Local);
        var summary = new UserActivitySummary { MostRecentWatch = local };

        Assert.Equal(DateTimeKind.Utc, summary.MostRecentWatch!.Value.Kind);
    }

    [Fact]
    public void UserActivitySummary_MostRecentWatch_UnspecifiedKind_IsReinterpretedAsUtc()
    {
        var unspecified = new DateTime(2024, 5, 20, 8, 0, 0, DateTimeKind.Unspecified);
        var summary = new UserActivitySummary { MostRecentWatch = unspecified };

        Assert.Equal(DateTimeKind.Utc, summary.MostRecentWatch!.Value.Kind);
        Assert.Equal(unspecified.Ticks, summary.MostRecentWatch.Value.Ticks);
    }

    [Fact]
    public void UserActivitySummary_MostRecentWatch_SetToNull_RemainsNull()
    {
        var summary = new UserActivitySummary { MostRecentWatch = DateTime.UtcNow };
        summary.MostRecentWatch = null;

        Assert.Null(summary.MostRecentWatch);
    }

    [Fact]
    public void UserActivitySummary_UserActivities_CanBePopulated()
    {
        var summary = new UserActivitySummary();
        summary.UserActivities.Add(new UserItemActivity { UserName = "bob", PlayCount = 2 });
        summary.UserActivities.Add(new UserItemActivity { UserName = "carol", PlayCount = 1 });

        Assert.Equal(2, summary.UserActivities.Count);
        Assert.Equal("bob", summary.UserActivities[0].UserName);
    }

    [Fact]
    public void UserActivitySummary_Genres_CanBeReassigned()
    {
        var summary = new UserActivitySummary { Genres = ["Comedy", "Drama", "Thriller"] };

        Assert.Equal(3, summary.Genres.Length);
        Assert.Equal("Comedy", summary.Genres[0]);
    }

    [Fact]
    public void UserActivitySummary_TwoInstancesWithSameValues_AreNotEqualByValue()
    {
        var a = new UserActivitySummary { ItemName = "Film", TotalPlayCount = 1 };
        var b = new UserActivitySummary { ItemName = "Film", TotalPlayCount = 1 };

        Assert.False(ReferenceEquals(a, b));
        Assert.False(a.Equals(b), "UserActivitySummary must use reference equality, not record semantics");
        Assert.True(a.Equals(a));
    }

    // UserActivityResult

    [Fact]
    public void UserActivityResult_Defaults_AreZeroAndEmptyCollection()
    {
        var before = DateTime.UtcNow;
        var result = new UserActivityResult();
        var after = DateTime.UtcNow;

        Assert.Equal(0, result.TotalItemsWithActivity);
        Assert.Equal(0, result.TotalUsersAnalyzed);
        Assert.Equal(0L, result.TotalPlayCount);
        Assert.NotNull(result.Items);
        Assert.Empty(result.Items);
        // GeneratedAt is initialised to DateTime.UtcNow at construction time.
        Assert.InRange(result.GeneratedAt, before, after);
        Assert.Equal(DateTimeKind.Utc, result.GeneratedAt.Kind);
    }

    [Fact]
    public void UserActivityResult_AllPropertiesRoundTrip()
    {
        var generatedAt = new DateTime(2024, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new Collection<UserActivitySummary>
        {
            new() { ItemName = "A", TotalPlayCount = 10 },
            new() { ItemName = "B", TotalPlayCount = 5 }
        };

        var result = new UserActivityResult
        {
            GeneratedAt = generatedAt,
            TotalItemsWithActivity = 99,
            TotalUsersAnalyzed = 4,
            TotalPlayCount = 300L
        };
        foreach (var item in items)
        {
            result.Items.Add(item);
        }

        Assert.Equal(generatedAt, result.GeneratedAt);
        Assert.Equal(99, result.TotalItemsWithActivity);
        Assert.Equal(4, result.TotalUsersAnalyzed);
        Assert.Equal(300L, result.TotalPlayCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("A", result.Items[0].ItemName);
    }

    [Fact]
    public void UserActivityResult_GeneratedAt_UtcValue_IsUnchanged()
    {
        var utc = new DateTime(2024, 1, 15, 6, 0, 0, DateTimeKind.Utc);
        var result = new UserActivityResult { GeneratedAt = utc };

        Assert.Equal(utc, result.GeneratedAt);
        Assert.Equal(DateTimeKind.Utc, result.GeneratedAt.Kind);
    }

    [Fact]
    public void UserActivityResult_GeneratedAt_LocalValue_IsNormalisedToUtc()
    {
        var local = new DateTime(2024, 1, 15, 6, 0, 0, DateTimeKind.Local);
        var result = new UserActivityResult { GeneratedAt = local };

        Assert.Equal(DateTimeKind.Utc, result.GeneratedAt.Kind);
    }

    [Fact]
    public void UserActivityResult_GeneratedAt_UnspecifiedKind_IsReinterpretedAsUtc()
    {
        var unspecified = new DateTime(2024, 1, 15, 6, 0, 0, DateTimeKind.Unspecified);
        var result = new UserActivityResult { GeneratedAt = unspecified };

        Assert.Equal(DateTimeKind.Utc, result.GeneratedAt.Kind);
        Assert.Equal(unspecified.Ticks, result.GeneratedAt.Ticks);
    }

    [Fact]
    public void UserActivityResult_Items_IsInitialisedMutableCollection_NotNull()
    {
        // Consumers call result.Items.Add() without a null check.
        // If the default ever becomes null, this test fires before the NRE reaches production.
        var result = new UserActivityResult();
        Assert.NotNull(result.Items);

        // Must be directly mutable (init-only collection, not frozen/read-only).
        result.Items.Add(new UserActivitySummary { ItemName = "New" });
        Assert.Single(result.Items);
    }

    [Fact]
    public void UserActivityResult_TwoInstancesWithSameValues_AreNotEqualByValue()
    {
        var a = new UserActivityResult { TotalItemsWithActivity = 1, TotalUsersAnalyzed = 1 };
        var b = new UserActivityResult { TotalItemsWithActivity = 1, TotalUsersAnalyzed = 1 };

        Assert.False(ReferenceEquals(a, b));
        Assert.False(a.Equals(b), "UserActivityResult must use reference equality, not record semantics");
        Assert.True(a.Equals(a));
    }
}
