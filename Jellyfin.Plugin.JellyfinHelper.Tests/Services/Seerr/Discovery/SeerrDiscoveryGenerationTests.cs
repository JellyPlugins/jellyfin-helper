using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests GenerateDiscoveryRecommendationsAsync - the largest previously-uncovered method in the discovery module.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryGenerationTests : IDisposable
{
    private readonly Mock<IWatchHistoryService> _history;
    private readonly DiscoveryCacheService _cache;
    private readonly Mock<IDiscoveryFeedbackStore> _feedback;
    private readonly ScriptedHttpHandler _handler;
    private readonly SeerrDiscoveryService _sut;

    public SeerrDiscoveryGenerationTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();

        _handler = new ScriptedHttpHandler();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));

        _history = new Mock<IWatchHistoryService>();
        _history.Setup(h => h.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());
        _history.Setup(h => h.GetSeriesEpisodeCounts())
            .Returns(new Dictionary<Guid, int>());

        var arr = new Mock<IArrIntegrationService>();
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var learned = new LearnedScoringStrategy(null, new Mock<ILogger<LearnedScoringStrategy>>().Object);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy(null, new Mock<ILogger<NeuralScoringStrategy>>().Object);
        var ensemble = new EnsembleScoringStrategy(
            learned, heuristic, neural, null,
            EnsembleScoringStrategy.DefaultAlphaMin,
            EnsembleScoringStrategy.DefaultAlphaMax,
            EnsembleScoringStrategy.DefaultGenrePenaltyFloor,
            new Mock<ILogger<EnsembleScoringStrategy>>().Object);
        var pluginLog = new Mock<IPluginLogService>();
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object);
        _feedback = new Mock<IDiscoveryFeedbackStore>();

        _sut = new SeerrDiscoveryService(
            httpFactory.Object,
            _history.Object,
            arr.Object,
            libraryManager.Object,
            ensemble,
            _cache,
            _feedback.Object,
            pluginLog.Object,
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    public void Dispose()
    {
        _handler.Dispose();
        _cache.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ConfigMissing_ReturnsWithoutError()
    {
        // BUG GUARD: empty SeerrUrl/ApiKey must short-circuit BEFORE fetching profiles. A regression that fetched anyway would spike the DB on every scheduled tick for admins who never configured Seerr.
        Plugin.Instance!.Configuration.SeerrUrl = string.Empty;
        Plugin.Instance!.Configuration.SeerrApiKey = string.Empty;

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_TaskModeDeactivate_ShortCircuits()
    {
        // BUG GUARD: TaskMode.Deactivate MUST short-circuit BEFORE any HTTP or DB work, even with a fully-configured Seerr.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Deactivate;

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_NoActiveUsers_SkipsFeedbackRecording()
    {
        // BUG GUARD: users with zero plays and fewer than 3 favorites produce no
        // preference signal. Task must skip them and NOT record shown-items for
        // non-existent recommendations.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Activate;

        var quietUser = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            UserName = "quiet",
            WatchedMovieCount = 0,
            WatchedEpisodeCount = 0,
            FavoriteCount = 0,
            WatchedItems = []
        };
        _history.Setup(h => h.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { quietUser });

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Once);
        _feedback.Verify(
            f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_CancelledToken_Throws()
    {
        // Cancellation must escape the per-user try/catch - a swallowed cancellation
        // would leave a task registered as still-running in Jellyfin's scheduler.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Activate;

        _history.Setup(h => h.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>
            {
                new()
                {
                    UserId = Guid.NewGuid(),
                    UserName = "active",
                    WatchedMovieCount = 5,
                    FavoriteCount = 5,
                    WatchedItems =
                    [
                        new()
                        {
                            ItemId = Guid.NewGuid(),
                            Name = "seed",
                            ItemType = "Movie",
                            Played = true,
                            PlayCount = 3,
                            Genres = ["Action"]
                        }
                    ]
                }
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The outer cancellation check at the top of the per-user loop (or during `BuildExclusionSetAsync`) MUST fire before any real HTTP work happens.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _sut.GenerateDiscoveryRecommendationsAsync(cts.Token));
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_DryRunMode_DoesNotCallFeedbackRecord()
    {
        // BUG GUARD: DryRun mode must NEVER persist to disk or record shown feedback.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.DryRun;

        _history.Setup(h => h.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedback.Verify(
            f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }
}
