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
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.DataProtection;
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

        // Hardening for all outbound named clients (Arr / Seerr): MaxResponseContentBufferSize caps how much a response body can buffer, so a compromised or MITM'd upstream cannot stream a multi-GB body into a single string and OOM the Jellyfin process. Seerr reads were previously unbounded.
        const long maxResponseBytes = 100L * 1024 * 1024; // 100 MB, matching ArrIntegration's LimitedStream cap

        static HttpMessageHandler NoRedirectHandler() =>
            new SocketsHttpHandler { AllowAutoRedirect = false };

        void AddHardenedClient(string name, TimeSpan timeout) =>
            serviceCollection.AddHttpClient(name, client =>
            {
                client.Timeout = timeout;
                client.MaxResponseContentBufferSize = maxResponseBytes;
            }).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);

        AddHardenedClient("ArrIntegration", TimeSpan.FromSeconds(15));
        AddHardenedClient("SeerrIntegration", TimeSpan.FromSeconds(30));
        AddHardenedClient("SeerrDiscovery", TimeSpan.FromSeconds(30));

        // Secrets are encrypted via Data Protection using a non-machine-bound keyring in the plugin path
        // for simple backup/migration. SetApplicationName isolates this keyring from Jellyfin's own.
        // Threat model: On Linux, keyring files are unencrypted at rest—matching Jellyfin's trust boundary
        // (where configs/DBs hold plain keys). Directory access is restricted to the service user (0700).
        var dataProtection = serviceCollection.AddDataProtection().SetApplicationName("Jellyfin.Plugin.JellyfinHelper");
        var keyRingDirectory = ResolveKeyRingDirectory(Plugin.Instance?.DataFolderPath);
        if (keyRingDirectory is null)
        {
            // Fail loudly, never silently: without a persisted ring, secrets only survive until the next
            // restart (ephemeral default store). Startup itself must still succeed - features degrade, they
            // must not take the whole plugin down (see the RegisterServices no-throw contract in tests).
            Plugin.Instance?.Logger.LogWarning(
                "[DataProtection] No usable keyring directory under the plugin data path - encrypted secrets will not survive restarts until this is fixed.");
        }
        else
        {
            dataProtection.PersistKeysToFileSystem(keyRingDirectory);
        }

        serviceCollection.AddSingleton<ISecretProtector, SecretProtector>();
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
            // The heuristic sub-strategy inside EnsembleScoringStrategy MUST have its genre penalty disabled (floor = 1.0).
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
        serviceCollection.AddSingleton<IPerUserEnsembleRegistry>(sp =>
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            var blendBounds = EnsembleBlendBounds.FromConfiguration(Plugin.Instance?.Configuration);

            var globalEnsemble = sp.GetRequiredService<EnsembleScoringStrategy>();
            var sharedNeural = sp.GetRequiredService<NeuralScoringStrategy>();
            var pluginLog = sp.GetRequiredService<IPluginLogService>();
            var fileSystem = sp.GetRequiredService<IFileSystem>();
            var logger = sp.GetRequiredService<ILogger<PerUserEnsembleRegistry>>();

            return new PerUserEnsembleRegistry(
                globalEnsemble,
                sharedNeural,
                string.IsNullOrEmpty(dataPath) ? null : dataPath,
                blendBounds,
                pluginLog,
                logger,
                fileSystem);
        });
        serviceCollection.AddSingleton<IRecommendationEngine, Engine>();
        serviceCollection.AddSingleton<IRecommendationCacheService, RecommendationCacheService>();
        serviceCollection.AddSingleton<IUserActivityInsightsService, UserActivityInsightsService>();
        serviceCollection.AddSingleton<IUserActivityCacheService, UserActivityCacheService>();
        serviceCollection.AddSingleton<IRecommendationPlaylistService, RecommendationPlaylistService>();
        serviceCollection.AddSingleton<DiscoveryCacheService>();
        serviceCollection.AddSingleton<IDiscoveryFeedbackStore, DiscoveryFeedbackStore>();
        serviceCollection.AddSingleton<ISeerrDiscoveryService, SeerrDiscoveryService>();

        // Action filter for surfacing model-binding failures into the plugin log before [ApiController]'s auto-400 short-circuits the request.
        serviceCollection.AddScoped<ModelBindingLogFilter>();

        // Re-run the Discovery sidebar injection at server startup (after DI is built and the web root is mounted).
        serviceCollection.AddHostedService<DiscoverySidebarInjectionService>();
    }

    /// <summary>
    ///     Resolves the Data Protection keyring directory below the plugin data path, creating it when needed.
    ///     The subdirectory name is this method's own constant, never external input, so the combine cannot
    ///     discard the base; a relative base that would resolve against the process working directory is
    ///     additionally rejected outright. Single source of truth, shared by the registrator and its tests.
    /// </summary>
    /// <param name="dataFolderPath">The plugin data path, or null when the plugin instance is unavailable.</param>
    /// <returns>The ready keyring directory, or null when no usable directory could be resolved.</returns>
    internal static DirectoryInfo? ResolveKeyRingDirectory(string? dataFolderPath)
    {
        const string keyRingSubdirectory = "keys";
        if (string.IsNullOrWhiteSpace(dataFolderPath) || !Path.IsPathFullyQualified(dataFolderPath))
        {
            return null;
        }

        DirectoryInfo directory;
        try
        {
            directory = new DirectoryInfo(Path.Combine(dataFolderPath, keyRingSubdirectory));
            directory.Create();
        }
        catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or NotSupportedException
                                        or ArgumentException
                                        or System.Security.SecurityException)
        {
            return null;
        }

        // Best effort: lock the ring down to the service account. A failure here must not fail startup;
        // the caller logs loudly and falls back to the ephemeral store instead.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(
                    directory.FullName,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is IOException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or NotSupportedException
                                            or PlatformNotSupportedException)
            {
                return null;
            }
        }

        return directory;
    }
}
