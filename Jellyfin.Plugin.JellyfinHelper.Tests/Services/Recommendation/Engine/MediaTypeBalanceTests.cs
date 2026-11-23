using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

public sealed class MediaTypeBalanceTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EqualCompletedViewingTimeIsBalancedDespiteDifferentMovieAndEpisodeCounts()
    {
        var profile = new UserWatchProfile();
        for (var i = 0; i < 10; i++) profile.WatchedItems.Add(Movie(i, 120));
        for (var i = 0; i < 40; i++) profile.WatchedItems.Add(Episode(i, (i / 4) + 1, 30));
        Assert.Equal(.5, MediaTypeBalance.TargetSeriesShare(profile, Now));
        // Starts, resumes and duplicated profile rows do not create more watched hours.
        foreach (var row in profile.WatchedItems.ToArray()) { row.PlayCount = 1000; profile.WatchedItems.Add(row); }
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", PlayCount = int.MaxValue, RuntimeTicks = TimeSpan.TicksPerDay, Played = false });
        Assert.Equal(.5, MediaTypeBalance.TargetSeriesShare(profile, Now));
    }

    [Fact]
    public void MissingRuntimeFallsBackToTitlesRatherThanInflatingASeasonIntoManyMovieSizedVotes()
    {
        var shortHistory = new UserWatchProfile();
        var longHistory = new UserWatchProfile();
        for (var i = 0; i < 4; i++) { shortHistory.WatchedItems.Add(Movie(i, 0)); longHistory.WatchedItems.Add(Movie(i, 0)); }
        shortHistory.WatchedItems.Add(Episode(1, 1, 0));
        for (var i = 1; i <= 200; i++) longHistory.WatchedItems.Add(Episode(i, 1, 30));
        Assert.Equal(MediaTypeBalance.TargetSeriesShare(shortHistory, Now), MediaTypeBalance.TargetSeriesShare(longHistory, Now));
        Assert.InRange(MediaTypeBalance.TargetSeriesShare(longHistory, Now)!.Value, .2, .4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrongPreferencesRemainModerateAndSparseEvidenceStaysCloserToBalanced(bool series)
    {
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(series ? Episode(1, 1, 45) : Movie(1, 120));
        var sparse = MediaTypeBalance.TargetSeriesShare(profile, Now)!.Value;
        for (var i = 2; i <= 50; i++) profile.WatchedItems.Add(series ? Episode(i, i, 45) : Movie(i, 120));
        var supported = MediaTypeBalance.TargetSeriesShare(profile, Now)!.Value;
        Assert.InRange(supported, .2, .8);
        Assert.True(Math.Abs(supported - .5) > Math.Abs(sparse - .5));
        Assert.Equal(series ? .8 : .2, supported, precision: 10);
    }

    [Fact]
    public void UnplayedFavoritesFuturePlaybackUnknownTypesAndOrphanEpisodesCannotInventFormatConsumption()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems =
            [
                new() { ItemId = Guid.NewGuid(), ItemType = "Movie", IsFavorite = true, PlayCount = 9 },
                new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = Now.AddDays(1) },
                new() { ItemId = Guid.NewGuid(), ItemType = "Episode", Played = true },
                new() { ItemId = Guid.NewGuid(), ItemType = "MusicVideo", Played = true },
                new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, UserRating = 1 }
            ]
        };
        Assert.Null(MediaTypeBalance.TargetSeriesShare(profile, Now));
        Assert.Null(MediaTypeBalance.TargetSeriesShare(null, Now));
        Assert.Equal(0, MediaTypeBalance.Adjustment(true, 1, 0, null));
        Assert.Equal(0, MediaTypeBalance.Adjustment(true, 1, 0, double.NaN));
    }

    [Fact]
    public void DuplicateProviderCopiesAndSeriesContainersDoNotAddConsumptionAndUsersStayIndependent()
    {
        var first = new UserWatchProfile { WatchedItems = [Movie(1, 120), Episode(1, 1, 30)] };
        var before = MediaTypeBalance.TargetSeriesShare(first, Now);
        var copy = Movie(1, 120); copy.ItemId = Guid.NewGuid(); first.WatchedItems.Add(copy);
        first.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Series", TmdbId = 1, Played = true, RuntimeTicks = TimeSpan.TicksPerDay, LastPlayedDate = Now });
        Assert.Equal(before, MediaTypeBalance.TargetSeriesShare(first, Now));
        var second = new UserWatchProfile { WatchedItems = [Episode(2, 2, 120), Movie(2, 30)] };
        Assert.True(MediaTypeBalance.TargetSeriesShare(second, Now) > before);
        Assert.Equal(before, MediaTypeBalance.TargetSeriesShare(first, Now));
    }

    private static WatchedItemInfo Movie(int id, int minutes) => new()
    {
        ItemId = Guid.NewGuid(), TmdbId = id, ItemType = "Movie", Played = true, RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks, LastPlayedDate = Now
    };

    private static WatchedItemInfo Episode(int id, int series, int minutes) => new()
    {
        ItemId = Guid.NewGuid(), TmdbId = id, SeriesTmdbId = series, SeriesId = Guid.Parse($"00000000-0000-0000-0000-{series:D12}"),
        ItemType = "Episode", Played = true, RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks, LastPlayedDate = Now
    };
}
