using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
///     Centralised factory for constructing a fully-mocked
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>
///     under test.
///     <para>
///         The engine has seven constructor dependencies. Wiring all of them by hand
///         in every test file bloats the suite and makes future refactors of the
///         Engine constructor a shotgun-surgery nightmare. Centralising the "sensible
///         empty defaults" here means:
///         <list type="bullet">
///             <item>New Engine tests only override the collaborator they actually care about.</item>
///             <item>An added constructor parameter is a one-line fix here, not N test files.</item>
///             <item>Every test starts from the same neutral state, avoiding subtle
///                   cross-test interference through shared static Plugin.Instance.</item>
///         </list>
///     </para>
/// </summary>
internal static class EngineTestFactory
{
    /// <summary>
    ///     Bundles the constructed <see cref="Engine"/> instance with the mocks used to
    ///     build it, so tests can assert on interactions after the fact without having to
    ///     wire up their own mock references.
    /// </summary>
    /// <param name="Engine">The constructed engine under test.</param>
    /// <param name="WatchHistory">The mock watch-history service.</param>
    /// <param name="LibraryManager">The mock library manager.</param>
    /// <param name="PluginLog">The mock plugin-log service.</param>
    /// <param name="Logger">The mock logger.</param>
    /// <param name="StrategySelector">The mock strategy selector (returns 0.0 alpha offset by default).</param>
    /// <param name="FeedbackStore">The mock discovery feedback store.</param>
    internal sealed record EngineHarness(
        Engine Engine,
        Mock<IWatchHistoryService> WatchHistory,
        Mock<ILibraryManager> LibraryManager,
        Mock<IPluginLogService> PluginLog,
        Mock<ILogger<Engine>> Logger,
        Mock<IStrategySelector> StrategySelector,
        Mock<IDiscoveryFeedbackStore> FeedbackStore);

    /// <summary>
    ///     Constructs an <see cref="Engine"/> with sensible empty-collection defaults on
    ///     every mock: no watch profiles, no library items, no strategy offset, no
    ///     discovery feedback. Use the returned <see cref="EngineHarness"/> to override
    ///     specific behaviours for a given test.
    /// </summary>
    /// <remarks>
    ///     The scoring strategy is a real <see cref="HeuristicScoringStrategy"/> rather
    ///     than a mock because <see cref="Engine"/> never checks the strategy for
    ///     specific implementations except when it uses reflection on
    ///     <see cref="EnsembleScoringStrategy"/> in <c>TrainStrategy</c>. Callers that
    ///     need to exercise that branch should pass an explicit ensemble via the
    ///     optional parameter.
    /// </remarks>
    internal static EngineHarness Create(IScoringStrategy? strategyOverride = null)
    {
        var watchHistory = new Mock<IWatchHistoryService>();
        watchHistory.Setup(w => w.GetAllUserWatchProfiles())
                    .Returns(new Collection<UserWatchProfile>());
        watchHistory.Setup(w => w.GetUserWatchProfile(It.IsAny<Guid>()))
                    .Returns((UserWatchProfile?)null);

        var libraryManager = TestMockFactory.CreateLibraryManager();
        // GetItemList is called from LoadCandidateItems (batch path) and UpdateDiscoveryWatchedStatus.
        // Default TestMockFactory setup only wires GetVirtualFolders; Moq's default for
        // reference-type returns is null, which the Engine's downstream loops treat as
        // iterables — a null would produce an NRE the moment the batch path enters the
        // `foreach (var movie in movies)` block. Wire the empty-list return so the batch
        // path stays inside its normal control flow even in the no-library scenario.
        libraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([]);
        var pluginLog = new Mock<IPluginLogService>();
        var logger = TestMockFactory.CreateLogger<Engine>();

        var strategy = strategyOverride ?? new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);

        var strategySelector = new Mock<IStrategySelector>();
        strategySelector.Setup(s => s.GetAlphaOffset(It.IsAny<Guid>())).Returns(0.0);
        strategySelector.Setup(s => s.GetCohortName(It.IsAny<Guid>())).Returns("control");

        var feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        feedbackStore.Setup(f => f.LoadAll())
                     .Returns(new List<DiscoveryFeedbackResult>());

        var engine = new Engine(
            watchHistory.Object,
            libraryManager.Object,
            pluginLog.Object,
            logger.Object,
            strategy,
            strategySelector.Object,
            feedbackStore.Object);

        return new EngineHarness(
            engine,
            watchHistory,
            libraryManager,
            pluginLog,
            logger,
            strategySelector,
            feedbackStore);
    }
}