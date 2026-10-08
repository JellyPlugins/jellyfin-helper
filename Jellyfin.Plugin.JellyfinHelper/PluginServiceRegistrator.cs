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

        // Same hardening as the strict client, except TLS certificate validation: opt-in per Arr
        // instance (or single Seerr connection) for reverse proxies with a private CA, self-signed, or
        // IP certificate. Only the insecure named clients use this handler; every default path keeps
        // full validation.
#pragma warning disable S4830 // Justification: intentional admin opt-in bypass, never the default; strict client unchanged.
        static HttpMessageHandler NoRedirectInsecureHandler() =>
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
#pragma warning restore S4830

        void AddHardenedClient(string name, TimeSpan timeout) =>
            serviceCollection.AddHttpClient(name, client =>
            {
                client.Timeout = timeout;
                client.MaxResponseContentBufferSize = maxResponseBytes;
            }).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);

        AddHardenedClient("ArrIntegration", TimeSpan.FromSeconds(15));
        serviceCollection.AddHttpClient("ArrIntegrationInsecure", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = maxResponseBytes;
        }).ConfigurePrimaryHttpMessageHandler(NoRedirectInsecureHandler);
        AddHardenedClient("SeerrIntegration", TimeSpan.FromSeconds(30));
        AddHardenedClient("SeerrDiscovery", TimeSpan.FromSeconds(30));
        serviceCollection.AddHttpClient("SeerrIntegrationInsecure", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = maxResponseBytes;
        }).ConfigurePrimaryHttpMessageHandler(NoRedirectInsecureHandler);
        serviceCollection.AddHttpClient("SeerrDiscoveryInsecure", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = maxResponseBytes;
        }).ConfigurePrimaryHttpMessageHandler(NoRedirectInsecureHandler);

        // Trakt timeout is admin-configurable (already clamped by the config setter). The delegate runs on
        // every CreateClient, so a saved change applies to the next request without a restart; falls back to
        // the default when the plugin instance is not yet available during early DI construction.
        serviceCollection.AddHttpClient("Trakt", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Plugin.Instance?.Configuration?.TraktTimeoutSeconds ?? 30);
            client.MaxResponseContentBufferSize = maxResponseBytes;
        }).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);

        // The provider stays private to this plugin so keyrings can neither affect nor be affected by
        // Jellyfin's or other plugins' providers. Ring files are unencrypted at rest on Linux, locked
        // down to the service account (0700) - same trust boundary as Jellyfin's own config and database.
        serviceCollection.AddSingleton<ISecretProtector>(sp =>
        {
            // Resolved lazily: Jellyfin 12.2 runs RegisterServices before plugin instances exist, so
            // Plugin.Instance is null during eager DI construction and would force the ephemeral provider
            // (secrets unreadable after restart). The factory runs on first resolution, by which point the
            // data path is available.
            var keyRingDirectory = ResolveKeyRingDirectory(Plugin.Instance?.DataFolderPath);
            var logger = sp.GetRequiredService<ILogger<SecretProtector>>();
            if (keyRingDirectory is null)
            {
                // No persisted ring means secrets die with the restart. Startup itself must still succeed.
                logger.LogWarning(
                    "[DataProtection] No usable keyring directory under the plugin data path - encrypted secrets will not survive restarts until this is fixed.");
            }

            IDataProtectionProvider provider = keyRingDirectory is null
                ? new EphemeralDataProtectionProvider()
                : DataProtectionProvider.Create(
                    keyRingDirectory,
                    builder => builder.SetApplicationName("Jellyfin.Plugin.JellyfinHelper"));

            return new SecretProtector(provider, logger);
        });

        // Trakt per-user OAuth token store. Reads the data path at construction so tokens persist across
        // restarts; falls back to in-memory only when the data path is unavailable.
        serviceCollection.AddSingleton<Services.Trakt.ITraktUserStore>(sp =>
            new Services.Trakt.TraktUserStore(
                sp.GetRequiredService<ISecretProtector>(),
                sp.GetRequiredService<IPluginLogService>(),
                sp.GetRequiredService<ILogger<Services.Trakt.TraktUserStore>>(),
                Plugin.Instance?.DataFolderPath));

        // Reads the OFFICIAL Trakt plugin's persisted config so the Helper can source recommendations through
        // its single app on a free Trakt account (one connected app per account). Host services are resolved
        // lazily and nullably: a Jellyfin without IPluginManager/IApplicationPaths must not break DI, it must
        // simply report the official plugin as absent so the Helper keeps its own-client-id flow.
        serviceCollection.AddSingleton<Services.Trakt.External.IOfficialTraktPluginReader>(sp =>
        {
            var pluginManager = sp.GetService<MediaBrowser.Common.Plugins.IPluginManager>();
            var appPaths = sp.GetService<MediaBrowser.Common.Configuration.IApplicationPaths>();
            var configurationsPath = appPaths?.PluginConfigurationsPath;

            // Only hand the reader a fully-qualified path. A relative/empty value would make File.Exists resolve
            // against the process CWD (not the config dir), so treat it as "no config" and let the reader report
            // the plugin absent rather than probing an unexpected location.
            var configPath = !string.IsNullOrEmpty(configurationsPath) && Path.IsPathFullyQualified(configurationsPath)
                ? Path.Join(configurationsPath, Services.Trakt.External.OfficialTraktPluginGuids.ConfigFileName)
                : null;

            bool IsPresent() =>
                pluginManager?.GetPlugin(Services.Trakt.External.OfficialTraktPluginGuids.PluginId) is not null;

            return new Services.Trakt.External.OfficialTraktPluginReader(
                IsPresent,
                configPath,
                sp.GetRequiredService<IPluginLogService>(),
                sp.GetRequiredService<ILogger<Services.Trakt.External.OfficialTraktPluginReader>>());
        });

        serviceCollection.AddSingleton<Services.Trakt.ITraktAuthService, Services.Trakt.TraktAuthService>();
        serviceCollection.AddSingleton<Services.Trakt.TraktCacheService>();
        serviceCollection.AddSingleton<Services.Trakt.ITraktDiscoveryService, Services.Trakt.TraktDiscoveryService>();
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
    ///     Path.Join never discards the base path, and a relative base is rejected outright so the ring can
    ///     never land in the process working directory.
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
            directory = new DirectoryInfo(Path.Join(dataFolderPath, keyRingSubdirectory));
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

        // Best effort: lock the ring down to the service account. A failure here must not fail startup
        // AND must not discard the ring: the directory is already created and writable, so returning null
        // would needlessly drop to ephemeral keys and lose every secret on restart over a cosmetic
        // permission-tightening failure. Owner-only mode is defense-in-depth on top of the data path's
        // existing trust boundary (same as Jellyfin's own config/database), so proceeding without it is safe.
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
                // Swallowed by design: keep the usable ring (see comment above). The failure is a
                // permission-tightening miss, not a loss of the directory itself.
            }
        }

        return directory;
    }
}
