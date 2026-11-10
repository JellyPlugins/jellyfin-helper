using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>Historical reuse stays bounded, cutoff-correct and isolated from every target exclusion.</summary>
public sealed class TrainingHistoryCacheTests
{
    private static readonly DateTime Prediction = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameCutoff_ReusesNeighbourInputsAndNormalizesTheInstant()
    {
        var user = Guid.NewGuid();
        var profiles = Profiles(user);
        var cache = new TrainingHistoryCache(profiles, CancellationToken.None);
        var first = cache.Get(user, Prediction, new HashSet<Guid> { Guid.NewGuid() });
        var second = cache.Get(user, Prediction.ToLocalTime(), new HashSet<Guid> { Guid.NewGuid() });

        Assert.Same(first.Profiles, second.Profiles);
        Assert.Same(first.Collaborative, second.Collaborative);
        Assert.Equal(1, cache.CachedCutoffCount);
        Assert.Equal(first.Profiles.Sum(profile => profile.WatchedItems.Count + 1), cache.CachedRowCount);
    }

    [Fact]
    public void TargetExclusions_MatchUncachedReferenceWithoutPoisoningOtherTargetsOrUsers()
    {
        var user = Guid.NewGuid();
        var profiles = Profiles(user);
        var cache = new TrainingHistoryCache(profiles, CancellationToken.None);
        var excluded = new HashSet<Guid> { profiles[0].WatchedItems[0].ItemId };
        var unfiltered = cache.Get(user, Prediction, new HashSet<Guid>());
        var filtered = cache.Get(user, Prediction, excluded);
        var reference = new Collection<UserWatchProfile>(profiles.Select(profile => TrainingHistory.Before(profile, Prediction, profile.UserId == user ? excluded : null)).ToList());

        Assert.Equal(reference.SelectMany(profile => profile.WatchedItems.Select(row => (profile.UserId, row.ItemId))),
            filtered.Profiles.SelectMany(profile => profile.WatchedItems.Select(row => (profile.UserId, row.ItemId))));
        Assert.Equal(CollaborativeFilter.PrecomputeCollaborativeContext(CollaborativeFilter.PrecomputeUserWatchSets(reference)).ItemPopularity.OrderBy(pair => pair.Key), filtered.Collaborative.ItemPopularity.OrderBy(pair => pair.Key));
        foreach (var profile in reference)
        {
            var expected = CollaborativeFilter.BuildCollaborativeMap(profile, reference);
            var actual = CollaborativeFilter.BuildCollaborativeMap(filtered.Profiles.Single(row => row.UserId == profile.UserId), filtered.Profiles, filtered.Collaborative);
            Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
        }

        Assert.Equal(6, unfiltered.Profile.WatchedItems.Count);
        Assert.Same(unfiltered.Profiles, cache.Get(user, Prediction, new HashSet<Guid>()).Profiles);
        Assert.Contains(excluded.Single(), cache.Get(profiles[1].UserId, Prediction, excluded).Profiles[0].WatchedItems.Select(row => row.ItemId));
        Assert.Equal(8, profiles[0].WatchedItems.Count);
    }

    [Fact]
    public void SeriesEpisodeExclusions_RemoveEarlierEpisodesButKeepNeighbourEvidence()
    {
        var user = Guid.NewGuid();
        var series = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var profiles = Profiles(user);
        profiles[0].WatchedItems.Add(new WatchedItemInfo { ItemId = episode, SeriesId = series, ItemType = "Episode", Played = true, LastPlayedDate = Prediction.AddDays(-1) });
        profiles[1].WatchedItems.Add(new WatchedItemInfo { ItemId = episode, SeriesId = series, ItemType = "Episode", Played = true, LastPlayedDate = Prediction.AddDays(-1) });
        var cache = new TrainingHistoryCache(profiles, CancellationToken.None);
        var filtered = cache.Get(user, Prediction, ContentScoring.BuildSeriesExcludeSet(series, profiles[0]));

        Assert.DoesNotContain(filtered.Profile.WatchedItems, row => row.SeriesId == series);
        Assert.DoesNotContain(series, filtered.Collaborative.UserSets[user]);
        Assert.DoesNotContain(episode, filtered.Collaborative.UserSets[user]);
        Assert.Contains(series, filtered.Collaborative.UserSets[profiles[1].UserId]);
        Assert.Equal(1, filtered.Collaborative.ItemPopularity[series]);
        Assert.Contains(episode, cache.Get(user, Prediction, new HashSet<Guid>()).Collaborative.UserSets[user]);
    }

    [Fact]
    public void FutureUndatedAndExactCutoffRows_CannotEnterAReusedHistory()
    {
        var user = Guid.NewGuid();
        var profiles = Profiles(user);
        profiles[0].WatchedItems.Add(Watch(Guid.NewGuid(), Prediction));
        var cache = new TrainingHistoryCache(profiles, CancellationToken.None);
        var current = cache.Get(user, Prediction, new HashSet<Guid>());
        Assert.Equal(6, current.Profile.WatchedItems.Count);
        Assert.All(current.Profile.WatchedItems, row => Assert.True(row.LastPlayedDate < Prediction));
        var earlier = cache.Get(user, Prediction.AddDays(-3), new HashSet<Guid>());
        Assert.Empty(earlier.Profile.WatchedItems);
        Assert.Empty(earlier.Collaborative.UserSets[user]);
    }

