using System.Collections.ObjectModel;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

public sealed class ReplayIdentityRegressionTests
{
    [Theory]
    [InlineData(700, "movie", true)]
    [InlineData(800, "movie", false)]
    [InlineData(0, "movie", false)]
    [InlineData(700, "tv", false)]
    public void AnAllowedDuplicateCannotProveTheOriginalPhysicalCopiesCapturedProviderIdentity(int currentOriginalProvider, string currentOriginalType, bool retain)
    {
        var user = Guid.NewGuid();
        var chosen = Guid.NewGuid();
        var originalCopy = Guid.NewGuid();
        var otherCopy = Guid.NewGuid();
        var alternative = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var chosenKey = TrainingExample.CreateProviderObservationId(user, 900, "movie");
        var originalKey = TrainingExample.CreateProviderObservationId(user, 700, "movie");
        var alternativeKey = TrainingExample.CreateProviderObservationId(user, 801, "movie");
        var captured = new TrainingExample
        {
            UserId = user, ObservationId = chosenKey, HasStableObservationId = true, IsObserved = true,
            Features = new CandidateFeatures { GenreSimilarity = .7 }, Label = .85, GeneratedAtUtc = now.AddDays(-3),
            Choice = new RecommendationChoiceEvidence
            {
                ChosenObservationId = chosenKey, ChosenItemId = chosen, ChosenFeatures = new CandidateFeatures { GenreSimilarity = .7 },
                AvailableAtUtc = now.AddDays(-5), RecommendedAtUtc = now.AddDays(-3), ChosenAtUtc = now.AddDays(-2),
                Alternatives = [new(originalKey, new CandidateFeatures { GenreSimilarity = .6 }) { LibraryItemId = originalCopy }, new(alternativeKey, new CandidateFeatures { GenreSimilarity = .5 }) { LibraryItemId = alternative }]
            }
        };
        var history = new RecommendationChoiceHistory();
        history.Merge([captured], new HashSet<Guid> { user }, now);
        var profile = new UserWatchProfile
        {
            UserId = user, AccessibleItemIds = new HashSet<Guid> { chosen, originalCopy, otherCopy, alternative },
            WatchedItems = [new WatchedItemInfo { ItemId = chosen, TmdbId = 900, ItemType = "Movie", Played = true, LastPlayedDate = now.AddDays(-2), Genres = ["Drama"] }]
        };
        var watch = new Mock<IWatchHistoryService>();
        watch.Setup(value => value.GetAllUserWatchProfiles()).Returns(new Collection<UserWatchProfile> { profile });
        watch.Setup(value => value.GetAllUserIds()).Returns([user]);
        var feedback = new Mock<IDiscoveryFeedbackStore>();
        feedback.Setup(value => value.LoadAll()).Returns([]);
        var titles = new Dictionary<Guid, (int TmdbId, string MediaType)> { [chosen] = (900, "movie"), [otherCopy] = (700, "movie"), [alternative] = (801, "movie") };
        if (currentOriginalProvider > 0) titles[originalCopy] = (currentOriginalProvider, currentOriginalType);
        var metadata = new LibraryItemMetadata(new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), titles.ToDictionary(pair => pair.Key, pair => pair.Value.TmdbId))
        {
            AllowedItemIds = profile.AccessibleItemIds, AllowedTitleKeys = titles.Values.ToHashSet(), TitleKeysByItemId = titles
        };
        using var service = new TrainingService(watch.Object, feedback.Object, Mock.Of<IPluginLogService>(), Mock.Of<ILogger>()) { ChoiceHistory = history };
        var batch = typeof(TrainingService).GetMethod("BuildTrainingExamples", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [new HeuristicScoringStrategy(), new[] { new RecommendationResult { UserId = user } }, null, null, metadata, TestContext.Current.CancellationToken])!;
        var examples = (IReadOnlyList<TrainingExample>)batch.GetType().GetProperty("Examples")!.GetValue(batch)!;
        var choice = Assert.Single(examples, row => row.Choice is not null).Choice!;
        Assert.Equal(retain ? 2 : 1, choice.Alternatives.Length);
        Assert.Equal(retain, choice.Alternatives.Any(item => item.ObservationId == originalKey));
        if (!retain) Assert.DoesNotContain(examples, row => row.IsContrastSample && row.ObservationId == originalKey);
        Assert.Equal(2, captured.Choice!.Alternatives.Length);
        Assert.Equal(now.AddDays(-3), choice.RecommendedAtUtc);
        Assert.True(ScoringHelper.IsContentEligible(captured.Choice.Alternatives[0].Features));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedEpisodeChoicesResolveOnlyTheirOwnParentAndKeepOriginalPredictionInputs(bool libraryAlias)
    {
        var user = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var series = Guid.NewGuid();
        var alternative = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var chosenKey = libraryAlias ? TrainingExample.CreateLibraryObservationId(user, series) : TrainingExample.CreateProviderObservationId(user, 900, "tv");
        var alternativeKey = TrainingExample.CreateProviderObservationId(user, 801, "movie");
        var captured = new TrainingExample
        {
            UserId = user, ObservationId = chosenKey, HasStableObservationId = true, IsObserved = true,
            Features = new CandidateFeatures { GenreSimilarity = .7 }, Label = .85, GeneratedAtUtc = now.AddDays(-3),
            Choice = new RecommendationChoiceEvidence
            {
                ChosenObservationId = chosenKey, ChosenItemId = episode, ChosenFeatures = new CandidateFeatures { GenreSimilarity = .7 },
                AvailableAtUtc = now.AddDays(-5), RecommendedAtUtc = now.AddDays(-3), ChosenAtUtc = now.AddDays(-2),
                Alternatives = [new(alternativeKey, new CandidateFeatures { GenreSimilarity = .6 }) { LibraryItemId = alternative }]
            }
        };
        var history = new RecommendationChoiceHistory();
        history.Merge([captured], new HashSet<Guid> { user }, now);
        var profile = new UserWatchProfile
        {
            UserId = user, AccessibleItemIds = new HashSet<Guid> { episode, series, alternative },
            WatchedItems = [new WatchedItemInfo { ItemId = episode, SeriesId = series, SeriesTmdbId = libraryAlias ? 0 : 900, ItemType = "Episode", Played = true, LastPlayedDate = now.AddDays(-2), Genres = ["Drama"] }]
        };
        var watch = new Mock<IWatchHistoryService>();
        watch.Setup(value => value.GetAllUserWatchProfiles()).Returns(new Collection<UserWatchProfile> { profile });
        watch.Setup(value => value.GetAllUserIds()).Returns([user]);
        var feedback = new Mock<IDiscoveryFeedbackStore>();
        feedback.Setup(value => value.LoadAll()).Returns([]);
        var titles = new Dictionary<Guid, (int TmdbId, string MediaType)> { [alternative] = (801, "movie") };
        if (!libraryAlias) titles[series] = (900, "tv");
        var metadata = new LibraryItemMetadata(new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), titles.ToDictionary(pair => pair.Key, pair => pair.Value.TmdbId))
        {
            AllowedItemIds = profile.AccessibleItemIds, AllowedTitleKeys = titles.Values.ToHashSet(), TitleKeysByItemId = titles
        };
        using var service = new TrainingService(watch.Object, feedback.Object, Mock.Of<IPluginLogService>(), Mock.Of<ILogger>()) { ChoiceHistory = history };
        var batch = typeof(TrainingService).GetMethod("BuildTrainingExamples", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [new HeuristicScoringStrategy(), new[] { new RecommendationResult { UserId = user } }, null, null, metadata, TestContext.Current.CancellationToken])!;
        var examples = (IReadOnlyList<TrainingExample>)batch.GetType().GetProperty("Examples")!.GetValue(batch)!;
        var replay = Assert.Single(examples, row => row.Choice is not null);
        Assert.Equal(chosenKey, replay.ObservationId);
        Assert.Equal(episode, replay.Choice!.ChosenItemId);
        Assert.Equal(.7, replay.Choice.ChosenFeatures.GenreSimilarity);
        Assert.Equal(now.AddDays(-3), replay.Choice.RecommendedAtUtc);
        Assert.Equal(alternative, Assert.Single(replay.Choice.Alternatives).LibraryItemId);
        Assert.Equal(.7, captured.Features.GenreSimilarity);
    }

    [Theory]
    [InlineData(900, "tv", true, false, false, true)]
    [InlineData(800, "tv", true, false, false, false)]
    [InlineData(0, "tv", true, false, false, false)]
    [InlineData(900, "movie", true, false, false, false)]
    [InlineData(900, "tv", false, false, false, false)]
    [InlineData(900, "tv", true, true, false, false)]
    [InlineData(900, "tv", true, false, true, true)]
    [InlineData(0, "tv", true, false, true, true)]
    public void ParentAliasChecksCannotBorrowAnotherUsersProfileOrAnotherTypedCopy(int parentProvider, string parentType, bool ownParent, bool excluded, bool libraryAlias, bool expected)
    {
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var series = Guid.NewGuid();
        var profiles = new Dictionary<Guid, UserWatchProfile>
        {
            [user] = new() { UserId = user, WatchedItems = [new() { ItemId = episode, SeriesId = ownParent ? series : null, ItemType = "Episode" }] },
            [otherUser] = new() { UserId = otherUser, WatchedItems = [new() { ItemId = episode, SeriesId = series, ItemType = "Episode" }] }
        };
        var titles = new Dictionary<Guid, (int, string)> { [Guid.NewGuid()] = (900, "tv") };
        if (parentProvider > 0) titles[series] = (parentProvider, parentType);
        var metadata = new LibraryItemMetadata(new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<Guid>>())
        {
            TitleKeysByItemId = titles, ExcludedTitleKeys = excluded ? new HashSet<(int, string)> { (parentProvider, parentType) } : new HashSet<(int, string)>()
        };
        var permission = (Func<Guid, Guid, Guid, bool>)typeof(TrainingService).GetMethod("CapturedItemPermission", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [profiles, metadata, TestContext.Current.CancellationToken])!;
        var observation = libraryAlias ? TrainingExample.CreateLibraryObservationId(user, series) : TrainingExample.CreateProviderObservationId(user, 900, "tv");
        Assert.Equal(expected, permission(user, episode, observation));
    }

    [Fact]
    public void LazyParentLookupHonorsCancellationWithoutScanningUnreferencedProfiles()
    {
        var user = Guid.NewGuid();
        var series = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var profiles = new Dictionary<Guid, UserWatchProfile> { [user] = new() { UserId = user, WatchedItems = [new() { ItemId = episode, SeriesId = series, ItemType = "Episode" }] } };
        var titles = new Dictionary<Guid, (int, string)> { [series] = (900, "tv") };
        var metadata = new LibraryItemMetadata(new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<Guid>>()) { TitleKeysByItemId = titles };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var permission = (Func<Guid, Guid, Guid, bool>)typeof(TrainingService).GetMethod("CapturedItemPermission", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [profiles, metadata, cancellation.Token])!;
        Assert.True(permission(user, series, TrainingExample.CreateProviderObservationId(user, 900, "tv")));
        Assert.Throws<OperationCanceledException>(() => permission(user, episode, TrainingExample.CreateProviderObservationId(user, 900, "tv")));
    }
}
