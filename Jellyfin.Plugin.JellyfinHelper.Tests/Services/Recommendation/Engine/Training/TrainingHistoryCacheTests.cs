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

        Assert.Same(first.Profile, second.Profile);
        Assert.Same(first.Collaborative, second.Collaborative);
        Assert.Equal(1, cache.CachedCutoffCount);
        Assert.Equal(first.Collaborative.UserSets.Values.Sum(set => set.Count + 1) + first.Profile.WatchedItems.Count, cache.CachedRowCount);
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

        Assert.Equal(reference.Single(profile => profile.UserId == user).WatchedItems.Select(row => row.ItemId), filtered.Profile.WatchedItems.Select(row => row.ItemId));
        Assert.Equal(CollaborativeFilter.PrecomputeCollaborativeContext(CollaborativeFilter.PrecomputeUserWatchSets(reference)).ItemPopularity.OrderBy(pair => pair.Key), filtered.Collaborative.ItemPopularity.OrderBy(pair => pair.Key));
        foreach (var profile in reference)
        {
            var expected = CollaborativeFilter.BuildCollaborativeMap(profile, reference);
            var actual = CollaborativeFilter.BuildCollaborativeMap(profile.UserId, filtered.Collaborative);
            Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
        }

        Assert.Equal(6, unfiltered.Profile.WatchedItems.Count);
        Assert.Same(unfiltered.Profile, cache.Get(user, Prediction, new HashSet<Guid>()).Profile);
        Assert.Contains(excluded.Single(), cache.Get(profiles[1].UserId, Prediction, excluded).Collaborative.UserSets[user]);
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
            Assert.Equal(first.Profile.WatchedItems.Select(row => (row.ItemId, row.LastPlayedDate)), inputs.Profile.WatchedItems.Select(row => (row.ItemId, row.LastPlayedDate)));
            Assert.InRange(cache.CachedCutoffCount, 1, TrainingHistoryCache.MaxCachedCutoffs);
            Assert.InRange(cache.CachedRowCount, 1, TrainingHistoryCache.MaxCachedRows);
        }

        var rebuilt = cache.Get(user, Prediction, new HashSet<Guid>());
        Assert.NotSame(first.Profile, rebuilt.Profile);
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
    public void IncrementalContexts_MatchMaterializedHistoryAcrossOutOfOrderCutoffsAndDuplicateSeriesReferences()
    {
        var user = Guid.NewGuid();
        var profiles = Profiles(user);
        var series = Guid.NewGuid();
        foreach (var profile in profiles)
        {
            for (var i = 0; i < 4; i++) profile.WatchedItems.Insert(0, new WatchedItemInfo { ItemId = new Guid(i + 1, 0, 0, new byte[8]), SeriesId = series, Played = true, LastPlayedDate = Prediction.AddHours(-i - 1) });
        }

        var cache = new TrainingHistoryCache(profiles, CancellationToken.None);
        var first = cache.Get(user, Prediction, new HashSet<Guid>());
        foreach (var cutoff in new[] { Prediction.AddDays(-4), Prediction.AddHours(-3), Prediction.AddDays(3), Prediction.AddHours(-1), Prediction })
        {
            var actual = cache.Get(user, cutoff, new HashSet<Guid>());
            var reference = new Collection<UserWatchProfile>(profiles.Select(profile => TrainingHistory.Before(profile, cutoff)).ToList());
            var expectedContext = CollaborativeFilter.PrecomputeCollaborativeContext(CollaborativeFilter.PrecomputeUserWatchSets(reference));
            Assert.Equal(expectedContext.ItemPopularity.OrderBy(pair => pair.Key), actual.Collaborative.ItemPopularity.OrderBy(pair => pair.Key));
            foreach (var profile in reference)
            {
                Assert.Equal(expectedContext.UserSets[profile.UserId].Order(), actual.Collaborative.UserSets[profile.UserId].Order());
                Assert.Equal(CollaborativeFilter.BuildCollaborativeMap(profile, reference, expectedContext).OrderBy(pair => pair.Key), CollaborativeFilter.BuildCollaborativeMap(profile.UserId, actual.Collaborative).OrderBy(pair => pair.Key));
            }
        }

        Assert.Equal(2, first.Collaborative.ItemPopularity[series]);
        Assert.Contains(series, first.Collaborative.UserSets[user]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Performance")]
    public void DistinctPerUserCutoffs_DoNotMultiplyNeighbourReconstructionAllocations(bool interleaved)
    {
        var profiles = new Collection<UserWatchProfile>();
        for (var u = 0; u < 50; u++)
        {
            var profile = new UserWatchProfile { UserId = new Guid(u + 1, 0, 0, new byte[8]) };
            for (var i = 0; i < 40; i++) profile.WatchedItems.Add(Watch(new Guid(i + 1, (short)(u + 1), 0, new byte[8]), Prediction.AddMinutes(-(interleaved ? i * 50.0 + u + 1 : u * 40.0 + i + 1))));
            profiles.Add(profile);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var cache = new TrainingHistoryCache(profiles, CancellationToken.None);
        foreach (var profile in profiles)
        {
            foreach (var row in profile.WatchedItems)
            {
                var inputs = cache.Get(profile.UserId, row.LastPlayedDate!.Value, new HashSet<Guid>());
                Assert.DoesNotContain(row.ItemId, inputs.Collaborative.UserSets[profile.UserId]);
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 40_000_000, $"Distinct-cutoff contexts allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void IndexedViews_ObeySetAndDictionaryContractsAtExclusiveCutoffs()
    {
        var user = Guid.NewGuid();
        var known = Guid.NewGuid();
        var future = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { new() { UserId = user, WatchedItems = [Watch(known, Prediction.AddDays(-1)), Watch(future, Prediction)] } };
        var cache = new TrainingHistoryCache(profiles, CancellationToken.None);
        var context = cache.Get(user, Prediction, new HashSet<Guid>()).Collaborative;
        var set = context.UserSets[user];
        Assert.True(set.SetEquals([known, known]));
        Assert.True(set.IsSubsetOf([known, future]));
        Assert.True(set.IsProperSubsetOf([known, future]));
        Assert.True(set.IsSupersetOf([known]));
        Assert.True(set.IsProperSupersetOf([]));
        Assert.True(set.Overlaps([known]));
        Assert.False(set.Overlaps([future]));
        Assert.Equal(new[] { known }, ((System.Collections.IEnumerable)set).Cast<Guid>());
        Assert.Single(context.ItemPopularity);
        Assert.Equal(new[] { known }, context.ItemPopularity.Keys);
        Assert.Equal(new[] { 1 }, context.ItemPopularity.Values);
        Assert.True(context.ItemPopularity.TryGetValue(known, out var knownCount));
        Assert.Equal(1, knownCount);
        Assert.False(context.ItemPopularity.ContainsKey(future));
        Assert.False(context.ItemPopularity.TryGetValue(future, out var futureCount));
        Assert.Equal(0, futureCount);
        Assert.Throws<KeyNotFoundException>(() => context.ItemPopularity[future]);
        Assert.Throws<KeyNotFoundException>(() => context.ItemPopularity[Guid.NewGuid()]);
        Assert.Single(((System.Collections.IEnumerable)context.ItemPopularity).Cast<KeyValuePair<Guid, int>>());

        var excluded = cache.Get(user, Prediction, new HashSet<Guid> { known }).Collaborative.ItemPopularity;
        Assert.Empty(excluded);
        Assert.Empty(excluded.Keys);
        Assert.Empty(excluded.Values);
        Assert.False(excluded.ContainsKey(known));
        Assert.False(excluded.TryGetValue(future, out _));
        Assert.Throws<KeyNotFoundException>(() => excluded[known]);
        Assert.Empty(((System.Collections.IEnumerable)excluded).Cast<KeyValuePair<Guid, int>>());
        Assert.True(context.ItemPopularity.TryGetValue(known, out var unchangedCount));
        Assert.Equal(knownCount, unchangedCount);
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
            var result = new RecommendationResult { UserId = user, GeneratedAt = Prediction, TrainingContrasts = [] };
            for (var i = 0; i < 30; i++) profile.WatchedItems.Add(Watch(new Guid(i + 1, 1, 0, new byte[8]), Prediction.AddDays(-60 + i)));
            for (var i = 0; i < 20; i++)
            {
                var id = new Guid(i + 1, (short)(u + 1), 0, new byte[8]);
                profile.WatchedItems.Add(Watch(id, Prediction.AddDays(1)));
                result.Recommendations.Add(new RecommendedItem { ItemId = id, ItemType = "Movie", Genres = ["Drama"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = 0.7 } });
            }

            profiles.Add(profile);
            for (var i = 0; i < 5; i++) result.TrainingContrasts!.Add(new RecommendedItem { ItemId = new Guid(i + 100, (short)(u + 80), 0, new byte[8]), ItemType = "Movie", ScoringFeatures = new CandidateFeatures { GenreSimilarity = 0.7 } });
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
