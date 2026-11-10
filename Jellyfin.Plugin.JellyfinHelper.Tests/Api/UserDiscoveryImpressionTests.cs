using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>Impressions require authenticated ownership, actual server delivery, bounded payloads and durable storage.</summary>
[Collection("ConfigOverride")]
public sealed class UserDiscoveryImpressionTests : IDisposable
{
    private readonly string _cachePath = Path.GetTempFileName();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly Mock<IDiscoveryFeedbackStore> _feedback = new();
    private readonly Mock<ISeerrDiscoveryService> _discovery = new();
    private readonly PluginConfiguration _configuration = new() { DiscoveryUserAccessEnabled = true, RecommendationsTaskMode = TaskMode.Activate };
    private readonly DiscoveryCacheService _cache;

    public UserDiscoveryImpressionTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.DiscoveryUserAccessEnabled = true;
        _cache = new DiscoveryCacheService(Mock.Of<IPluginLogService>(), NullLogger<DiscoveryCacheService>.Instance, filePath: _cachePath);
        _discovery.SetupGet(service => service.MaxVisiblePerUser).Returns(10);
        _discovery.Setup(service => service.FilterConsumedItems(It.IsAny<Guid>(), It.IsAny<DiscoveryResult>())).Returns((Guid _, DiscoveryResult result) => result);
        _discovery.Setup(service => service.ReconcileRequestedItemsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _feedback.Setup(store => store.GetDismissedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
        _feedback.Setup(store => store.GetRequestedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _memory.Dispose();
        File.Delete(_cachePath);
        ControllerTestFactory.ResetPluginConfiguration();
    }

    [Fact]
    public async Task Impressions_UseServerFeaturesAndAuthenticatedUserAfterDelivery()
    {
        var user = Guid.NewGuid();
        var generation = DateTime.UtcNow.AddDays(-1);
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, GeneratedAt = generation, Recommendations = [new DiscoveryRecommendation { TmdbId = 1, MediaType = "movie", ScoringFeatures = new CandidateFeatures { GenreSimilarity = 0.3 } }] }]));
        var controller = Controller(user);
        var response = await Deliver(controller);
        Assert.IsType<NoContentResult>(controller.RecordImpressions(Payload(1, response.DeliveryId)));

        _feedback.Verify(store => store.RecordImpressions(user,
            It.Is<IReadOnlyList<DiscoveryRecommendation>>(items => items.Count == 1 && items[0].TmdbId == 1 && BitConverter.DoubleToInt64Bits(items[0].ScoringFeatures!.GenreSimilarity) == BitConverter.DoubleToInt64Bits(0.3)), generation), Times.Once);
    }

    [Fact]
    public async Task Delivery_RedactsFeaturePayloadWithoutRemovingPersistedServerEvidence()
    {
        var user = Guid.NewGuid();
        var delivered = await SaveAndDeliver(user, DateTime.UtcNow, 0.3);
        Assert.Null(delivered.Recommendations.Single().ScoringFeatures);
        var wire = JsonSerializer.Serialize(delivered);
        Assert.DoesNotContain(nameof(CandidateFeatures.CollaborativeScore), wire);
        Assert.DoesNotContain(nameof(CandidateFeatures.GenreSimilarity), wire);
        var cached = Assert.Single(await _cache.LoadAsync(CancellationToken.None));
        Assert.NotNull(cached.Recommendations.Single().ScoringFeatures);
        Assert.IsType<NoContentResult>(Controller(user).RecordImpressions(Payload(1, delivered.DeliveryId)));
        _feedback.Verify(store => store.RecordImpressions(user, It.Is<IReadOnlyList<DiscoveryRecommendation>>(items => items.Single().ScoringFeatures != null), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task GeneratedButNotDeliveredPoolTail_CannotBeConfirmed()
    {
        var user = Guid.NewGuid();
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, Recommendations = Enumerable.Range(1, 20).Select(id => new DiscoveryRecommendation { TmdbId = id }).ToList() }]));
        var controller = Controller(user);
        var response = await Deliver(controller);
        Assert.IsType<BadRequestObjectResult>(controller.RecordImpressions(Payload(11, response.DeliveryId)));
        _feedback.Verify(store => store.RecordImpressions(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task OtherUsersDelivery_CannotAuthorizeAnImpression()
    {
        var first = Guid.NewGuid();
        Assert.True(_cache.Save([new DiscoveryResult { UserId = first, Recommendations = [new DiscoveryRecommendation { TmdbId = 1 }] }]));
        var response = await Deliver(Controller(first));
        Assert.IsType<BadRequestObjectResult>(Controller(Guid.NewGuid()).RecordImpressions(Payload(1, response.DeliveryId)));
    }

    [Fact]
    public void MissingClaimAndDisabledAccess_AreRejected()
    {
        Assert.IsType<UnauthorizedResult>(Controller(null).RecordImpressions(Payload(1)));
        _configuration.DiscoveryUserAccessEnabled = false;
        Plugin.Instance!.Configuration.DiscoveryUserAccessEnabled = false;
        Assert.Equal(403, Assert.IsType<ObjectResult>(Controller(Guid.NewGuid()).RecordImpressions(Payload(1))).StatusCode);
    }

    [Fact]
    public void InactiveTask_DoesNotPersistAutomaticFeedback()
    {
        _configuration.RecommendationsTaskMode = TaskMode.DryRun;
        var result = Assert.IsType<OkObjectResult>(Controller(Guid.NewGuid()).RecordImpressions(Payload(1)));
        Assert.False(Assert.IsType<RequestResult>(result.Value).Success);
        _feedback.Verify(store => store.RecordImpressions(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void PayloadSize_IsBoundedByValidationAndController(int count)
    {
        var payload = new DiscoveryImpressionsDto { DeliveryId = Guid.NewGuid(), Items = Enumerable.Range(1, count).Select(id => new DiscoveryDismissDto { TmdbId = id }).ToArray() };
        var validation = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(payload, new ValidationContext(payload), validation, validateAllProperties: true));
        Assert.IsType<BadRequestObjectResult>(Controller(Guid.NewGuid()).RecordImpressions(payload));
    }

    [Fact]
    public async Task PersistenceFailure_IsNotAcknowledgedAsRecorded()
    {
        var user = Guid.NewGuid();
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 1 }] }]));
        var controller = Controller(user);
        var response = await Deliver(controller);
        _feedback.Setup(store => store.RecordImpressions(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime>())).Throws(new IOException("write failed"));
        Assert.Equal(503, Assert.IsType<ObjectResult>(controller.RecordImpressions(Payload(1, response.DeliveryId))).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OverlappingDeliveries_KeepExactSnapshotsRegardlessOfTimestampsOrArrivalOrder(bool sameGeneration, bool newestFirst)
    {
        var user = Guid.NewGuid();
        var firstGeneration = DateTime.UtcNow.AddDays(-2);
        var secondGeneration = sameGeneration ? firstGeneration : firstGeneration.AddDays(1);
        var first = await SaveAndDeliver(user, firstGeneration, 0.3);
        var second = await SaveAndDeliver(user, secondGeneration, 0.8);
        Assert.NotEqual(first.DeliveryId, second.DeliveryId);

        // Neither a changed source/cache nor a modified API response can rewrite retained evidence.
        Assert.Null(first.Recommendations[0].ScoringFeatures);
        Assert.Null(second.Recommendations[0].ScoringFeatures);
        first.Recommendations[0].Title = "changed response";
        second.Recommendations[0].Score = 0.99;
        var captured = new List<(double Genre, DateTime Generation)>();
        _feedback.Setup(store => store.RecordImpressions(user, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime>()))
            .Callback<Guid, IReadOnlyList<DiscoveryRecommendation>, DateTime>((_, items, generation) => captured.Add((items.Single().ScoringFeatures!.GenreSimilarity, generation)));
        var controller = Controller(user);
        foreach (var delivery in newestFirst ? new[] { second, first } : new[] { first, second })
        {
            Assert.IsType<NoContentResult>(controller.RecordImpressions(Payload(1, delivery.DeliveryId)));
        }

        Assert.Equal(newestFirst ? new[] { (0.8, secondGeneration), (0.3, firstGeneration) } : new[] { (0.3, firstGeneration), (0.8, secondGeneration) }, captured);
    }

    [Fact]
    public async Task MixedDeliveryPayload_IsRejectedWithoutPartialRecording()
    {
        var user = Guid.NewGuid();
        var first = await SaveAndDeliver(user, DateTime.UtcNow.AddDays(-1), 0.3);
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 2 }] }]));
        await Deliver(Controller(user));
        var payload = Payload(1, first.DeliveryId);
        payload.Items = [new DiscoveryDismissDto { TmdbId = 1 }, new DiscoveryDismissDto { TmdbId = 2 }];

        Assert.IsType<BadRequestObjectResult>(Controller(user).RecordImpressions(payload));
        _feedback.Verify(store => store.RecordImpressions(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task DeliveryRetention_IsBoundedAndEvictedTokensNeverFallBackToNewSnapshots()
    {
        var user = Guid.NewGuid();
        var oldest = await SaveAndDeliver(user, DateTime.UtcNow.AddDays(-1), 0.1);
        DiscoveryResult latest = oldest;
        for (var i = 0; i < 6; i++) latest = await SaveAndDeliver(user, DateTime.UtcNow, 0.8);

        Assert.IsType<BadRequestObjectResult>(Controller(user).RecordImpressions(Payload(1, oldest.DeliveryId)));
        Assert.IsType<NoContentResult>(Controller(user).RecordImpressions(Payload(1, latest.DeliveryId)));
        _feedback.Verify(store => store.RecordImpressions(user, It.Is<IReadOnlyList<DiscoveryRecommendation>>(items => BitConverter.DoubleToInt64Bits(items.Single().ScoringFeatures!.GenreSimilarity) == BitConverter.DoubleToInt64Bits(0.8)), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task ExpiredDelivery_IsRejectedEvenWhileOtherCacheEntriesAreLive()
    {
        var user = Guid.NewGuid();
        var response = await SaveAndDeliver(user, DateTime.UtcNow, 0.3);
        var key = typeof(UserDiscoveryController).GetMethod("DeliveryKey", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [user]);
        Assert.True(_memory.TryGetValue(key!, out object? value));
        var batch = Assert.IsType<IDictionary>(value, exactMatch: false).Values.Cast<object>().Single();
        batch.GetType().GetProperty("DeliveredAtUtc")!.SetValue(batch, DateTime.UtcNow.AddMinutes(-6));

        Assert.IsType<BadRequestObjectResult>(Controller(user).RecordImpressions(Payload(1, response.DeliveryId)));
        _feedback.Verify(store => store.RecordImpressions(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task MissingEmptyOrUnknownDeliveryId_DoesNotAuthorizeByTitleAlone()
    {
        var user = Guid.NewGuid();
        await SaveAndDeliver(user, DateTime.UtcNow, 0.3);
        foreach (var id in new Guid?[] { null, Guid.Empty, Guid.NewGuid() })
        {
            var payload = Payload(1);
            payload.DeliveryId = id;
            Assert.IsType<BadRequestObjectResult>(Controller(user).RecordImpressions(payload));
        }

        _feedback.Verify(store => store.RecordImpressions(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime>()), Times.Never);
    }

    private async Task<DiscoveryResult> SaveAndDeliver(Guid user, DateTime generation, double genre)
    {
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, GeneratedAt = generation, Recommendations = [new DiscoveryRecommendation { TmdbId = 1, ScoringFeatures = new CandidateFeatures { GenreSimilarity = genre } }] }]));
        return await Deliver(Controller(user));
    }

    private static async Task<DiscoveryResult> Deliver(UserDiscoveryController controller)
    {
        var result = await controller.GetMyDiscoveryResults(CancellationToken.None);
        var response = Assert.IsType<DiscoveryResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.NotNull(response.DeliveryId);
        Assert.NotEqual(Guid.Empty, response.DeliveryId);
        return response;
    }

    private UserDiscoveryController Controller(Guid? user)
    {
        var config = new Mock<IPluginConfigurationService>();
        config.Setup(service => service.GetConfiguration()).Returns(_configuration);
        var controller = new UserDiscoveryController(_cache, _discovery.Object, _feedback.Object, config.Object, _memory,
            Mock.Of<ITraktDiscoveryService>(), Mock.Of<IOfficialTraktPluginReader>(), NullLogger<UserDiscoveryController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            user.HasValue ? [new Claim("Jellyfin-UserId", user.Value.ToString())] : [], "Test")) } };
        return controller;
    }

    private static DiscoveryImpressionsDto Payload(int id, Guid? deliveryId = null) => new() { DeliveryId = deliveryId ?? Guid.NewGuid(), Items = [new DiscoveryDismissDto { TmdbId = id, MediaType = "movie" }] };
}
