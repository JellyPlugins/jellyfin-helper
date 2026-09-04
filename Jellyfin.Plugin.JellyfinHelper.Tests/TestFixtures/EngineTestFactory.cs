using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
///     Centralised factory for constructing a fully-mocked Engine under test. The engine has eight constructor dependencies.
/// </summary>
internal static class EngineTestFactory
{
    /// <summary>
    ///     Bundles the constructed Engine instance with the mocks used to build it, so tests can assert on interactions after the fact without having to wire up their own mock references.
    /// </summary>
    /// <param name="Engine">The constructed engine under test.</param>
    /// <param name="WatchHistory">The mock watch-history service.</param>
    /// <param name="LibraryManager">The mock library manager.</param>
    /// <param name="PluginLog">The mock plugin-log service.</param>
    /// <param name="Logger">The mock logger.</param>
    /// <param name="StrategySelector">The mock strategy selector (returns 0.0 alpha offset by default).</param>
    /// <param name="PerUserRegistry">The mock per-user ensemble registry (returns the shared strategy for every user by default).</param>
    /// <param name="FeedbackStore">The mock discovery feedback store.</param>
    /// <param name="ItemRepository">The mock item repository (returns empty genre/studio counts by default).</param>
    internal sealed record EngineHarness(
        Engine Engine,
        Mock<IWatchHistoryService> WatchHistory,
        Mock<ILibraryManager> LibraryManager,
        Mock<IPluginConfigurationService> ConfigService,
        Mock<IPluginLogService> PluginLog,
        Mock<ILogger<Engine>> Logger,
        Mock<IStrategySelector> StrategySelector,
        Mock<IPerUserEnsembleRegistry> PerUserRegistry,
        Mock<IDiscoveryFeedbackStore> FeedbackStore,
        Mock<IItemRepository> ItemRepository);

    /// <summary>
    ///     Constructs an Engine with sensible empty-collection defaults on every mock: no watch profiles, no library items, no strategy offset, no discovery feedback.
    /// </summary>
    /// <remarks>
    ///     The scoring strategy is a real HeuristicScoringStrategy rather than a mock because Engine never checks the strategy for specific implementations except when it uses reflection on EnsembleScoringStrategy in TrainStrategy.
    /// </remarks>
    /// <returns></returns>
    internal static EngineHarness Create(IScoringStrategy? strategyOverride = null)
    {
        var watchHistory = new Mock<IWatchHistoryService>();
        watchHistory.Setup(w => w.GetAllUserWatchProfiles())
                    .Returns(new Collection<UserWatchProfile>());
        watchHistory.Setup(w => w.GetUserWatchProfile(It.IsAny<Guid>()))
                    .Returns((UserWatchProfile?)null);

        var libraryManager = TestMockFactory.CreateLibraryManager();
        // GetItemList is called from LoadCandidateItems (batch path) and UpdateDiscoveryWatchedStatus.
        libraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([]);

        // Empty ExcludedLibraries by default so the library filter is a no-op and existing tests are unaffected.
        var configService = new Mock<IPluginConfigurationService>();
        configService.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration());

        var pluginLog = new Mock<IPluginLogService>();
        var logger = TestMockFactory.CreateLogger<Engine>();

        var strategy = strategyOverride ?? new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);

        var strategySelector = new Mock<IStrategySelector>();
        strategySelector.Setup(s => s.GetAlphaOffset(It.IsAny<Guid>())).Returns(0.0);
        strategySelector.Setup(s => s.GetCohortName(It.IsAny<Guid>())).Returns("control");

        // By default the registry returns the same shared strategy for every user, so existing Engine tests
        // see byte-identical scoring behaviour to the pre-per-user single-strategy engine. GlobalEnsemble is
        // wired to the strategy when it is an ensemble, else to a fresh in-memory ensemble so TrainStrategy
        // (which trains registry.GlobalEnsemble) does not dereference null on the heuristic-only default.
        var globalEnsemble = strategy as EnsembleScoringStrategy
            ?? new EnsembleScoringStrategy(
                new LearnedScoringStrategy(),
                new HeuristicScoringStrategy(genrePenaltyFloor: 1.0));
        var perUserRegistry = new Mock<IPerUserEnsembleRegistry>();
        perUserRegistry.Setup(r => r.GetScoringStrategyForUser(It.IsAny<Guid>())).Returns(strategy);
        perUserRegistry.SetupGet(r => r.GlobalEnsemble).Returns(globalEnsemble);
        perUserRegistry.Setup(r => r.GetOrCreateTrainableEnsembleForUser(It.IsAny<Guid>())).Returns(globalEnsemble);
        perUserRegistry.Setup(r => r.GetDiagnostics(It.IsAny<Guid>())).Returns(globalEnsemble.GetDiagnosticsSnapshot());
        perUserRegistry.Setup(r => r.HasPerUserModel(It.IsAny<Guid>())).Returns(false);

        var feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        feedbackStore.Setup(f => f.LoadAll())
                     .Returns(new List<DiscoveryFeedbackResult>());

        // Empty genre/studio counts by default -> BuildGenreStudioIdfTable yields an empty table and the GenreStudioIdfPrior feature stays neutral (0.0), keeping the batch path in its normal control flow for the no-library scenario.
        var itemRepository = new Mock<IItemRepository>();
        itemRepository.Setup(r => r.GetGenres(It.IsAny<InternalItemsQuery>()))
                      .Returns(new QueryResult<(BaseItem, ItemCounts)>());
        itemRepository.Setup(r => r.GetStudios(It.IsAny<InternalItemsQuery>()))
                      .Returns(new QueryResult<(BaseItem, ItemCounts)>());

        var engine = new Engine(
            watchHistory.Object,
            libraryManager.Object,
            configService.Object,
            pluginLog.Object,
            logger.Object,
            strategy,
            strategySelector.Object,
            perUserRegistry.Object,
            feedbackStore.Object,
            itemRepository.Object);

        return new EngineHarness(
            engine,
            watchHistory,
            libraryManager,
            configService,
            pluginLog,
            logger,
            strategySelector,
            perUserRegistry,
            feedbackStore,
            itemRepository);
    }
}