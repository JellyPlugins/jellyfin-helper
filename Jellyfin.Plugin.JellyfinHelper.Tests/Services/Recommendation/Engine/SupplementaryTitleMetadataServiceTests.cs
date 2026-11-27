using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>ARR configuration and failures remain optional; background metadata is available without Seerr.</summary>
public sealed class SupplementaryTitleMetadataServiceTests
{
    [Fact]
    public async Task NoConfiguredSourcesMakesNoNetworkRequests()
    {
        var arr = new Mock<IArrIntegrationService>(MockBehavior.Strict);
        var service = Create(arr, new TitleMetadataCache());
        await service.WarmAsync(new PluginConfiguration(), TestContext.Current.CancellationToken);
        arr.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ConstructorRejectsMissingDependencies(int missing)
    {
        var arr = new Mock<IArrIntegrationService>().Object;
        var cache = new TitleMetadataCache();
        var secrets = TestMockFactory.CreateSecretProtector();
        var log = TestMockFactory.CreatePluginLogService();
        var logger = new Mock<ILogger<SupplementaryTitleMetadataService>>().Object;
        Assert.Throws<ArgumentNullException>(() => missing switch
        {
            0 => new SupplementaryTitleMetadataService(null!, cache, secrets, log, logger),
            1 => new SupplementaryTitleMetadataService(arr, null!, secrets, log, logger),
            2 => new SupplementaryTitleMetadataService(arr, cache, null!, log, logger),
            3 => new SupplementaryTitleMetadataService(arr, cache, secrets, null!, logger),
            _ => new SupplementaryTitleMetadataService(arr, cache, secrets, log, null!)
        });
    }

    [Fact]
    public async Task NullConfigurationIsRejectedBeforeAnyOptionalNetworkWork()
    {
        var arr = new Mock<IArrIntegrationService>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Create(arr, new TitleMetadataCache()).WarmAsync(null!, TestContext.Current.CancellationToken));
        arr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SonarrAndRadarrComplementTheCacheEvenWhenSeerrIsNotConfigured()
    {
        var config = new PluginConfiguration
        {
            RadarrInstances = [new ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "radarr-test" }],
            SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "sonarr-test" }]
        };
        var cache = new TitleMetadataCache();
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync("https://radarr.example.test", "radarr-test", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ArrMovie { TmdbId = 123, Genres = ["Drama"], Overview = "Movie overview" }]);
        arr.Setup(value => value.GetSonarrSeriesAsync("https://sonarr.example.test", "sonarr-test", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ArrSeries { TvdbId = 456, Genres = ["Animation", "Kids"], SeriesType = "anime", Overview = "TVDB overview" }]);
        await Create(arr, cache).WarmAsync(config, TestContext.Current.CancellationToken);
        Assert.Equal("Movie overview", cache.Get(config, 123, "movie")!.Overview);
        Assert.Equal("TVDB overview", cache.Get(config, 0, "tv", 456)!.Overview);
        Assert.True(cache.Get(config, 0, "tv", 456)!.AnimeHint);
        Assert.Null(cache.Get(config, 0, "tv", 456)!.SeriesType);
        Assert.Empty(config.SeerrUrl);
        Assert.Empty(config.SeerrApiKey);
    }

    [Fact]
    public async Task OneUnavailableProviderDoesNotBlockAnotherConfiguredSource()
    {
        var config = new PluginConfiguration
        {
            RadarrInstances = [new ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "radarr-test" }],
            SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "sonarr-test" }]
        };
        var cache = new TitleMetadataCache();
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("offline"));
        arr.Setup(value => value.GetSonarrSeriesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ArrSeries { TmdbId = 123, Genres = ["Drama"] }]);
        await Create(arr, cache).WarmAsync(config, TestContext.Current.CancellationToken);
        Assert.NotNull(cache.Get(config, 123, "tv"));
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowedAsAnOptionalProviderFailure()
    {
        var config = new PluginConfiguration { RadarrInstances = [new ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "test" }] };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(arr, new TitleMetadataCache()).WarmAsync(config, cancellation.Token));
    }

    [Fact]
    public async Task SlowProviderDoesNotPreventAnotherProviderFromPublishingAndCancellationIsPropagated()
    {
        var config = new PluginConfiguration
        {
            RadarrInstances = [new ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "radarr-test" }],
            SonarrInstances =
            [
                new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "sonarr-test" },
                new ArrInstanceConfig { Url = "https://next-sonarr.example.test", ApiKey = "sonarr-test" }
            ]
        };
        var cache = new TitleMetadataCache();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, bool _, CancellationToken token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new List<ArrMovie>();
            });
        arr.Setup(value => value.GetSonarrSeriesAsync("https://sonarr.example.test", It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await started.Task;
                return new List<ArrSeries> { new() { TmdbId = 123, Overview = "available" } };
            });
        arr.Setup(value => value.GetSonarrSeriesAsync("https://next-sonarr.example.test", It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // The bounded worker can reach this source only after publishing the first Sonarr result.
                published.SetResult();
                return Task.FromResult<List<ArrSeries>?>(null);
            });
        var warming = Create(arr, cache).WarmAsync(config, cancellation.Token);
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal("available", cache.Get(config, 123, "tv")!.Overview);
            Assert.False(warming.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
        }
    }

    [Fact]
    public async Task CapturedProviderAndLanguageCannotPublishUnderChangedConfiguration()
    {
        var config = new PluginConfiguration { RadarrInstances = [new ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "radarr-test", SkipCertificateValidation = true }] };
        var cache = new TitleMetadataCache();
        var response = new TaskCompletionSource<List<ArrMovie>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync("https://radarr.example.test", "radarr-test", true, It.IsAny<CancellationToken>()))
            .Returns(() => { started.SetResult(); return response.Task; });
        var service = new SupplementaryTitleMetadataService(arr.Object, cache, TestMockFactory.CreateSecretProtector(), TestMockFactory.CreatePluginLogService(), new Mock<ILogger<SupplementaryTitleMetadataService>>().Object)
        {
            MetadataLanguage = () => "en"
        };
        var warming = service.WarmAsync(config, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        config.RadarrInstances[0].Url = "https://changed.example.test";
        response.SetResult([new ArrMovie { TmdbId = 123, Overview = "old provider" }]);
        await warming;
        Assert.Null(cache.GetForLanguage(config, 123, "movie", 0, null, "en"));
    }

    [Fact]
    public async Task ProviderLocalTimeoutAndEmptyOrNullCatalogsAreOptional()
    {
        var config = new PluginConfiguration
        {
            RadarrInstances = [new ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "radarr-test" }],
            SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "sonarr-test" }]
        };
        var cache = new TitleMetadataCache();
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException("provider timeout"));
        arr.Setup(value => value.GetSonarrSeriesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ReturnsAsync((List<ArrSeries>?)null);
        await Create(arr, cache).WarmAsync(config, TestContext.Current.CancellationToken);
        Assert.Null(cache.Get(config, 123, "movie"));
        arr.Setup(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ReturnsAsync((List<ArrMovie>?)null);
        await Create(arr, cache).WarmAsync(config, TestContext.Current.CancellationToken);
    }

    private static SupplementaryTitleMetadataService Create(Mock<IArrIntegrationService> arr, TitleMetadataCache cache)
        => new(arr.Object, cache, TestMockFactory.CreateSecretProtector(), TestMockFactory.CreatePluginLogService(), new Mock<ILogger<SupplementaryTitleMetadataService>>().Object);
}
