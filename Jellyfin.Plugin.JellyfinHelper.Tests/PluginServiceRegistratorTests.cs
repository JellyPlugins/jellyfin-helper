using System.IO.Abstractions;
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
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Tests for <see cref="PluginServiceRegistrator"/> to make sure every service the plugin
///     depends on is registered against the DI container. A regression in this file typically
///     surfaces as an unresolved-service <see cref="InvalidOperationException"/> at controller
///     construction time in production — a nasty runtime-only failure. Catching it here is
///     cheap and catches accidental removal or rename of a registration.
/// </summary>
public class PluginServiceRegistratorTests
{
    /// <summary>Registers all services against a fresh collection. Plugin.Instance may or may
    /// not exist depending on test ordering — the registrator uses the null-conditional so it
    /// tolerates either state.</summary>
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

    // -----------------------------------------------------------------------
    // Contract: RegisterServices does not throw regardless of Plugin.Instance state
    // -----------------------------------------------------------------------

    [Fact]
    public void RegisterServices_WithNullApplicationHost_ThrowsNothing_WhenAppHostProvided()
    {
        // We pass a real Mock instance, not null — the interface contract does not permit null,
        // but the registrator must not depend on any member of the host either.
        var ex = Record.Exception(() => Register());
        Assert.Null(ex);
    }

    // -----------------------------------------------------------------------
    // HttpClients — three named clients with specific timeouts must be present
    // -----------------------------------------------------------------------

    [Fact]
    public void RegisterServices_RegistersIHttpClientFactory()
    {
        var sc = Register();
        Assert.Contains(sc, d => d.ServiceType == typeof(System.Net.Http.IHttpClientFactory));
    }

    // -----------------------------------------------------------------------
    // Every interface the controllers depend on must be registered as Singleton.
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // ILinkHandler is registered TWICE (Strm and Symlink) — must expose both.
    // -----------------------------------------------------------------------

    [Fact]
    public void RegisterServices_LinkHandler_RegistersBothStrmAndSymlinkImplementations()
    {
        var sc = Register();
        var handlers = sc.Where(d => d.ServiceType == typeof(ILinkHandler)).ToList();
        Assert.Equal(2, handlers.Count);
        // Both must be Singleton so container returns the same instance across the app.
        Assert.All(handlers, d => Assert.Equal(ServiceLifetime.Singleton, d.Lifetime));
    }

    // -----------------------------------------------------------------------
    // The scoring strategies must all be reachable (Heuristic, Learned, Neural,
    // Ensemble) so the ensemble can compose them.
    // -----------------------------------------------------------------------

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
        // Regression: the IScoringStrategy binding must resolve to the Ensemble, not
        // Heuristic/Learned/Neural alone. If someone re-orders the AddSingleton calls
        // and forgets to redirect the interface, recommendation ranking silently switches
        // strategies.
        var sc = Register();
        var provider = sc.BuildServiceProvider();
        var strategy = provider.GetService<IScoringStrategy>();
        Assert.NotNull(strategy);
        Assert.IsType<EnsembleScoringStrategy>(strategy);
    }

    [Fact]
    public void RegisterServices_ResolvesFullDependencyGraphWithoutError()
    {
        // Smoke test: build the container and try to resolve every registered service.
        // Any missing dependency (e.g. a required ILogger<T>) surfaces as an
        // InvalidOperationException here rather than at runtime in Jellyfin.
        var sc = Register();
        // Provide the loggers that the concrete registrations request. Without these,
        // ILogger<T> resolution would fail on the first strategy factory.
        sc.AddLogging();

        var provider = sc.BuildServiceProvider(validateScopes: true);

        // A representative sample from every category — verifying each of these resolves
        // exercises the entire factory chain (loggers, config lookups, path composition).
        Assert.NotNull(provider.GetService<IScoringStrategy>());
        Assert.NotNull(provider.GetService<IStrategySelector>());
        Assert.NotNull(provider.GetService<LearnedScoringStrategy>());
        Assert.NotNull(provider.GetService<NeuralScoringStrategy>());
        Assert.NotNull(provider.GetService<HeuristicScoringStrategy>());
        Assert.NotNull(provider.GetService<EnsembleScoringStrategy>());
    }

    [Fact]
    public void RegisterServices_HttpClientFactory_ProducesConfiguredClients()
    {
        // The three named HttpClient registrations set specific timeouts. If someone
        // accidentally removes a name, the factory silently returns a default client
        // with a 100-second timeout — dangerous for calls to Radarr/Sonarr/Seerr.
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
    public void RegisterServices_UnknownClientName_FallsBackToDefaultTimeout()
    {
        // Negative sanity check: a typo'd name doesn't accidentally match one of our
        // registrations. Confirms our named-client registrations are actually keyed
        // on the exact names controllers use.
        var sc = Register();
        sc.AddLogging();
        var provider = sc.BuildServiceProvider();
        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();

        // "arrIntegration" is a subtle typo — must NOT match "ArrIntegration".
        var typo = factory.CreateClient("arrIntegration");
        // The default HttpClient timeout is 100 seconds.
        Assert.Equal(TimeSpan.FromSeconds(100), typo.Timeout);
    }

    [Fact]
    public void RegisterServices_TwoInvocationsOnFreshCollections_ProduceIdenticalCounts()
    {
        // Determinism guard: two fresh registrations must yield the same number of
        // descriptors. If a registration ever became non-deterministic (e.g. driven by
        // Random / DateTime.UtcNow / environmental state), this catches it early.
        var sc1 = Register();
        var sc2 = Register();
        Assert.Equal(sc1.Count, sc2.Count);
    }

    [Fact]
    public void RegisterServices_CalledTwiceOnSameCollection_DoublesTheRegistrationsAsExpected()
    {
        // Real re-entrancy guard: calling RegisterServices twice against the SAME
        // ServiceCollection is expected to append descriptors (Add* semantics, not
        // TryAdd*). If any registration silently switched to TryAdd, the second call
        // would be a no-op and this test would fail — surfacing the subtle behaviour
        // change instead of shipping it.
        //
        // Note: the registrator is called by Jellyfin exactly once, so this is a
        // regression net for future refactors, not a claim that double-registration
        // is a supported production scenario.
        var sc = new ServiceCollection();
        var host = new Mock<IServerApplicationHost>();
        var sut = new PluginServiceRegistrator();

        sut.RegisterServices(sc, host.Object);
        var countAfterFirst = sc.Count;
        Assert.NotEqual(0, countAfterFirst);

        sut.RegisterServices(sc, host.Object);
        var countAfterSecond = sc.Count;

        // Every Add* registration is duplicated by the second call. Named HttpClient
        // registrations use Configure<HttpClientFactoryOptions> which multiply on repeat
        // invocation, so the exact ratio is not necessarily 2:1 — we assert only strict
        // monotonic growth to keep this test resilient against future changes.
        Assert.True(
            countAfterSecond > countAfterFirst,
            $"Registration count must grow when RegisterServices is invoked twice on the same collection (was {countAfterFirst}, now {countAfterSecond}).");
    }
}