    [Fact]
    public void Retention_IsBoundedByCutoffsAndRowsAndEvictionDoesNotChangeReconstruction()
    {
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user };
        for (var i = 0; i < 2000; i++) profile.WatchedItems.Add(Watch(new Guid(i + 1, 0, 0, new byte[8]), Prediction.AddDays(-1)));
        var cache = new TrainingHistoryCache([profile], CancellationToken.None);
        var first = cache.Get(user, Prediction, new HashSet<Guid>());
        for (var i = 1; i <= TrainingHistoryCache.MaxCachedCutoffs + 2; i++)
        {
            var inputs = cache.Get(user, Prediction.AddMinutes(i), new HashSet<Guid>());
            Assert.Equal(first.Profile.WatchedItems, inputs.Profile.WatchedItems);
            Assert.InRange(cache.CachedCutoffCount, 1, TrainingHistoryCache.MaxCachedCutoffs);
            Assert.InRange(cache.CachedRowCount, 1, TrainingHistoryCache.MaxCachedRows);
        }

        var rebuilt = cache.Get(user, Prediction, new HashSet<Guid>());
        Assert.NotSame(first.Profiles, rebuilt.Profiles);
        Assert.Equal(first.Collaborative.ItemPopularity.OrderBy(pair => pair.Key), rebuilt.Collaborative.ItemPopularity.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void OversizedSnapshot_IsUsableButNotRetained()
    {
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user };
        for (var i = 0; i < TrainingHistoryCache.MaxCachedRows; i++) profile.WatchedItems.Add(Watch(new Guid(i + 1, 0, 0, new byte[8]), Prediction.AddDays(-1)));
        var cache = new TrainingHistoryCache([profile], CancellationToken.None);
        var inputs = cache.Get(user, Prediction, new HashSet<Guid>());

        Assert.Equal(TrainingHistoryCache.MaxCachedRows, inputs.Profile.WatchedItems.Count);
        Assert.Equal(0, cache.CachedCutoffCount);
        Assert.Equal(0, cache.CachedRowCount);
    }

    [Fact]
    public void Cancellation_IsObservedEvenOnACacheHitAndFreshBuildsDoNotReuseOldProfiles()
    {
        var user = Guid.NewGuid();
        var profiles = Profiles(user);
        using var cancellation = new CancellationTokenSource();
        var cache = new TrainingHistoryCache(profiles, cancellation.Token);
        cache.Get(user, Prediction, new HashSet<Guid>());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cache.Get(user, Prediction, new HashSet<Guid>()));
        foreach (var row in profiles[0].WatchedItems) row.LastPlayedDate = Prediction.AddDays(1);

        var freshBuild = new TrainingHistoryCache(profiles, CancellationToken.None).Get(user, Prediction, new HashSet<Guid>());
        Assert.Empty(freshBuild.Profile.WatchedItems);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void BuildExamples_50UsersWithSnapshotsAndHistoricalActivity_StaysWithinAllocationBudget()
    {
        var profiles = new Collection<UserWatchProfile>();
        var previous = new List<RecommendationResult>();
        for (var u = 1; u <= 50; u++)
        {
            var user = new Guid(u, 0, 0, new byte[8]);
            var profile = new UserWatchProfile { UserId = user };
            var result = new RecommendationResult { UserId = user, GeneratedAt = Prediction };
            for (var i = 0; i < 30; i++) profile.WatchedItems.Add(Watch(new Guid(i + 1, 1, 0, new byte[8]), Prediction.AddDays(-60 + i)));
            for (var i = 0; i < 20; i++)
            {
                var id = new Guid(i + 1, (short)(u + 1), 0, new byte[8]);
                profile.WatchedItems.Add(Watch(id, Prediction.AddDays(1)));
                result.Recommendations.Add(new RecommendedItem { ItemId = id, ItemType = "Movie", Genres = ["Drama"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = 0.7 } });
            }

            profiles.Add(profile);
            previous.Add(result);
        }

        // Genuine pre-prediction organic activity exercises historical neighbour reconstruction.
        // An allocation budget catches multiplication of that work without relying on wall-clock timings.
        TrainingDataBuilder.BuildExamples(previous, profiles, CancellationToken.None);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var built = TrainingDataBuilder.BuildExamples(previous, profiles, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2750, built.Examples.Count);
        Assert.True(allocated < 100_000_000, $"Historical build allocated {allocated:N0} bytes, exceeding its 100 MB regression budget.");
    }

    private static Collection<UserWatchProfile> Profiles(Guid user)
    {
        var profiles = new Collection<UserWatchProfile> { new() { UserId = user }, new() { UserId = Guid.NewGuid() } };
        for (var i = 0; i < 6; i++)
        {
            var id = Guid.NewGuid();
            foreach (var profile in profiles) profile.WatchedItems.Add(Watch(id, Prediction.AddDays(-2)));
        }

        foreach (var profile in profiles)
        {
            profile.WatchedItems.Add(Watch(Guid.NewGuid(), Prediction.AddDays(1)));
            profile.WatchedItems.Add(Watch(Guid.NewGuid(), null));
        }

        return profiles;
    }

    private static WatchedItemInfo Watch(Guid id, DateTime? date) => new() { ItemId = id, ItemType = "Movie", Played = true, LastPlayedDate = date, Genres = ["Drama"] };
}
