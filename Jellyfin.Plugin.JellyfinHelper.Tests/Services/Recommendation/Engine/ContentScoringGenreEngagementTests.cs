using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

public sealed class ContentScoringGenreEngagementTests
{
    [Fact]
    public void ComputeGenreEngagement_EmptyCandidateGenres_ReturnsNeutral()
    {
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Action"] });
        var (fam, avg, abandon) = ContentScoring.ComputeGenreEngagement([], profile);
        Assert.Equal(0.0, fam);
        Assert.Equal(0.5, avg);
        Assert.Equal(0.0, abandon);
    }

    [Fact]
    public void ComputeGenreEngagement_NoHistory_ReturnsNeutral()
    {
        var profile = new UserWatchProfile();
        var (fam, avg, abandon) = ContentScoring.ComputeGenreEngagement(["Action"], profile);
        Assert.Equal(0.0, fam);
        Assert.Equal(0.5, avg);
        Assert.Equal(0.0, abandon);
    }

    [Fact]
    public void ComputeGenreEngagement_MatchingGenre_ReturnsFamiliarity()
    {
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Action"], RuntimeTicks = 100, PlaybackPositionTicks = 100 });
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Horror"], RuntimeTicks = 100, PlaybackPositionTicks = 10 });
        var (fam, avg, abandon) = ContentScoring.ComputeGenreEngagement(["Action"], profile);
        Assert.True(fam > 0);
        Assert.InRange(avg, 0.0, 1.0);
        Assert.InRange(abandon, 0.0, 1.0);
    }

    [Fact]
    public void ComputeGenreEngagement_AbandonedGenre_ReturnsAbandonRate()
    {
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = false, Genres = ["Action"], RuntimeTicks = 1000, PlaybackPositionTicks = 100 });
        var (fam, avg, abandon) = ContentScoring.ComputeGenreEngagement(["Action"], profile);
        Assert.Equal(1.0, abandon, 6);
        Assert.True(avg < 0.25);
    }

    [Fact]
    public void ComputeUserEngagementAggregates_NoHistory_ReturnsNeutral()
    {
        var profile = new UserWatchProfile();
        var (avg, abandon, active) = ContentScoring.ComputeUserEngagementAggregates(profile);
        Assert.Equal(0.5, avg);
        Assert.Equal(0.0, abandon);
        Assert.False(active);
    }

    [Fact]
    public void ComputeUserEngagementAggregates_WithHistory_ReturnsValues()
    {
        var profile = new UserWatchProfile();
        for (var i = 0; i < 12; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Drama"], RuntimeTicks = 100, PlaybackPositionTicks = 100 });
        }

        var (avg, abandon, active) = ContentScoring.ComputeUserEngagementAggregates(profile);
        Assert.Equal(1.0, avg, 6);
        Assert.Equal(0.0, abandon, 6);
        Assert.True(active);
    }

    [Fact]
    public void ComputeSeriesAffinity_NotSeries_ReturnsZero()
    {
        var profile = new UserWatchProfile();
        var candidate = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Name = "M" };
        var result = ContentScoring.ComputeSeriesAffinity(candidate, profile, new Dictionary<Guid, int>(), new Dictionary<Guid, HashSet<string>>());
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeSeriesAffinity_NoProgressingSeries_ReturnsZero()
    {
        var profile = new UserWatchProfile();
        var seriesId = Guid.NewGuid();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true });
        var candidate = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "S" };
        var counts = new Dictionary<Guid, int> { [seriesId] = 10 };
        var result = ContentScoring.ComputeSeriesAffinity(candidate, profile, counts, new Dictionary<Guid, HashSet<string>>());
        Assert.Equal(0.0, result);
    }
}
