using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

[Collection("ConfigOverride")]
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
        var cache = new DiscoveryCacheService(pluginLog.Object, cacheLogger.Object, filePath: Path.GetTempFileName());
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
        var (success, message) = await service.SubmitRequestAsync(0, "movie", null, null, null, null, CancellationToken.None);
        Assert.False(success);
        Assert.Contains("TMDb", message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_InvalidMediaType_ReturnsFalse()
    {
        var service = CreateService();
        var (success, message) = await service.SubmitRequestAsync(123, "invalid", null, null, null, null, CancellationToken.None);
        Assert.False(success);
        Assert.Contains("mediaType", message);
    }

    [Fact]
    public async Task SubmitRequestAsync_SeerrNotConfigured_ReturnsFalse()
    {
        // Snapshot prior state so we can restore it after the test (Plugin.Instance is a process-wide singleton)
        var prevUrl = Plugin.Instance?.Configuration?.SeerrUrl;
        var prevKey = Plugin.Instance?.Configuration?.SeerrApiKey;
        try
        {
            // Ensure "not configured" state regardless of prior test execution order
            if (Plugin.Instance?.Configuration != null)
            {
                Plugin.Instance.Configuration.SeerrUrl = string.Empty;
                Plugin.Instance.Configuration.SeerrApiKey = string.Empty;
            }

            var service = CreateService();
            var (success, message) = await service.SubmitRequestAsync(123, "movie", null, null, null, null, CancellationToken.None);
            Assert.False(success);
            Assert.Contains("not configured", message);
        }
        finally
        {
            if (Plugin.Instance?.Configuration != null)
            {
                Plugin.Instance.Configuration.SeerrUrl = prevUrl!;
                Plugin.Instance.Configuration.SeerrApiKey = prevKey!;
            }
        }
    }

    [Fact]
    public async Task SubmitRequestAsync_ApiKeyWithCrlf_ReturnsFalse()
    {
        var prevUrl = Plugin.Instance?.Configuration?.SeerrUrl;
        var prevKey = Plugin.Instance?.Configuration?.SeerrApiKey;
        try
        {
            if (Plugin.Instance?.Configuration != null)
            {
                Plugin.Instance.Configuration.SeerrUrl = "http://seerr.local";
                Plugin.Instance.Configuration.SeerrApiKey = "key\r\nX-Injected: evil";
            }

            var service = CreateService();
            var (success, message) = await service.SubmitRequestAsync(
                123, "movie", null, null, null, null, CancellationToken.None);

            // CRLF guard fires inside CreateClient; caller wraps it as a config error.
            Assert.False(success);
            Assert.False(string.IsNullOrEmpty(message));
        }
        finally
        {
            if (Plugin.Instance?.Configuration != null)
            {
                Plugin.Instance.Configuration.SeerrUrl = prevUrl!;
                Plugin.Instance.Configuration.SeerrApiKey = prevKey!;
            }
        }
    }
}