using System;
using System.IO;
using System.IO.Abstractions;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyfinHelper;

/// <summary>
/// Registers services for dependency injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost; // Required by interface but unused
        serviceCollection.AddHttpClient("ArrIntegration", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        serviceCollection.AddHttpClient("SeerrIntegration", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        serviceCollection.AddSingleton<ICleanupConfigHelper, CleanupConfigHelper>();
        serviceCollection.AddSingleton<ICleanupTrackingService, CleanupTrackingService>();
        serviceCollection.AddSingleton<ITrashService, TrashService>();
        serviceCollection.AddSingleton<IPluginConfigurationService, PluginConfigurationService>();
        serviceCollection.AddSingleton<IPluginLogService, PluginLogService>();
        serviceCollection.AddSingleton<IMediaStatisticsService, MediaStatisticsService>();
        serviceCollection.AddSingleton<IStatisticsCacheService, StatisticsCacheService>();
        serviceCollection.AddSingleton<IGrowthTimelineService, GrowthTimelineService>();
        serviceCollection.AddSingleton<ILibraryInsightsService, LibraryInsightsService>();
        serviceCollection.AddSingleton<IBackupService, BackupService>();
        serviceCollection.AddSingleton<IFileSystem, FileSystem>();
        serviceCollection.AddSingleton<ISymlinkHelper, SymlinkHelper>();
        serviceCollection.AddSingleton<ILinkHandler, StrmLinkHandler>();
        serviceCollection.AddSingleton<ILinkHandler, SymlinkHandler>();
        serviceCollection.AddSingleton<ILinkRepairService, LinkRepairService>();
        serviceCollection.AddSingleton<IArrIntegrationService, ArrIntegrationService>();
        serviceCollection.AddSingleton<ISeerrIntegrationService, SeerrIntegrationService>();
        serviceCollection.AddSingleton<IWatchHistoryService, WatchHistoryService>();
        serviceCollection.AddSingleton(_ =>
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            string? weightsPath = null;
            if (!string.IsNullOrEmpty(dataPath))
            {
                weightsPath = Path.Join(dataPath, "ml_weights.json");
            }

            var config = Plugin.Instance?.Configuration;
            var alphaMin = config?.EnsembleAlphaMin ?? 0.3;
            var alphaMax = config?.EnsembleAlphaMax ?? 0.8;
            var genrePenaltyFloor = config?.EnsembleGenrePenaltyFloor ?? 0.10;

            return new EnsembleScoringStrategy(weightsPath, alphaMin, alphaMax, genrePenaltyFloor);
        });
        serviceCollection.AddSingleton<HeuristicScoringStrategy>();
        serviceCollection.AddSingleton<IScoringStrategy>(ResolveScoringStrategy);
        serviceCollection.AddSingleton<IRecommendationEngine, RecommendationEngine>();
        serviceCollection.AddSingleton<IRecommendationCacheService, RecommendationCacheService>();
        serviceCollection.AddSingleton<IUserActivityInsightsService, UserActivityInsightsService>();
        serviceCollection.AddSingleton<IUserActivityCacheService, UserActivityCacheService>();
    }

    /// <summary>
    ///     Resolves the active <see cref="IScoringStrategy"/> based on plugin configuration.
    ///     Valid strategy values: "ensemble" (default), "heuristic", "learned".
    /// </summary>
    private static IScoringStrategy ResolveScoringStrategy(IServiceProvider sp)
    {
        var config = Jellyfin.Plugin.JellyfinHelper.Plugin.Instance?.Configuration;
        var strategy = config?.RecommendationStrategy ?? "ensemble";

        switch (strategy.ToLowerInvariant())
        {
            case "heuristic":
                return (IScoringStrategy?)sp.GetService(typeof(HeuristicScoringStrategy))
                    ?? new HeuristicScoringStrategy();

            case "learned":
                var ensembleForLearned =
                    (EnsembleScoringStrategy?)sp.GetService(typeof(EnsembleScoringStrategy));
                return ensembleForLearned?.LearnedStrategy
                    ?? (IScoringStrategy)new LearnedScoringStrategy();

            default:
                return (IScoringStrategy?)sp.GetService(typeof(EnsembleScoringStrategy))
                    ?? new EnsembleScoringStrategy();
        }
    }
}
