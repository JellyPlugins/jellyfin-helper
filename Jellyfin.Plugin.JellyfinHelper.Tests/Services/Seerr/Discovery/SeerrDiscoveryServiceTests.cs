using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

public class SeerrDiscoveryServiceTests
{
    private static SeerrDiscoveryService CreateService()
    {
        var factory = new Mock<System.Net.Http.IHttpClientFactory>();
        var history = new Mock<IWatchHistoryService>();
        var arr = new Mock<IArrIntegrationService>();
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
        var cacheLogger = new Mock<ILogger<DiscoveryCacheService>>();
        var cache = new DiscoveryCacheService(pluginLog.Object, cacheLogger.Object);
        var feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        var logger = new Mock<ILogger<SeerrDiscoveryService>>();
        return new SeerrDiscoveryService(
            factory.Object, history.Object, arr.Object,
            ensemble, cache, feedbackStore.Object, pluginLog.Object, logger.Object);
    }

    [Fact]
    public async Task SubmitRequestAsync_InvalidTmdbId_ReturnsFalse()
    {
        var service = CreateService();
        var (success, _) = await service.SubmitRequestAsync(0, "movie", null, null, null, null, CancellationToken.None);
        Assert.False(success);
    }

    [Fact]
    public async Task SubmitRequestAsync_InvalidMediaType_ReturnsFalse()
    {
        var service = CreateService();
        var (success, _) = await service.SubmitRequestAsync(123, "invalid", null, null, null, null, CancellationToken.None);
        Assert.False(success);
    }

    [Fact]
    public async Task SubmitRequestAsync_SeerrNotConfigured_ReturnsFalse()
    {
        var service = CreateService();
        var (success, message) = await service.SubmitRequestAsync(123, "movie", null, null, null, null, CancellationToken.None);
        Assert.False(success);
        Assert.Contains("not configured", message);
    }
}