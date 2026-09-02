using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for RecommendationController.GetEnsembleDiagnostics: 200 with a populated DTO when the engine returns
///     diagnostics, the Available=false path when the engine returns null, and 503 when recommendations are deactivated.
/// </summary>
public class RecommendationControllerDiagnosticsTests
{
    private readonly Mock<IRecommendationCacheService> _mockCache;
    private readonly Mock<IPluginConfigurationService> _mockConfigService;
    private readonly RecommendationController _controller;
    private readonly Mock<IRecommendationEngine> _mockEngine;
    private readonly Mock<IWatchHistoryService> _mockWatchHistory;

    public RecommendationControllerDiagnosticsTests()
    {
        _mockEngine = new Mock<IRecommendationEngine>();
        _mockCache = new Mock<IRecommendationCacheService>();
        _mockWatchHistory = new Mock<IWatchHistoryService>();
        _mockConfigService = new Mock<IPluginConfigurationService>();

        // Default: recommendations enabled (Activate mode)
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate });

        _controller = new RecommendationController(
            _mockEngine.Object,
            _mockCache.Object,
            _mockWatchHistory.Object,
            _mockConfigService.Object);
    }

    [Fact]
    public void GetEnsembleDiagnostics_EngineReturnsDiagnostics_ReturnsPopulatedDto()
    {
        var diagnostics = new EnsembleDiagnostics
        {
            Alpha = 0.55,
            NeuralBeta = 0.2,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = -3.0,
            EffectiveSigmoidMidpoint = 47.0,
            Trend = EnsembleScoringStrategy.MetricsTrend.Improving,
            TrainingExampleCount = 240,
            MetricsHistoryCount = 6,
            AlphaMin = 0.3,
            AlphaMax = 0.75,
            NeuralEnabled = true
        };
        _mockEngine.Setup(e => e.GetEnsembleDiagnostics()).Returns(diagnostics);

        var result = _controller.GetEnsembleDiagnostics();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EnsembleDiagnosticsResponse>(ok.Value);
        Assert.True(dto.Available);
        Assert.Equal(0.55, dto.Alpha);
        Assert.Equal(0.2, dto.NeuralBeta);
        Assert.False(dto.QualityGateFrozen);
        Assert.Equal(-3.0, dto.SigmoidMidpointOffset);
        Assert.Equal(47.0, dto.EffectiveSigmoidMidpoint);
        Assert.Equal("Improving", dto.Trend);
        Assert.Equal(240, dto.TrainingExampleCount);
        Assert.Equal(6, dto.MetricsHistoryCount);
        Assert.Equal(0.3, dto.AlphaMin);
        Assert.Equal(0.75, dto.AlphaMax);
        Assert.True(dto.NeuralEnabled);
    }

    [Fact]
    public void GetEnsembleDiagnostics_EngineReturnsNull_ReturnsUnavailableDto()
    {
        _mockEngine.Setup(e => e.GetEnsembleDiagnostics()).Returns((EnsembleDiagnostics?)null);

        var result = _controller.GetEnsembleDiagnostics();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EnsembleDiagnosticsResponse>(ok.Value);
        Assert.False(dto.Available);
    }

    [Fact]
    public void GetEnsembleDiagnostics_Deactivated_Returns503()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate });

        var result = _controller.GetEnsembleDiagnostics();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        _mockEngine.Verify(e => e.GetEnsembleDiagnostics(), Times.Never);
    }
}
