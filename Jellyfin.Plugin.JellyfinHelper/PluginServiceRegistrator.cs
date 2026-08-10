using System;
using System.IO;
using System.IO.Abstractions;
using System.Net.Http;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
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
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // Hardening for all outbound named clients (Arr / Seerr):
        //  * MaxResponseContentBufferSize caps how much a response body can buffer, so a
        //    compromised/MITM'd upstream cannot stream a multi-GB body into a single string and
        //    OOM the Jellyfin process (Seerr reads used unbounded ReadAsStringAsync).
        //  * AllowAutoRedirect=false stops a hostile 3xx from redirecting an admin-configured LAN
        //    request to an arbitrary internal address (blind-SSRF hardening); 3xx then surfaces as a
        //    non-success status the callers already handle.
        const long maxResponseBytes = 100L * 1024 * 1024; // 100 MB, matching ArrIntegration's LimitedStream cap

        static HttpMessageHandler NoRedirectHandler() =>
            new SocketsHttpHandler { AllowAutoRedirect = false };

        serviceCollection.AddHttpClient("ArrIntegration", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = maxResponseBytes;
        }).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);
        serviceCollection.AddHttpClient("SeerrIntegration", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = maxResponseBytes;
        }).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);
        serviceCollection.AddHttpClient("SeerrDiscovery", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = maxResponseBytes;
        }).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);
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
        serviceCollection.AddSingleton<IFolderBrowserService, FolderBrowserService>();
        serviceCollection.AddSingleton<IWatchHistoryService, WatchHistoryService>();
        serviceCollection.AddSingleton(sp =>
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            string? weightsPath = null;
            if (!string.IsNullOrEmpty(dataPath))
            {
                weightsPath = Path.Join(dataPath, "ml_weights.json");
            }

            var logger = sp.GetRequiredService<ILogger<LearnedScoringStrategy>>();
            return new LearnedScoringStrategy(weightsPath, logger);
        });
        serviceCollection.AddSingleton(sp =>
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            string? neuralWeightsPath = null;
            if (!string.IsNullOrEmpty(dataPath))
            {
                neuralWeightsPath = Path.Join(dataPath, "neural_weights.json");
            }

            var logger = sp.GetRequiredService<ILogger<NeuralScoringStrategy>>();
            return new NeuralScoringStrategy(neuralWeightsPath, logger);
        });
        serviceCollection.AddSingleton(_ =>
        {
            // The heuristic sub-strategy inside EnsembleScoringStrategy MUST have its genre
            // penalty disabled (floor = 1.0). The ensemble applies the genre penalty centrally
            // via ComputeSoftGenrePenalty after blending; passing any value < 1.0 here would
            // cause double-penalization and is explicitly rejected by EnsembleScoringStrategy's
            // constructor guard. The config-driven EnsembleGenrePenaltyFloor controls only the
            // ensemble-level penalty, not this sub-strategy.
            return new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        });
        serviceCollection.AddSingleton(sp =>
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            string? statePath = null;
            if (!string.IsNullOrEmpty(dataPath))
            {
                statePath = Path.Join(dataPath, "ensemble_state.json");
            }

            var config = Plugin.Instance?.Configuration;
            var alphaMin = config?.EnsembleAlphaMin ?? EnsembleScoringStrategy.DefaultAlphaMin;
            var alphaMax = config?.EnsembleAlphaMax ?? EnsembleScoringStrategy.DefaultAlphaMax;
            var genrePenaltyFloor = config?.EnsembleGenrePenaltyFloor ?? EnsembleScoringStrategy.DefaultGenrePenaltyFloor;

            var learned = sp.GetRequiredService<LearnedScoringStrategy>();
            var heuristic = sp.GetRequiredService<HeuristicScoringStrategy>();
            var neural = sp.GetRequiredService<NeuralScoringStrategy>();
            var logger = sp.GetRequiredService<ILogger<EnsembleScoringStrategy>>();

            return new EnsembleScoringStrategy(learned, heuristic, neural, statePath, alphaMin, alphaMax, genrePenaltyFloor, logger);
        });
        // Always use Ensemble strategy - no user-selectable strategy choice.
        // Ensemble combines all methods (Heuristic + Learned + Neural) for best results.
        serviceCollection.AddSingleton<IScoringStrategy>(sp => sp.GetRequiredService<EnsembleScoringStrategy>());
        serviceCollection.AddSingleton<IStrategySelector>(sp =>
        {
            var ensemble = sp.GetRequiredService<EnsembleScoringStrategy>();
            return new StrategySelector(ensemble);
        });
        serviceCollection.AddSingleton<IRecommendationEngine, Engine>();
        serviceCollection.AddSingleton<IRecommendationCacheService, RecommendationCacheService>();
        serviceCollection.AddSingleton<IUserActivityInsightsService, UserActivityInsightsService>();
        serviceCollection.AddSingleton<IUserActivityCacheService, UserActivityCacheService>();
        serviceCollection.AddSingleton<IRecommendationPlaylistService, RecommendationPlaylistService>();
        serviceCollection.AddSingleton<DiscoveryCacheService>();
        serviceCollection.AddSingleton<IDiscoveryFeedbackStore, DiscoveryFeedbackStore>();
        serviceCollection.AddSingleton<ISeerrDiscoveryService, SeerrDiscoveryService>();

        // Action filter for surfacing model-binding failures into the plugin log before
        // [ApiController]'s auto-400 short-circuits the request. Scoped is the recommended
        // lifetime for filters resolved via [ServiceFilter(...)] - a new instance per request
        // matches the built-in filter lifecycle and avoids surprises when the filter ever
        // grows request-scoped dependencies.
        serviceCollection.AddScoped<ModelBindingLogFilter>();

        // Re-run the Discovery sidebar injection at server startup (after DI is built and the web
        // root is mounted). The plugin constructor already injects once, but this hosted service
        // runs at a more robust point and self-heals the disk-write fallback after a Jellyfin web
        // update overwrites index.html. Injection is idempotent, so running it twice is safe.
        serviceCollection.AddHostedService<DiscoverySidebarInjectionService>();
    }
}
