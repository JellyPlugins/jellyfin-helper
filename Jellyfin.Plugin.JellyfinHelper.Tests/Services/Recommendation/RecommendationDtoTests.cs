using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

public class RecommendationDtoTests
{
    [Fact]
    public void WatchedItemInfo_DefaultValues_AreCorrect()
    {
        var item = new WatchedItemInfo();
        Assert.Equal(Guid.Empty, item.ItemId);
        Assert.Equal(string.Empty, item.Name);
        Assert.Equal(string.Empty, item.ItemType);
        Assert.False(item.Played);
        Assert.Null(item.LastPlayedDate);
        Assert.Equal(0, item.PlayCount);
        Assert.Null(item.Year);
        Assert.Empty(item.Genres);
        Assert.Null(item.CommunityRating);
        Assert.False(item.IsFavorite);
    }

    [Fact]
    public void UserWatchProfile_DefaultValues_AreCorrect()
    {
        var profile = new UserWatchProfile();
        Assert.Equal(Guid.Empty, profile.UserId);
        Assert.Equal(string.Empty, profile.UserName);
        Assert.Empty(profile.WatchedItems);
        Assert.Equal(0, profile.WatchedMovieCount);
        Assert.Equal(0, profile.WatchedEpisodeCount);
        Assert.Equal(0, profile.WatchedSeriesCount);
        Assert.Equal(0, profile.TotalWatchTimeTicks);
        Assert.Null(profile.LastActivityDate);
        Assert.Empty(profile.GenreDistribution);
        Assert.Equal(0, profile.FavoriteCount);
        Assert.Equal(0.0, profile.AverageCommunityRating);
    }

    [Fact]
    public void RecommendedItem_DefaultValues_AreCorrect()
    {
        var item = new RecommendedItem();
        Assert.Equal(Guid.Empty, item.ItemId);
        Assert.Equal(string.Empty, item.Name);
        Assert.Equal(string.Empty, item.ItemType);
        Assert.Equal(0, item.Score);
        Assert.Equal(string.Empty, item.Reason);
        Assert.Equal(string.Empty, item.ReasonKey);
        Assert.Null(item.RelatedItemName);
        Assert.Empty(item.Genres);
        Assert.Null(item.Year);
        Assert.Null(item.CommunityRating);
        Assert.Null(item.OfficialRating);
        Assert.Null(item.PrimaryImageTag);
    }

    [Fact]
    public void RecommendationResult_DefaultValues_AreCorrect()
    {
        var result = new RecommendationResult();
        Assert.Equal(Guid.Empty, result.UserId);
        Assert.Equal(string.Empty, result.UserName);
        Assert.Null(result.Profile);
        Assert.Empty(result.Recommendations);
        Assert.True(result.GeneratedAt <= DateTime.UtcNow);
        Assert.True(result.GeneratedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void RecommendationResult_CanAddRecommendations()
    {
        var result = new RecommendationResult
        {
            UserId = Guid.NewGuid(),
            UserName = "TestUser",
            Recommendations =
            [
                new RecommendedItem { Name = "Movie A", Score = 0.85 },
                new RecommendedItem { Name = "Movie B", Score = 0.72 }
            ],
            GeneratedAt = DateTime.UtcNow
        };

        Assert.Equal(2, result.Recommendations.Count);
        Assert.Equal("Movie A", result.Recommendations[0].Name);
    }

    [Fact]
    public void UserWatchProfile_GenreDistribution_CanBePopulated()
    {
        var profile = new UserWatchProfile
        {
            GenreDistribution = new Dictionary<string, int>
            {
                { "Action", 15 },
                { "Comedy", 8 },
                { "Drama", 12 }
            }
        };

        Assert.Equal(3, profile.GenreDistribution.Count);
        Assert.Equal(15, profile.GenreDistribution["Action"]);
    }

    [Fact]
    public void WatchedItemInfo_AllPropertiesRoundTrip()
    {
        var id = Guid.NewGuid();
        var lastPlayed = DateTime.UtcNow;

        var item = new WatchedItemInfo
        {
            ItemId = id,
            Name = "Test Movie",
            ItemType = "Movie",
            Played = true,
            LastPlayedDate = lastPlayed,
            PlayCount = 3,
            Year = 2024,
            Genres = ["Action", "Sci-Fi"],
            CommunityRating = 8.5f,
            IsFavorite = true
        };

        Assert.Equal(id, item.ItemId);
        Assert.Equal("Test Movie", item.Name);
        Assert.True(item.Played);
        Assert.Equal(lastPlayed, item.LastPlayedDate);
        Assert.Equal(3, item.PlayCount);
        Assert.Equal(2024, item.Year);
        Assert.Equal(2, item.Genres.Length);
        Assert.Equal(8.5f, item.CommunityRating);
        Assert.True(item.IsFavorite);
    }
}