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

        // A single abandoned sample is a low-confidence estimate: with shrinkage K=3 the raw abandon
        // rate of 1.0 is damped by 1 / (1 + 3) = 0.25, and completion (0.1) is pulled toward 0.5.
        Assert.True(fam > 0);
        Assert.Equal(0.25, abandon, 6);
        Assert.InRange(avg, 0.25, 0.5);
    }

    [Fact]
    public void ComputeGenreEngagement_ManyAbandonedSamples_ApproachesRawRate()
    {
        // With many samples the shrinkage confidence approaches 1.0, so the measured abandon rate is
        // trusted almost fully (unlike the single-sample case above).
        var profile = new UserWatchProfile();
        for (var i = 0; i < 30; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = false, Genres = ["Action"], RuntimeTicks = 1000, PlaybackPositionTicks = 100 });
        }

        var (_, avg, abandon) = ContentScoring.ComputeGenreEngagement(["Action"], profile);

        // confidence = 30 / (30 + 3) = 0.909; abandon = 1.0 * 0.909.
        Assert.Equal(30.0 / 33.0, abandon, 6);
        Assert.True(avg < 0.25);
    }

    [Fact]
    public void ComputeSeriesAffinity_NotSeries_ReturnsZero()
    {
        var profile = new UserWatchProfile();
        var candidate = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Name = "M" };
        var context = ContentScoring.BuildSeriesAffinityContext(profile, new Dictionary<Guid, int>());
        var result = ContentScoring.ComputeSeriesAffinity(candidate, context, new Dictionary<Guid, HashSet<string>>());
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeSeriesAffinity_NoProgressingSeries_ReturnsZero()
    {
        var profile = new UserWatchProfile();
        var seriesId = Guid.NewGuid();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true });
        var candidate = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "S" };

        // Series is only 10% watched (1/10), below the 30-80% progressing band, so there is no
        // progressing series to compare against and affinity is 0.
        var counts = new Dictionary<Guid, int> { [seriesId] = 10 };
        var context = ContentScoring.BuildSeriesAffinityContext(profile, counts);
        var result = ContentScoring.ComputeSeriesAffinity(candidate, context, new Dictionary<Guid, HashSet<string>>());
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeSeriesAffinity_ProgressingSeriesWithSharedGenresAndPeople_ReturnsPositive()
    {
        // A progressing series (50% watched) shares genre + cast with the candidate. Exercises the full
        // Jaccard path including people-set collection from the lookup for both the watched items and
        // the watched series id.
        var profile = new UserWatchProfile();
        var watchedSeriesId = Guid.NewGuid();
        var ep1 = Guid.NewGuid();
        var ep2 = Guid.NewGuid();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = ep1, SeriesId = watchedSeriesId, Played = true, Genres = ["Drama"] });
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = ep2, SeriesId = watchedSeriesId, Played = true, Genres = ["Drama"] });

        var candidate = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "Candidate", Genres = ["Drama"] };

        var peopleLookup = new Dictionary<Guid, HashSet<string>>
        {
            [candidate.Id] = new(StringComparer.OrdinalIgnoreCase) { "Alice" },
            [ep1] = new(StringComparer.OrdinalIgnoreCase) { "Alice" },
            [watchedSeriesId] = new(StringComparer.OrdinalIgnoreCase) { "Bob" }
        };

        // 2 of 4 episodes watched = 50%, inside the 30-80% progressing band.
        var counts = new Dictionary<Guid, int> { [watchedSeriesId] = 4 };
        var context = ContentScoring.BuildSeriesAffinityContext(profile, counts);

        var result = ContentScoring.ComputeSeriesAffinity(candidate, context, peopleLookup);

        // Shared "Drama" genre (Jaccard 1.0) and shared "Alice" person yield a positive composite.
        Assert.True(result > 0.0, $"Expected positive affinity from shared genre/people, got {result}");
    }

    [Fact]
    public void BuildSeriesAffinityContext_SeriesWithNonPositiveEpisodeCount_IsNotProgressing()
    {
        // A watched series whose library episode count is missing/zero must be skipped from the
        // progressing set (guards the total<=0 path in GetProgressingSeriesIds).
        var profile = new UserWatchProfile();
        var seriesId = Guid.NewGuid();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, Genres = ["Drama"] });

        var candidate = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "S", Genres = ["Drama"] };
        var counts = new Dictionary<Guid, int> { [seriesId] = 0 };
        var context = ContentScoring.BuildSeriesAffinityContext(profile, counts);

        Assert.Empty(context.ProgressingSeriesIds);
        Assert.Equal(0.0, ContentScoring.ComputeSeriesAffinity(candidate, context, new Dictionary<Guid, HashSet<string>>()));
    }
}
