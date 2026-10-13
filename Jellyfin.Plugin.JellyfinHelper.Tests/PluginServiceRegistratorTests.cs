using System.IO.Abstractions;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Tests for PluginServiceRegistrator to make sure every service the plugin depends on is registered against the DI container.
/// </summary>
[Collection("ConfigOverride")]
public class PluginServiceRegistratorTests : IDisposable
{
    public void Dispose()
    {
        // The data-path factory test initializes the Plugin singleton; always tear it down so the
        // global state does not bleed into other tests in the collection.
        ControllerTestFactory.TeardownPluginInstance();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Registers all services against a fresh collection. Plugin.Instance may or may not exist depending on test ordering - the registrator uses the null-conditional so it tolerates either state.
    /// </summary>
    private static IServiceCollection Register()
    {
        var sc = new ServiceCollection();
        var host = new Mock<IServerApplicationHost>();
        var sut = new PluginServiceRegistrator();
        sut.RegisterServices(sc, host.Object);
        return sc;
    }

    private static bool ContainsSingleton<TService>(IServiceCollection sc)
        => sc.Any(d => d.ServiceType == typeof(TService) && d.Lifetime == ServiceLifetime.Singleton);

    // Contract: RegisterServices does not throw regardless of Plugin.Instance state

    [Fact]
    public void RegisterServices_WithNullApplicationHost_ThrowsNothing_WhenAppHostProvided()
    {
        // We pass a real Mock instance, not null - the interface contract does not permit null,
        // but the registrator must not depend on any member of the host either.
        var ex = Record.Exception(() => Register());
        Assert.Null(ex);
    }

    // HttpClients - three named clients with specific timeouts must be present

    [Fact]
    public void RegisterServices_RegistersIHttpClientFactory()
    {
        var sc = Register();
        Assert.Contains(sc, d => d.ServiceType == typeof(System.Net.Http.IHttpClientFactory));
    }

    // Every interface the controllers depend on must be registered as Singleton.

    [Theory]
    [InlineData(typeof(ICleanupConfigHelper))]
    [InlineData(typeof(ICleanupTrackingService))]
    [InlineData(typeof(ITrashService))]
    [InlineData(typeof(IPluginConfigurationService))]
    [InlineData(typeof(IPluginLogService))]
    [InlineData(typeof(IMediaStatisticsService))]
    [InlineData(typeof(IStatisticsCacheService))]
    [InlineData(typeof(IGrowthTimelineService))]
    [InlineData(typeof(ILibraryInsightsService))]
    [InlineData(typeof(IBackupService))]
    [InlineData(typeof(IFileSystem))]
    [InlineData(typeof(ISymlinkHelper))]
    [InlineData(typeof(ILinkRepairService))]
    [InlineData(typeof(IArrIntegrationService))]
    [InlineData(typeof(ISeerrIntegrationService))]
    [InlineData(typeof(IFolderBrowserService))]
    [InlineData(typeof(IWatchHistoryService))]
    [InlineData(typeof(IRecommendationEngine))]
    [InlineData(typeof(IRecommendationCacheService))]
    [InlineData(typeof(IUserActivityInsightsService))]
    [InlineData(typeof(IUserActivityCacheService))]
    [InlineData(typeof(IRecommendationPlaylistService))]
    [InlineData(typeof(IDiscoveryFeedbackStore))]
    [InlineData(typeof(ISeerrDiscoveryService))]
    [InlineData(typeof(IScoringStrategy))]
    [InlineData(typeof(IStrategySelector))]
    public void RegisterServices_RegistersRequiredSingleton(Type serviceType)
    {
        var sc = Register();
        Assert.Contains(sc, d => d.ServiceType == serviceType && d.Lifetime == ServiceLifetime.Singleton);
    }

    // ILinkHandler is registered TWICE (Strm and Symlink) - must expose both.

    [Fact]
    public void RegisterServices_LinkHandler_RegistersBothStrmAndSymlinkImplementations()
    {
        var sc = Register();
        var handlers = sc.Where(d => d.ServiceType == typeof(ILinkHandler)).ToList();
        Assert.Equal(2, handlers.Count);
        // Both must be Singleton so container returns the same instance across the app.
        Assert.All(handlers, d => Assert.Equal(ServiceLifetime.Singleton, d.Lifetime));
    }

    // The scoring strategies must all be reachable (Heuristic, Learned, Neural,
    // Ensemble) so the ensemble can compose them.

    [Theory]
    [InlineData(typeof(HeuristicScoringStrategy))]
    [InlineData(typeof(LearnedScoringStrategy))]
    [InlineData(typeof(NeuralScoringStrategy))]
    [InlineData(typeof(EnsembleScoringStrategy))]
    [InlineData(typeof(DiscoveryCacheService))]
    public void RegisterServices_RegistersConcreteStrategy(Type concreteType)
    {
        var sc = Register();
        Assert.Contains(sc, d => d.ServiceType == concreteType && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void RegisterServices_IScoringStrategy_DelegatesToEnsemble()
    {
        // The IScoringStrategy binding must resolve to the Ensemble, not Heuristic/Learned/Neural alone. If someone re-orders the AddSingleton calls and forgets to redirect the interface, recommendation ranking silently switches strategies.
        var sc = Register();
        var provider = sc.BuildServiceProvider();
        var strategy = provider.GetService<IScoringStrategy>();
        Assert.NotNull(strategy);
        Assert.IsType<EnsembleScoringStrategy>(strategy);
    }

    [Fact]
    public void RegisterServices_ResolvesFullDependencyGraphWithoutError()
    {
        // Smoke test: build the container and try to resolve every registered service. Any missing dependency (e.g.
        var sc = Register();
        // Provide the loggers that the concrete registrations request. Without these,
        // ILogger<T> resolution would fail on the first strategy factory.
        sc.AddLogging();

        var provider = sc.BuildServiceProvider(validateScopes: true);

        // A representative sample from every category - verifying each of these resolves
        // exercises the entire factory chain (loggers, config lookups, path composition).
        Assert.NotNull(provider.GetService<IScoringStrategy>());
        Assert.NotNull(provider.GetService<IStrategySelector>());
        Assert.NotNull(provider.GetService<LearnedScoringStrategy>());
        Assert.NotNull(provider.GetService<NeuralScoringStrategy>());
        Assert.NotNull(provider.GetService<HeuristicScoringStrategy>());
        Assert.NotNull(provider.GetService<EnsembleScoringStrategy>());
        // The per-user registry factory reads Plugin.Instance for its data path and blend bounds and resolves
        // the shared global ensemble and neural, so resolving it exercises that whole factory.
        Assert.NotNull(provider.GetService<IPerUserEnsembleRegistry>());
    }

    [Fact]
    public void RegisterServices_ResolvesRecommendationPlaylistService_WhenJellyfinDepsRegistered()
    {
        // BUG GUARD: RecommendationPlaylistService requires IPlaylistManager, IUserManager, and ILibraryManager - all Jellyfin-host interfaces not present in the plugin's own RegisterServices call.
        var sc = Register();
        sc.AddLogging();
        sc.AddSingleton(new Mock<IPlaylistManager>().Object);
        sc.AddSingleton(new Mock<IUserManager>().Object);
        sc.AddSingleton(new Mock<ILibraryManager>().Object);

        var provider = sc.BuildServiceProvider(validateScopes: true);

        var service = provider.GetService<IRecommendationPlaylistService>();
        Assert.NotNull(service);
        Assert.IsType<RecommendationPlaylistService>(service);
    }

    [Fact]
    public void RegisterServices_HttpClientFactory_ProducesConfiguredClients()
    {
        // The three named HttpClient registrations set specific timeouts. If someone accidentally removes a name, the factory silently returns a default client with a 100-second timeout - dangerous for calls to Radarr/Sonarr/Seerr.
        var sc = Register();
        sc.AddLogging();
        var provider = sc.BuildServiceProvider();
        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();

        var arr = factory.CreateClient("ArrIntegration");
        Assert.Equal(TimeSpan.FromSeconds(15), arr.Timeout);

        var seerr = factory.CreateClient("SeerrIntegration");
        Assert.Equal(TimeSpan.FromSeconds(30), seerr.Timeout);

        var seerrDiscovery = factory.CreateClient("SeerrDiscovery");
        Assert.Equal(TimeSpan.FromSeconds(30), seerrDiscovery.Timeout);
    }

    [Fact]
    public void RegisterServices_TraktClient_SendsUserAgent()
    {
        // BUG GUARD: Trakt sits behind Cloudflare, which 403s requests that carry no User-Agent. HttpClient
        // sends none by default, so without an explicit UA every Trakt call failed with 403 regardless of a
        // valid token/client id (all users saw an empty Discovery grid). The Trakt client must set a UA.
        var sc = Register();
        sc.AddLogging();
        var provider = sc.BuildServiceProvider();
        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();

        var trakt = factory.CreateClient("Trakt");

        Assert.NotEmpty(trakt.DefaultRequestHeaders.UserAgent);
        Assert.Contains(
            trakt.DefaultRequestHeaders.UserAgent,
            p => (p.Product?.Name ?? string.Empty).Contains("JellyfinHelper", System.StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterServices_UnknownClientName_FallsBackToDefaultTimeout()
    {
        // Negative sanity check: a typo'd name doesn't accidentally match one of our registrations. Confirms our named-client registrations are actually keyed on the exact names controllers use.
        var sc = Register();
        sc.AddLogging();
        var provider = sc.BuildServiceProvider();
        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();

        // "arrIntegration" is a subtle typo - must NOT match "ArrIntegration".
        var typo = factory.CreateClient("arrIntegration");
        // The default HttpClient timeout is 100 seconds.
        Assert.Equal(TimeSpan.FromSeconds(100), typo.Timeout);
    }

    [Fact]
    public void RegisterServices_TwoInvocationsOnFreshCollections_ProduceIdenticalCounts()
    {
        // Determinism guard: two fresh registrations must yield the same number of descriptors. If a registration ever became non-deterministic (e.g.
        var sc1 = Register();
        var sc2 = Register();
        Assert.Equal(sc1.Count, sc2.Count);
    }

    [Fact]
    public void RegisterServices_RegistersModelBindingLogFilter_AsScoped()
    {
        // Filter is consumed via [ServiceFilter(typeof(ModelBindingLogFilter))] on the ConfigurationController action.
        var sc = Register();
        Assert.Contains(sc, d => d.ServiceType == typeof(ModelBindingLogFilter)
                                 && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void RegisterServices_CalledTwiceOnSameCollection_DoublesTheRegistrationsAsExpected()
    {
        // Real re-entrancy guard: calling RegisterServices twice against the SAME ServiceCollection is expected to append descriptors (Add* semantics, not TryAdd*).
        var sc = new ServiceCollection();
        var host = new Mock<IServerApplicationHost>();
        var sut = new PluginServiceRegistrator();

        sut.RegisterServices(sc, host.Object);
        var countAfterFirst = sc.Count;
        Assert.NotEqual(0, countAfterFirst);

        sut.RegisterServices(sc, host.Object);
        var countAfterSecond = sc.Count;

        // Every Add* registration is duplicated by the second call.
        Assert.True(
            countAfterSecond > countAfterFirst,
            $"Registration count must grow when RegisterServices is invoked twice on the same collection (was {countAfterFirst}, now {countAfterSecond}).");
    }

    [Fact]
    public void RegisterServices_WithInitializedPluginInstance_ResolvesStrategiesUsingDataFolderPath()
    {
        // The scoring-strategy and per-user-registry factories read Plugin.Instance.DataFolderPath to
        // compose their on-disk weight/state paths. With no instance that branch is skipped; initializing
        // the singleton exercises the data-path arm of each factory so resolution covers it.
        ControllerTestFactory.InitializePluginInstance();
        Assert.NotNull(Plugin.Instance);
        Assert.False(string.IsNullOrEmpty(Plugin.Instance!.DataFolderPath));

        var sc = Register();
        sc.AddLogging();
        var provider = sc.BuildServiceProvider(validateScopes: true);

        Assert.NotNull(provider.GetService<LearnedScoringStrategy>());
        Assert.NotNull(provider.GetService<NeuralScoringStrategy>());
        Assert.NotNull(provider.GetService<EnsembleScoringStrategy>());
        Assert.NotNull(provider.GetService<IPerUserEnsembleRegistry>());
    }

    // ResolveKeyRingDirectory - Data Protection keyring setup must never resolve against the process
    // working directory (the reported Path.Combine pitfall) and must lock the ring down on Unix.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    public void ResolveKeyRingDirectory_InvalidBase_ReturnsNull(string? basePath)
    {
        // Null/empty/whitespace/relative bases must be rejected outright - otherwise the ring would
        // silently land in the process working directory instead of the plugin data path.
        Assert.Null(PluginServiceRegistrator.ResolveKeyRingDirectory(basePath));
    }

    [Fact]
    public void ResolveKeyRingDirectory_ValidBase_CreatesKeysSubdirectory()
    {
        var basePath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var directory = PluginServiceRegistrator.ResolveKeyRingDirectory(basePath);
            Assert.NotNull(directory);
            Assert.Equal(
                Path.GetFullPath(Path.Join(basePath, "keys")),
                directory!.FullName);
            Assert.True(Directory.Exists(directory.FullName));
        }
        finally
        {
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, true);
            }
        }
    }

    [Fact]
    public void ResolveKeyRingDirectory_ValidBase_LocksDownPermissionsOnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Windows relies on ACL inheritance; there is nothing portable to assert.
            return;
        }

        var basePath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var directory = PluginServiceRegistrator.ResolveKeyRingDirectory(basePath);
            Assert.NotNull(directory);
            var mode = File.GetUnixFileMode(directory!.FullName);
            const UnixFileMode ownerBits = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode otherBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(ownerBits, mode & ownerBits);
            Assert.Equal((UnixFileMode)0, mode & otherBits);
        }
        finally
        {
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, true);
            }
        }
    }

