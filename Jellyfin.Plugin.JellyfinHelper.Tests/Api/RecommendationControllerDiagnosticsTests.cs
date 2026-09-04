using System;
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

    [Fact]
    public void GetEnsembleDiagnostics_WithUserId_PerUserModel_ReturnsPerUserDto()
    {
        var userId = Guid.NewGuid();

        var perUserDiagnostics = new EnsembleDiagnostics { Alpha = 0.68, NeuralEnabled = true };
        _mockEngine.Setup(e => e.GetEnsembleDiagnostics(userId)).Returns(perUserDiagnostics);
        // The honest per-user signal comes from HasPerUserModel, NOT a reference comparison of snapshots.
        _mockEngine.Setup(e => e.HasPerUserModel(userId)).Returns(true);
        _mockWatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "Bob" });

        var result = _controller.GetEnsembleDiagnostics(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EnsembleDiagnosticsResponse>(ok.Value);
        Assert.True(dto.Available);
        Assert.True(dto.IsPerUser);
        Assert.Equal("Bob", dto.UserName);
        Assert.Equal(0.68, dto.Alpha);
    }

    [Fact]
    public void GetEnsembleDiagnostics_WithUserId_ColdStartUser_ReturnsGlobalFallbackDto()
    {
        var userId = Guid.NewGuid();

        // Cold-start: the engine returns the GLOBAL snapshot for this user (no per-user model) and
        // HasPerUserModel is false. IsPerUser must be false so the UI shows the global-fallback label.
        // This is the regression guard for the old ReferenceEquals bug that reported per-user for everyone.
        _mockEngine.Setup(e => e.GetEnsembleDiagnostics(userId))
            .Returns(new EnsembleDiagnostics { Alpha = 0.4, NeuralEnabled = true });
        _mockEngine.Setup(e => e.HasPerUserModel(userId)).Returns(false);
        _mockWatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "NewUser" });

        var result = _controller.GetEnsembleDiagnostics(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EnsembleDiagnosticsResponse>(ok.Value);
        Assert.True(dto.Available);
        Assert.False(dto.IsPerUser);
        Assert.Equal(0.4, dto.Alpha);
    }

    [Fact]
    public void GetEnsembleDiagnostics_NoUserId_ReturnsGlobalDto()
    {
        var globalDiagnostics = new EnsembleDiagnostics { Alpha = 0.5, NeuralEnabled = true };
        _mockEngine.Setup(e => e.GetEnsembleDiagnostics()).Returns(globalDiagnostics);

        var result = _controller.GetEnsembleDiagnostics(userId: null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EnsembleDiagnosticsResponse>(ok.Value);
        Assert.True(dto.Available);
        Assert.False(dto.IsPerUser);
        Assert.Null(dto.UserName);
        Assert.Equal(0.5, dto.Alpha);

        // Global path must use the parameterless engine call and never the per-user overload.
        _mockEngine.Verify(e => e.GetEnsembleDiagnostics(), Times.Once);
        _mockEngine.Verify(e => e.GetEnsembleDiagnostics(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetEnsembleDiagnostics_WithUserId_Deactivated_Returns503()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate });

        var result = _controller.GetEnsembleDiagnostics(Guid.NewGuid());

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        _mockEngine.Verify(e => e.GetEnsembleDiagnostics(), Times.Never);
        _mockEngine.Verify(e => e.GetEnsembleDiagnostics(It.IsAny<Guid>()), Times.Never);
    }
}