    [Fact]
    public void RegisterServices_ResolvesSecretProtector_EphemeralWhenNoInstance()
    {
        // No Plugin instance means no data path: the factory must fall back to the ephemeral provider
        // instead of throwing, because startup itself must still succeed (secrets just die with restart).
        ControllerTestFactory.TeardownPluginInstance();
        var sc = Register();
        sc.AddSingleton(Mock.Of<ILogger<SecretProtector>>());

        var protector = sc.BuildServiceProvider().GetRequiredService<ISecretProtector>();

        Assert.NotNull(protector);
        const string secret = "s3cret";
        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret)));
    }

    [Fact]
    public void RegisterServices_ResolvesSecretProtector_PersistedRingWhenInstanceAvailable()
    {
        // With a Plugin instance the factory must back Data Protection with a keyring under the data
        // path (not ephemeral): the keys directory is created on disk and secrets round-trip through it.
        ControllerTestFactory.InitializePluginInstance();
        Assert.NotNull(Plugin.Instance);
        var dataPath = Plugin.Instance!.DataFolderPath;
        Assert.False(string.IsNullOrEmpty(dataPath));

        var sc = Register();
        sc.AddSingleton(Mock.Of<ILogger<SecretProtector>>());

        var protector = sc.BuildServiceProvider().GetRequiredService<ISecretProtector>();

        Assert.NotNull(protector);
        const string secret = "s3cret";
        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret)));
        Assert.True(Directory.Exists(Path.Join(dataPath, "keys")));
    }

    [Fact]
    public void RegisterServices_ResolvesOfficialTraktPluginReader_AsAbsentWithoutHostServices()
    {
        // Neither IPluginManager nor IApplicationPaths is registered (a host without them): the nullable
        // GetService lookups yield null and the reader must report absent instead of throwing at startup.
        var sc = Register();
        sc.AddSingleton<IPluginLogService>(TestMockFactory.CreatePluginLogService());
        sc.AddSingleton(Mock.Of<ILogger<OfficialTraktPluginReader>>());

        var reader = sc.BuildServiceProvider().GetRequiredService<IOfficialTraktPluginReader>();

        Assert.NotNull(reader);
        Assert.False(reader.IsPresent());
    }

    [Theory]
    [InlineData(PluginStatus.Active, true)]
    [InlineData(PluginStatus.Disabled, false)]
    public void RegisterServices_OfficialTraktPluginReader_ReflectsPluginStatus(PluginStatus status, bool expected)
    {
        // Presence means installed AND active: GetPlugin also returns disabled plugins, and sourcing
        // through a non-running plugin would serve a stale token, so only Active counts as present.
        var managerMock = new Mock<IPluginManager>();
        managerMock
            .Setup(m => m.GetPlugin(It.IsAny<Guid>(), It.IsAny<Version>()))
            .Returns(new LocalPlugin("test-path", true, new PluginManifest { Status = status }));
        var pathsMock = new Mock<IApplicationPaths>();
        pathsMock.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());

        var sc = Register();
        sc.AddSingleton(managerMock.Object);
        sc.AddSingleton(pathsMock.Object);
        sc.AddSingleton<IPluginLogService>(TestMockFactory.CreatePluginLogService());
        sc.AddSingleton(Mock.Of<ILogger<OfficialTraktPluginReader>>());

        var reader = sc.BuildServiceProvider().GetRequiredService<IOfficialTraktPluginReader>();

        Assert.Equal(expected, reader.IsPresent());
    }

    [Fact]
    public void RegisterServices_OfficialTraktPluginReader_ReadsTokenFromComposedConfigPath()
    {
        // Proves the factory hands the reader <PluginConfigurationsPath>/Trakt.xml: a token seeded at
        // that exact file must be readable through the resolved reader (not just a probe flag).
        var userId = Guid.NewGuid();
        var configDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        try
        {
            File.WriteAllText(
                Path.Join(configDir, "Trakt.xml"),
                "<PluginConfiguration><TraktUsers><TraktUser>"
                + "<AccessToken>seeded-tok</AccessToken>"
                + "<RefreshToken>seeded-ref</RefreshToken>"
                + $"<LinkedMbUserId>{userId:D}</LinkedMbUserId>"
                + "<AccessTokenExpiration>2099-01-01T00:00:00Z</AccessTokenExpiration>"
                + "</TraktUser></TraktUsers></PluginConfiguration>");
            var pathsMock = new Mock<IApplicationPaths>();
            pathsMock.SetupGet(p => p.PluginConfigurationsPath).Returns(configDir);

            var sc = Register();
            sc.AddSingleton(pathsMock.Object);
            sc.AddSingleton<IPluginLogService>(TestMockFactory.CreatePluginLogService());
            sc.AddSingleton(Mock.Of<ILogger<OfficialTraktPluginReader>>());

            var reader = sc.BuildServiceProvider().GetRequiredService<IOfficialTraktPluginReader>();

            var token = reader.TryGetToken(userId, DateTimeOffset.UtcNow);
            Assert.NotNull(token);
            Assert.Equal("seeded-tok", token!.AccessToken);
        }
        finally
        {
            if (Directory.Exists(configDir))
            {
                Directory.Delete(configDir, true);
            }
        }
    }

    [Fact]
    public void ResolveKeyRingDirectory_WhenKeysPathIsAFile_ReturnsNull()
    {
        // Force the Create() failure branch: a plain file sitting where the "keys" subdirectory
        // would go makes DirectoryInfo.Create throw IOException on every platform, so the method
        // must fail closed to null rather than surface the exception into startup.
        var basePath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(basePath);
        var collidingFile = Path.Join(basePath, "keys");
        File.WriteAllText(collidingFile, "not a directory");
        try
        {
            Assert.Null(PluginServiceRegistrator.ResolveKeyRingDirectory(basePath));
        }
        finally
        {
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, true);
            }
        }
    }
}
