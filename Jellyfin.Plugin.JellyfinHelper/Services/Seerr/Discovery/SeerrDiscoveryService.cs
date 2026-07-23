using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Generates personalized content discovery recommendations by querying the configured
///     Overseerr/Jellyseerr instance, scoring candidates against user watch profiles,
///     and persisting results for frontend consumption.
/// </summary>
public sealed class SeerrDiscoveryService : ISeerrDiscoveryService
{
    /// <summary>
    ///     Minimum vote average below which candidates are filtered out (quality floor).
    /// </summary>
    private const double MinVoteAverage = 5.0;

    /// <summary>
    ///     Minimum vote average for child-safe content (higher floor to ensure quality children's content).
    /// </summary>
    private const double MinVoteAverageChild = 5.5;

    /// <summary>
    ///     Maximum number of visible discovery recommendations served to the frontend per user.
    ///     The API layer filters the persisted pool down to this count.
    /// </summary>
    internal const int MaxVisiblePerUser = 10;

    /// <summary>
    ///     Total number of discovery recommendations generated and persisted per user (backfill pool).
    ///     Items beyond <see cref="MaxVisiblePerUser"/> serve as replacements when visible items
    ///     are dismissed or requested by the user.
    /// </summary>
    private const int MaxPoolPerUser = 20;

    /// <summary>
    ///     Number of top candidates (by pre-score) to enrich with credits data.
    ///     Credits calls are expensive (1 API call per item), so we only
    ///     enrich the most promising candidates after an initial genre/rating-based pre-score.
    /// </summary>
    private const int CreditsEnrichmentBudget = 20;

    /// <summary>
    ///     Maximum number of cast members to extract per candidate during credits enrichment.
    ///     Only top-billed actors (by order) and directors are included.
    /// </summary>
    private const int MaxCastPerCandidate = 10;

    /// <summary>
    ///     Maximum degree of parallelism for credits enrichment fetches.
    ///     3 concurrent requests balances throughput against Seerr/TMDb rate limits.
    /// </summary>
    private const int CreditsEnrichmentParallelism = 3;

    /// <summary>
    ///     Per-request timeout for credits enrichment calls, in milliseconds.
    ///     A single slow Seerr response must not stall the entire enrichment pass.
    /// </summary>
    private const int CreditsEnrichmentTimeoutMs = 8_000;

    /// <summary>
    ///     Maximum Jellyfin parental rating value that triggers the child-account discovery path.
    ///     Corresponds to FSK-6 / G / PG — users with this rating or below receive only
    ///     Family/Kids/Animation content from discovery queries.
    /// </summary>
    private const int ChildAccountMaxParentalRating = 60;

    /// <summary>TMDb genre ID for Family content (movies and TV).</summary>
    private const int TmdbGenreFamily = 10751;

    /// <summary>TMDb genre ID for Animation (movies).</summary>
    private const int TmdbGenreAnimation = 16;

    /// <summary>TMDb genre ID for Kids TV.</summary>
    private const int TmdbGenreTvKids = 10762;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

    /// <summary>
    ///     Delay between TMDb discovery queries via Seerr to respect rate limits.
    /// </summary>
    private static readonly TimeSpan InterQueryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     TTL for the cached Seerr user list used by <see cref="ResolveSeerrUserIdAsync"/>.
    ///     Avoids re-fetching the full paginated user roster on every request submission.
    /// </summary>
    private static readonly TimeSpan SeerrUserCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IArrIntegrationService _arrIntegration;
    private readonly EnsembleScoringStrategy _ensemble;
    private readonly DiscoveryCacheService _cache;
    private readonly IDiscoveryFeedbackStore _feedbackStore;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<SeerrDiscoveryService> _logger;

    /// <summary>
    ///     Cached Seerr user list to avoid re-fetching the full paginated roster
    ///     on every <see cref="ResolveSeerrUserIdAsync"/> call (e.g., every frontend request).
    /// </summary>
    private readonly Lock _userCacheLock = new();
    private IReadOnlyList<SeerrUser>? _cachedSeerrUsers;
    private DateTime _cachedSeerrUsersExpiry = DateTime.MinValue;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SeerrDiscoveryService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="arrIntegration">The Arr integration service.</param>
    /// <param name="ensemble">The ensemble scoring strategy (combines heuristic + learned + neural).</param>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="feedbackStore">The discovery feedback store for training data collection.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public SeerrDiscoveryService(
        IHttpClientFactory httpClientFactory,
        IWatchHistoryService watchHistoryService,
        IArrIntegrationService arrIntegration,
        EnsembleScoringStrategy ensemble,
        DiscoveryCacheService cache,
        IDiscoveryFeedbackStore feedbackStore,
        IPluginLogService pluginLog,
        ILogger<SeerrDiscoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(watchHistoryService);
        ArgumentNullException.ThrowIfNull(arrIntegration);
        ArgumentNullException.ThrowIfNull(ensemble);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(pluginLog);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _watchHistoryService = watchHistoryService;
        _arrIntegration = arrIntegration;
        _ensemble = ensemble;
        _cache = cache;
        _feedbackStore = feedbackStore;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    int ISeerrDiscoveryService.MaxVisiblePerUser => MaxVisiblePerUser;

    /// <inheritdoc />
    public async Task GenerateDiscoveryRecommendationsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            _pluginLog.LogInfo("SeerrDiscovery", "Seerr not configured. Skipping discovery.", _logger);
            return;
        }

        if (config.RecommendationsTaskMode == TaskMode.Deactivate)
        {
            _pluginLog.LogInfo("SeerrDiscovery", "Discovery task is deactivated. Skipping.", _logger);
            return;
        }

        var dryRun = config.RecommendationsTaskMode == TaskMode.DryRun;

        _pluginLog.LogInfo(
            "SeerrDiscovery",
            dryRun
                ? "Starting discovery generation (Dry Run - will not persist)."
                : $"Starting discovery generation (pool={MaxPoolPerUser}, visible={MaxVisiblePerUser} per user).",
            _logger);

        // Step 1: Load user profiles.
        // Include users who have either played content OR have enough favorites to build
        // genre preferences from. BuildGenrePreferenceVector treats favorites as valid
        // preference signals (explicit interest without requiring playback).
        var profiles = _watchHistoryService.GetAllUserWatchProfiles();
        var activeProfiles = profiles
            .Where(p => p.WatchedMovieCount + p.WatchedEpisodeCount > 0 || p.FavoriteCount >= 3)
            .ToList();

        if (activeProfiles.Count == 0)
        {
            _pluginLog.LogInfo("SeerrDiscovery", "No users with watch history or sufficient favorites found. Skipping.", _logger);
            return;
        }

        // Step 1b: Build exclusion set from Arr libraries
        var excludedTmdbIds = await BuildExclusionSetAsync(config, cancellationToken).ConfigureAwait(false);
        _pluginLog.LogDebug(
            "SeerrDiscovery",
            $"Built exclusion set with {excludedTmdbIds.Count} TMDb IDs (library only — per-user dismissed/requested merged later).",
            _logger);

        // Step 2: Process each user
        var allResults = new List<DiscoveryResult>(activeProfiles.Count);

        foreach (var profile in activeProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var userResult = await GenerateForUserAsync(
                    profile, config, excludedTmdbIds, cancellationToken).ConfigureAwait(false);

                if (userResult != null)
                {
                    allResults.Add(userResult);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogWarning(
                    "SeerrDiscovery",
                    $"Failed to generate discovery for user {profile.UserName}: {ex.Message}",
                    ex,
                    _logger);
            }
        }

        // Step 3: Persist or log
        if (dryRun)
        {
            _pluginLog.LogInfo(
                "SeerrDiscovery",
                $"[Dry Run] Would persist {allResults.Count} user results with {allResults.Sum(r => r.Recommendations.Count)} total recommendations.",
                _logger);
        }
        else
        {
            var persisted = _cache.Save(allResults);
            if (persisted)
            {
                _pluginLog.LogInfo(
                    "SeerrDiscovery",
                    $"Persisted {allResults.Count} user results with {allResults.Sum(r => r.Recommendations.Count)} total recommendations.",
                    _logger);

                // Step 4: Record shown items in the feedback store for training data collection.
                // Only record after successful persistence to prevent feedback/training state
                // from referencing recommendations that never actually reached disk.
                // Best-effort: feedback persistence must not break the discovery task.
                foreach (var result in allResults)
                {
                    try
                    {
                        _feedbackStore.RecordShown(result.UserId, result.UserName, result.Recommendations);
                    }
                    catch (Exception ex) when (!ex.IsFatal())
                    {
                        _pluginLog.LogDebug(
                            "SeerrDiscovery",
                            $"Failed to record feedback for user {result.UserName}: {ex.Message}",
                            _logger);
                    }
                }
            }
            else
            {
                _pluginLog.LogWarning(
                    "SeerrDiscovery",
                    $"Failed to persist {allResults.Count} user results. Skipping feedback recording to avoid stale training data.",
                    null,
                    _logger);
            }
        }
    }

    /// <inheritdoc />
    public async Task<(bool Success, string Message)> SubmitRequestAsync(
        int tmdbId,
        string mediaType,
        int? seerrUserId,
        int? serverId,
        int? profileId,
        string? rootFolder,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0)
        {
            return (false, "Invalid TMDb ID.");
        }

        mediaType = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (mediaType is not ("movie" or "tv"))
        {
            return (false, "mediaType must be 'movie' or 'tv'.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrWhiteSpace(config.SeerrUrl)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return (false, "Seerr is not configured.");
        }

        // Defensive boundary guard: reject negative IDs at the service boundary.
        // DTO validation covers the controller path, but this method is public and
        // may be called from other contexts (e.g., admin controller, future internal callers).
        if (serverId.HasValue && serverId.Value < 0)
        {
            return (false, "serverId must be 0 or greater.");
        }

        if (profileId.HasValue && profileId.Value < 0)
        {
            return (false, "profileId must be 0 or greater.");
        }

        Uri baseUri;
        string apiKey;
        try
        {
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, config.SeerrApiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Invalid Seerr configuration: {ex.Message}",
                ex,
                _logger);
            return (false, "Invalid Seerr configuration.");
        }

        var client = GetSeerrClient();
        try
        {
            var payloadDict = new Dictionary<string, object>
            {
                ["mediaType"] = mediaType,
                ["mediaId"] = tmdbId,
                ["is4k"] = false
            };

            // For TV requests, include "seasons": "all" to request all available seasons.
            // Jellyseerr/Overseerr requires the seasons field to be present for TV requests;
            // omitting it causes a server-side crash ("Cannot read properties of undefined
            // (reading 'filter')") because the backend assumes seasons is always defined.
            // The string "all" is the canonical way to request all seasons in Overseerr API v1.
            if (mediaType == "tv")
            {
                payloadDict["seasons"] = "all";
            }

            if (seerrUserId is > 0)
            {
                payloadDict["userId"] = seerrUserId.Value;
            }

            if (serverId.HasValue)
            {
                payloadDict["serverId"] = serverId.Value;
            }

            if (profileId.HasValue)
            {
                payloadDict["profileId"] = profileId.Value;
            }

            if (!string.IsNullOrWhiteSpace(rootFolder))
            {
                payloadDict["rootFolder"] = rootFolder;
            }

            using var content = new StringContent(
                JsonSerializer.Serialize(payloadDict, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var request = BuildRequest(HttpMethod.Post, baseUri, "api/v1/request", apiKey, content);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var userInfo = seerrUserId is > 0 ? $" (as user #{seerrUserId})" : string.Empty;
                _pluginLog.LogInfo(
                    "SeerrDiscovery",
                    $"Request submitted: {mediaType} TMDb#{tmdbId}{userInfo}",
                    _logger);
                return (true, "Request submitted successfully.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Request failed for TMDb#{tmdbId}: HTTP {(int)response.StatusCode} - {body}",
                null,
                _logger);

            // The full error body is already logged above for admin diagnostics.
            // Only return a generic status code to the client to avoid leaking
            // internal Seerr server details (hostnames, config paths, stack traces).
            return (false, $"Seerr returned HTTP {(int)response.StatusCode}. Check the plugin log for details.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Request timed out for TMDb#{tmdbId}",
                ex,
                _logger);
            return (false, "Request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Request failed for TMDb#{tmdbId}: {ex.Message}",
                ex,
                _logger);
            return (false, "Request failed.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeerrUser>> GetSeerrUsersAsync(CancellationToken cancellationToken)
    {
        return await GetCachedSeerrUsersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Fetches the paginated Seerr user list and returns both the user roster
    ///     and a flag indicating whether all pages were fetched successfully.
    ///     The completeness flag is coupled to the result to prevent race conditions
    ///     when multiple threads refresh the cache concurrently.
    /// </summary>
    private async Task<(IReadOnlyList<SeerrUser> Users, bool Complete)> FetchSeerrUsersInternalAsync(
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrWhiteSpace(config.SeerrUrl)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return ([], false);
        }

        Uri baseUri;
        string apiKey;
        try
        {
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, config.SeerrApiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Invalid Seerr configuration for user fetch: {ex.Message}",
                ex,
                _logger);
            return ([], false);
        }

        var client = GetSeerrClient();
        try
        {
            var allUsers = new List<SeerrUser>();
            var skip = 0;
            const int take = 50;
            const int maxPages = 20; // Safety limit to prevent infinite loops
            var fetchComplete = true;

            for (var page = 0; page < maxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var userRequest = BuildRequest(
                    HttpMethod.Get,
                    baseUri,
                    $"api/v1/user?take={take}&skip={skip}&sort=displayname",
                    apiKey,
                    content: null);
                using var response = await client.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _pluginLog.LogWarning(
                        "SeerrDiscovery",
                        $"User list pagination failed at skip={skip}: HTTP {(int)response.StatusCode}. Returning partial result ({allUsers.Count} users fetched so far).",
                        null,
                        _logger);
                    fetchComplete = false;
                    break;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var pageResult = JsonSerializer.Deserialize<SeerrUserPage>(json, JsonOptions);
                if (pageResult?.Results == null || pageResult.Results.Count == 0)
                {
                    break;
                }

                allUsers.AddRange(pageResult.Results);

                // Stop if we've fetched all pages or no more results
                var totalPages = pageResult.PageInfo?.Pages ?? 1;
                var currentPage = (skip / take) + 1;
                if (currentPage >= totalPages || pageResult.Results.Count < take)
                {
                    break;
                }

                skip += take;

                // Safety: if this is the last allowed iteration, mark as incomplete
                if (page == maxPages - 1)
                {
                    _pluginLog.LogWarning(
                        "SeerrDiscovery",
                        $"User list pagination hit the {maxPages}-page safety cap ({allUsers.Count} users fetched). Returning partial result.",
                        null,
                        _logger);
                    fetchComplete = false;
                }
            }

            return (allUsers, fetchComplete);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Failed to fetch Seerr users: {ex.Message}",
                ex,
                _logger);
            return ([], false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeerrServiceInfo>> GetServiceInfoAsync(
        string serviceType,
        CancellationToken cancellationToken)
    {
        var (services, _) = await GetServiceInfoWithStatusAsync(serviceType, cancellationToken).ConfigureAwait(false);
        return services;
    }

    /// <summary>
    ///     Internal variant of <see cref="GetServiceInfoAsync"/> that also returns a success flag
    ///     indicating whether the fetch completed without errors.
    ///     Used by <see cref="GetUserRequestPermissionsAsync"/> to distinguish between
    ///     "no services configured" (success=true, empty list) and "lookup failed" (success=false, empty list).
    /// </summary>
    private async Task<(IReadOnlyList<SeerrServiceInfo> Services, bool Success)> GetServiceInfoWithStatusAsync(
        string serviceType,
        CancellationToken cancellationToken)
    {
        if (serviceType is not ("radarr" or "sonarr"))
        {
            return ([], true);
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrWhiteSpace(config.SeerrUrl)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            // Not configured is a valid state (not a transient failure)
            return ([], true);
        }

        Uri baseUri;
        string apiKey;
        try
        {
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, config.SeerrApiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Invalid Seerr configuration for service info ({serviceType}): {ex.Message}",
                ex,
                _logger);
            return ([], false);
        }

        var client = GetSeerrClient();
        try
        {
            using var listRequest = BuildRequest(HttpMethod.Get, baseUri, $"api/v1/service/{serviceType}", apiKey);
            using var listResponse = await client.SendAsync(listRequest, cancellationToken).ConfigureAwait(false);

            if (!listResponse.IsSuccessStatusCode)
            {
                _pluginLog.LogWarning(
                    "SeerrDiscovery",
                    $"Failed to fetch Seerr {serviceType} services: HTTP {(int)listResponse.StatusCode}.",
                    null,
                    _logger);
                return ([], false);
            }

            var listJson = await listResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var servers = JsonSerializer.Deserialize<List<SeerrServiceInfo>>(listJson, JsonOptions);
            if (servers == null || servers.Count == 0)
            {
                // Successfully fetched but no servers configured
                return ([], true);
            }

            const int maxServerIterations = 10;
            var enrichedServers = new List<SeerrServiceInfo>();
            foreach (var server in servers.Take(maxServerIterations))
            {
                try
                {
                    using var detailRequest = BuildRequest(
                        HttpMethod.Get, baseUri, $"api/v1/service/{serviceType}/{server.Id}", apiKey);
                    using var detailResponse = await client.SendAsync(detailRequest, cancellationToken).ConfigureAwait(false);

                    if (detailResponse.IsSuccessStatusCode)
                    {
                        var detailJson = await detailResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        var detail = JsonSerializer.Deserialize<SeerrServiceInfo>(detailJson, JsonOptions);
                        if (detail != null)
                        {
                            server.Profiles = detail.Profiles;
                            server.RootFolders = detail.RootFolders;
                            server.ActiveProfileId = detail.ActiveProfileId;
                            server.ActiveDirectory = detail.ActiveDirectory;
                        }
                    }
                    else
                    {
                        _pluginLog.LogDebug(
                            "SeerrDiscovery",
                            $"Failed to fetch profiles for {serviceType} server #{server.Id}: HTTP {(int)detailResponse.StatusCode}.",
                            _logger);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or TimeoutException)
                {
                    _pluginLog.LogDebug(
                        "SeerrDiscovery",
                        $"Failed to fetch profiles for {serviceType} server #{server.Id}: {ex.Message}",
                        _logger);
                }

                enrichedServers.Add(server);
            }

            return (enrichedServers, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Failed to fetch Seerr {serviceType} service info: {ex.Message}",
                ex,
                _logger);
            return ([], false);
        }
    }

    /// <inheritdoc />
    public async Task<int?> ResolveSeerrUserIdAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        if (jellyfinUserId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var seerrUsers = await GetCachedSeerrUsersAsync(cancellationToken).ConfigureAwait(false);
            if (seerrUsers.Count == 0)
            {
                // Empty list means either Seerr is unavailable or a partial fetch occurred.
                // Return null — callers on the admin request path (DiscoveryController) treat this
                // as "omit userId" which falls back to the API-key owner. This is acceptable
                // for admin-initiated requests but NOT for user-scoped requests (UserDiscoveryController),
                // which should use GetUserRequestPermissionsAsync for proper tri-state handling.
                return null;
            }

            var match = FindSeerrUserByJellyfinId(seerrUsers, jellyfinUserId);
            if (match != null)
            {
                _pluginLog.LogDebug(
                    "SeerrDiscovery",
                    $"Resolved Jellyfin user {jellyfinUserId} to Seerr user #{match.Id} ({match.DisplayName}).",
                    _logger);
                return match.Id;
            }

            _pluginLog.LogDebug(
                "SeerrDiscovery",
                $"No Seerr user found for Jellyfin user {jellyfinUserId}. Request will use API key owner.",
                _logger);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Failed to resolve Seerr user for Jellyfin user {jellyfinUserId}: {ex.Message}",
                ex,
                _logger);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<UserRequestPermissionResult> GetUserRequestPermissionsAsync(
        Guid jellyfinUserId,
        string mediaType,
        string serviceType,
        CancellationToken cancellationToken)
    {
        // Normalize inputs at the boundary to prevent case/whitespace mismatches
        // from silently falling into wrong permission paths or bypassing validation.
        mediaType = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        serviceType = serviceType?.Trim().ToLowerInvariant() ?? string.Empty;

        if (mediaType is not ("movie" or "tv"))
        {
            return new UserRequestPermissionResult
            {
                CanRequest = false,
                DeniedReason = "Invalid media type."
            };
        }

        if (serviceType is not ("radarr" or "sonarr"))
        {
            return new UserRequestPermissionResult
            {
                CanRequest = false,
                DeniedReason = "Invalid service type."
            };
        }

        // Step 1: Resolve the Jellyfin user to their Seerr account
        var seerrUsers = await GetCachedSeerrUsersAsync(cancellationToken).ConfigureAwait(false);
        var seerrUser = FindSeerrUserByJellyfinId(seerrUsers, jellyfinUserId);

        if (seerrUser == null)
        {
            // Distinguish between "no users fetched" (likely transient failure) and "user not found"
            var isTransient = seerrUsers.Count == 0;
            var deniedReason = isTransient
                ? "Could not verify your Seerr account. The Seerr server may be temporarily unavailable. Please try again."
                : "Your Jellyfin account is not linked to a Seerr account.";

            _pluginLog.LogDebug(
                "SeerrDiscovery",
                $"Permission check: Jellyfin user {jellyfinUserId} — {deniedReason}",
                _logger);

            return new UserRequestPermissionResult
            {
                CanRequest = false,
                DeniedReason = deniedReason,
                IsTransient = isTransient
            };
        }

        // Step 2: Check if the user has request permission for this media type
        if (!seerrUser.CanRequest(mediaType))
        {
            _pluginLog.LogDebug(
                "SeerrDiscovery",
                $"Permission check: Seerr user #{seerrUser.Id} ({seerrUser.DisplayName}) lacks request permission for {mediaType}.",
                _logger);

            return new UserRequestPermissionResult
            {
                CanRequest = false,
                DeniedReason = "You do not have permission to submit requests."
            };
        }

        // Step 3: Determine which quality profiles to expose.
        // Distinguish between "no services configured" (empty result from a successful lookup)
        // and "service lookup failed" (transient error). On transient failure, still allow the
        // request but without profile selection — Seerr's own server defaults will apply.
        // This prevents a temporary Seerr outage from incorrectly routing requests to a wrong
        // server/profile while still allowing the request to proceed (Seerr validates internally).
        var (services, servicesFetchSucceeded) = await GetServiceInfoWithStatusAsync(serviceType, cancellationToken).ConfigureAwait(false);

        if (services.Count == 0)
        {
            if (!servicesFetchSucceeded)
            {
                // Transient failure: allow request with Seerr defaults (no profile selection).
                // Log for admin diagnostics but don't block the user.
                _pluginLog.LogDebug(
                    "SeerrDiscovery",
                    $"Permission check: Service info lookup failed for {serviceType}. Allowing request with server defaults.",
                    _logger);
            }

            // No services configured or transient failure — user can still request with server defaults
            return new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = []
            };
        }

        // Step 4: Expose quality profiles — all profiles for advanced users, default only for normal users.
        var filterToDefault = !seerrUser.CanSelectQualityProfile();
        var profiles = BuildAllowedProfileList(services, filterToDefault);
        return new UserRequestPermissionResult
        {
            CanRequest = true,
            Profiles = profiles
        };
    }

    /// <summary>
    ///     Finds the <see cref="SeerrUser"/> matching the given Jellyfin user ID
    ///     using normalized GUID comparison (no hyphens, case-insensitive).
    /// </summary>
    private static SeerrUser? FindSeerrUserByJellyfinId(
        IReadOnlyList<SeerrUser> seerrUsers,
        Guid jellyfinUserId)
    {
        if (jellyfinUserId == Guid.Empty || seerrUsers.Count == 0)
        {
            return null;
        }

        // ToString("N") produces a 32-char lowercase hex string without hyphens.
        // This is the canonical format we compare against.
        var normalizedJellyfinId = jellyfinUserId.ToString("N");

        foreach (var user in seerrUsers)
        {
            if (string.IsNullOrWhiteSpace(user.JellyfinUserId))
            {
                continue;
            }

            // Fast path: if the Seerr ID is already 32 chars (no hyphens), compare directly
            // without allocating a new string. Seerr stores Jellyfin IDs inconsistently —
            // some have hyphens (36 chars), some don't (32 chars).
            var seerrId = user.JellyfinUserId;
            if (seerrId.Length == 32)
            {
                if (string.Equals(normalizedJellyfinId, seerrId, StringComparison.OrdinalIgnoreCase))
                {
                    return user;
                }
            }
            else if (seerrId.Length == 36)
            {
                // Has hyphens — must normalize (allocates, but only for 36-char IDs)
                var normalized = seerrId.Replace("-", string.Empty, StringComparison.Ordinal);
                if (string.Equals(normalizedJellyfinId, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return user;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Builds the list of <see cref="AllowedQualityProfile"/> entries from the service info.
    ///     When <paramref name="filterToDefault"/> is <c>true</c>, only the server's active (default)
    ///     profile is included per server — this is the path for normal users without advanced permissions.
    /// </summary>
    private static List<AllowedQualityProfile> BuildAllowedProfileList(
        IReadOnlyList<SeerrServiceInfo> services,
        bool filterToDefault)
    {
        var result = new List<AllowedQualityProfile>();

        foreach (var server in services)
        {
            if (filterToDefault)
            {
                // Normal users: only the server's active/default profile
                var defaultProfile = server.Profiles.FirstOrDefault(p => p.Id == server.ActiveProfileId);
                if (defaultProfile != null)
                {
                    result.Add(new AllowedQualityProfile
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        ProfileId = defaultProfile.Id,
                        ProfileName = defaultProfile.Name,
                        IsDefault = true,
                        RootFolder = server.ActiveDirectory
                    });
                }

                // If Seerr does not report a resolvable active/default profile for this server,
                // do not synthesize one from Profiles[0]. The request path will fall back to
                // Seerr's own server defaults, which is safer than over-granting a random profile.
            }
            else
            {
                // Advanced users: all profiles on all servers.
                // Expose each available root folder per profile so the user can select any valid
                // combination. The controller's SubmitMyRequest validates (ServerId, ProfileId, RootFolder)
                // as an exact-match triple — so we must emit a separate entry for each allowed path.
                var rootFolderPaths = server.RootFolders
                    .Where(rf => !string.IsNullOrEmpty(rf.Path))
                    .Select(rf => rf.Path)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                // If the server has no root folders reported (e.g., detail fetch failed),
                // fall back to ActiveDirectory to maintain backward compatibility.
                if (rootFolderPaths.Count == 0 && !string.IsNullOrEmpty(server.ActiveDirectory))
                {
                    rootFolderPaths.Add(server.ActiveDirectory);
                }

                foreach (var profile in server.Profiles)
                {
                    if (rootFolderPaths.Count > 0)
                    {
                        foreach (var rootPath in rootFolderPaths)
                        {
                            result.Add(new AllowedQualityProfile
                            {
                                ServerId = server.Id,
                                ServerName = server.Name,
                                ProfileId = profile.Id,
                                ProfileName = profile.Name,
                                IsDefault = profile.Id == server.ActiveProfileId
                                            && string.Equals(rootPath, server.ActiveDirectory, StringComparison.Ordinal),
                                RootFolder = rootPath
                            });
                        }
                    }
                    else
                    {
                        // No root folders at all — emit with empty RootFolder.
                        // SubmitMyRequest will reject any client-specified rootFolder for this profile,
                        // and the request falls back to Seerr's server-configured default.
                        result.Add(new AllowedQualityProfile
                        {
                            ServerId = server.Id,
                            ServerName = server.Name,
                            ProfileId = profile.Id,
                            ProfileName = profile.Name,
                            IsDefault = profile.Id == server.ActiveProfileId,
                            RootFolder = string.Empty
                        });
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Returns the Seerr user list from the in-memory TTL cache, refreshing it
    ///     from the Seerr API if the cache is expired or empty.
    ///     This avoids re-fetching the full paginated user roster on every
    ///     <see cref="ResolveSeerrUserIdAsync"/> call (triggered per frontend request).
    /// </summary>
    private async Task<IReadOnlyList<SeerrUser>> GetCachedSeerrUsersAsync(CancellationToken cancellationToken)
    {
        // Fast path: check if cache is still valid under lock to ensure atomicity
        // of the (_cachedSeerrUsers, _cachedSeerrUsersExpiry) pair across threads.
        lock (_userCacheLock)
        {
            if (_cachedSeerrUsers != null && DateTime.UtcNow < _cachedSeerrUsersExpiry)
            {
                return _cachedSeerrUsers;
            }
        }

        // Slow path: refresh from Seerr API (outside lock to avoid blocking during I/O).
        // Uses the internal tuple helper so that the completeness flag is coupled
        // to THIS call's result — eliminates the race condition where a concurrent
        // partial fetch could overwrite _lastFetchWasComplete before we read it.
        var (freshUsers, complete) = await FetchSeerrUsersInternalAsync(cancellationToken).ConfigureAwait(false);

        // Only cache complete, non-empty results to allow retry on next call
        // when Seerr is temporarily unavailable or returns partial data.
        // A partial result (mid-pagination failure) must NOT be cached because
        // users on unfetched pages would incorrectly get "not linked to Seerr"
        // for the full TTL instead of the retriable "temporarily unavailable" message.
        if (freshUsers.Count > 0 && complete)
        {
            lock (_userCacheLock)
            {
                // Double-checked: another concurrent caller may have already populated
                // the cache while we were fetching outside the lock.
                if (_cachedSeerrUsers == null || DateTime.UtcNow >= _cachedSeerrUsersExpiry)
                {
                    _cachedSeerrUsers = freshUsers;
                    _cachedSeerrUsersExpiry = DateTime.UtcNow.Add(SeerrUserCacheTtl);
                }
            }
        }

        // Return empty list for incomplete fetches so callers stay on the retriable
        // "temporarily unavailable" path instead of consuming truncated data that would
        // incorrectly mark users on unfetched pages as "not linked to Seerr".
        return complete ? freshUsers : [];
    }

    private async Task<DiscoveryResult?> GenerateForUserAsync(
        UserWatchProfile profile,
        PluginConfiguration config,
        HashSet<(int TmdbId, string MediaType)> excludedTmdbIds,
        CancellationToken cancellationToken)
    {
        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        if (genrePreferences.Count == 0)
        {
            return null;
        }

        var topGenres = genrePreferences
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();

        var avgYear = ContentScoring.ComputeAverageYear(profile);
        var preferredPeople = BuildPreferredPeopleSet(profile);

        // Pre-build the genre exposure analysis ONCE per user. It is passed into every
        // ExternalCandidateFeatureBuilder.Build call below so the GenreUnderexposure /
        // GenreDominanceRatio / GenreAffinityGap features are computed identically to the
        // discovery TRAINING pipeline (DiscoveryFeedbackExampleBuilder). Without this, those
        // three features stayed at 0.0 during inference while the model was trained on their
        // real values — a train/serve skew that suppressed the intended core-taste boost.
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, profile);

        var isChildAccount = profile.MaxParentalRating.HasValue && profile.MaxParentalRating.Value <= ChildAccountMaxParentalRating;

        // Determine user's primary language for language-based discovery
        var primaryLanguage = GetPrimaryLanguageForDiscovery(profile);

        Uri baseUri;
        string apiKey;
        try
        {
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, config.SeerrApiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Invalid Seerr configuration for user {profile.UserName}: {ex.Message}",
                ex,
                _logger);
            return null;
        }

        var client = GetSeerrClient();
        var allCandidates = new List<TmdbDiscoverItem>();

        // === Seerr API uses PATH-based endpoints, NOT query parameters ===
        // Correct: /api/v1/discover/movies/genre/{genreId}?page=1
        // Correct: /api/v1/discover/movies/language/{language}?page=1
        // WRONG:   /api/v1/discover/movies?genre=16&sortBy=... (causes HTTP 400!)

        // Query A: Top genres (use all top-3 genres for movies + TV)
        if (topGenres.Count > 0)
        {
            if (isChildAccount)
            {
                // For child accounts: query Family genre for movies, Kids for TV
                var familyItems = await ExecuteDiscoverQueryAsync(
                    client, baseUri, apiKey, $"api/v1/discover/movies/genre/{TmdbGenreFamily}?page=1", cancellationToken).ConfigureAwait(false);
                allCandidates.AddRange(familyItems);

                var familyItems2 = await ExecuteDiscoverQueryAsync(
                    client, baseUri, apiKey, $"api/v1/discover/movies/genre/{TmdbGenreFamily}?page=2", cancellationToken).ConfigureAwait(false);
                allCandidates.AddRange(familyItems2);

                // Animation + Family for movies (children's animation)
                var animItems = await ExecuteDiscoverQueryAsync(
                    client, baseUri, apiKey, $"api/v1/discover/movies/genre/{TmdbGenreAnimation}?page=1", cancellationToken).ConfigureAwait(false);
                allCandidates.AddRange(animItems);

                // Kids TV genre
                var kidsItems = await ExecuteDiscoverQueryAsync(
                    client, baseUri, apiKey, $"api/v1/discover/tv/genre/{TmdbGenreTvKids}?page=1", cancellationToken).ConfigureAwait(false);
                StampMediaType(kidsItems, "tv");
                allCandidates.AddRange(kidsItems);

                var kidsItems2 = await ExecuteDiscoverQueryAsync(
                    client, baseUri, apiKey, $"api/v1/discover/tv/genre/{TmdbGenreTvKids}?page=2", cancellationToken).ConfigureAwait(false);
                StampMediaType(kidsItems2, "tv");
                allCandidates.AddRange(kidsItems2);

                // Family TV genre
                var familyTvItems = await ExecuteDiscoverQueryAsync(
                    client, baseUri, apiKey, $"api/v1/discover/tv/genre/{TmdbGenreFamily}?page=1", cancellationToken).ConfigureAwait(false);
                StampMediaType(familyTvItems, "tv");
                allCandidates.AddRange(familyTvItems);
            }
            else
            {
                // Normal users: query their top-3 preferred genres
                var movieGenreIds = BuildGenreIdList(topGenres, TmdbGenreMap.ToMovieTmdbId);
                foreach (var genreId in movieGenreIds)
                {
                    var items = await ExecuteDiscoverQueryAsync(
                        client, baseUri, apiKey, $"api/v1/discover/movies/genre/{genreId}?page=1", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(items);
                }

                var tvGenreIds = BuildGenreIdList(topGenres, TmdbGenreMap.ToTvTmdbId);
                foreach (var genreId in tvGenreIds)
                {
                    var items = await ExecuteDiscoverQueryAsync(
                        client, baseUri, apiKey, $"api/v1/discover/tv/genre/{genreId}?page=1", cancellationToken).ConfigureAwait(false);
                    StampMediaType(items, "tv");
                    allCandidates.AddRange(items);
                }

                // Query B: Page 2 of top genre for more variety
                if (movieGenreIds.Count > 0)
                {
                    var items = await ExecuteDiscoverQueryAsync(
                        client, baseUri, apiKey, $"api/v1/discover/movies/genre/{movieGenreIds[0]}?page=2", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(items);
                }

                if (tvGenreIds.Count > 0)
                {
                    var items = await ExecuteDiscoverQueryAsync(
                        client, baseUri, apiKey, $"api/v1/discover/tv/genre/{tvGenreIds[0]}?page=2", cancellationToken).ConfigureAwait(false);
                    StampMediaType(items, "tv");
                    allCandidates.AddRange(items);
                }

                // Query C: Language-based discovery if user has clear preference.
                // primaryLanguage is already validated as ISO 639-1 by GetPrimaryLanguageForDiscovery.
                if (!string.IsNullOrEmpty(primaryLanguage))
                {
                    var langMovies = await ExecuteDiscoverQueryAsync(
                        client, baseUri, apiKey, $"api/v1/discover/movies/language/{primaryLanguage}?page=1", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(langMovies);

                    var langTv = await ExecuteDiscoverQueryAsync(
                        client, baseUri, apiKey, $"api/v1/discover/tv/language/{primaryLanguage}?page=1", cancellationToken).ConfigureAwait(false);
                    StampMediaType(langTv, "tv");
                    allCandidates.AddRange(langTv);
                }
            }
        }

        // Add user-specific dismissed and previously requested items to the exclusion set.
        // Best-effort: failures don't break generation.
        var userExcluded = excludedTmdbIds;
        try
        {
            var dismissed = _feedbackStore.GetDismissedItems(profile.UserId);
            var requested = _feedbackStore.GetRequestedItems(profile.UserId);
            if (dismissed.Count > 0 || requested.Count > 0)
            {
                // Create a per-user copy to avoid mutating the shared set across users
                userExcluded = new HashSet<(int TmdbId, string MediaType)>(excludedTmdbIds);
                userExcluded.UnionWith(dismissed);
                userExcluded.UnionWith(requested);
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                "SeerrDiscovery",
                $"Could not load dismissed/requested items for user {profile.UserName}: {ex.Message}",
                _logger);
        }

        // Deduplicate and filter (includes parental rating + year + quality post-filtering)
        var minVote = isChildAccount ? MinVoteAverageChild : MinVoteAverage;
        var uniqueCandidates = DeduplicateAndFilter(allCandidates, userExcluded, profile.MaxParentalRating, minVote, avgYear, isChildAccount);

        if (uniqueCandidates.Count == 0)
        {
            _pluginLog.LogDebug(
                "SeerrDiscovery",
                $"No viable candidates for user {profile.UserName} after filtering (parental={profile.MaxParentalRating}).",
                _logger);
            return null;
        }

        _pluginLog.LogDebug(
            "SeerrDiscovery",
            $"User {profile.UserName}: {allCandidates.Count} raw candidates → {uniqueCandidates.Count} after filtering.",
            _logger);

        // Phase 1: PRE-SCORE all candidates (without credits/people data from TMDb)
        // This uses genre similarity, rating, recency, year proximity, and popularity
        // but PeopleSimilarity will be 0 since candidates don't have KnownPeople yet.
        var preScored = new List<(TmdbDiscoverItem Item, double Score)>(uniqueCandidates.Count);
        foreach (var candidate in uniqueCandidates)
        {
            var features = ExternalCandidateFeatureBuilder.Build(
                candidate, genrePreferences, preferredPeople, avgYear, genreExposure);
            var score = _ensemble.Score(features);
            preScored.Add((candidate, score));
        }

        // Sort by pre-score and take top-N for credits enrichment
        preScored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var enrichmentCandidates = preScored
            .Take(CreditsEnrichmentBudget)
            .Select(s => s.Item)
            .ToList();

        // Phase 2: ENRICH top candidates with credits data (actors/directors)
        // Only performed when the user has people preferences to match against.
        if (preferredPeople.Count > 0 && enrichmentCandidates.Count > 0)
        {
            await EnrichTopCandidatesWithCreditsAsync(
                client, baseUri, apiKey, enrichmentCandidates, cancellationToken).ConfigureAwait(false);

            var enrichedCount = enrichmentCandidates.Count(c => c.KnownPeople != null);
            _pluginLog.LogDebug(
                "SeerrDiscovery",
                $"User {profile.UserName}: Enriched {enrichedCount}/{enrichmentCandidates.Count} candidates with credits data.",
                _logger);
        }

        // Phase 3: FINAL SCORE the enriched candidates (now with PeopleSimilarity)
        var scored = new List<(TmdbDiscoverItem Item, CandidateFeatures Features, double Score)>(enrichmentCandidates.Count);
        foreach (var candidate in enrichmentCandidates)
        {
            var features = ExternalCandidateFeatureBuilder.Build(
                candidate, genrePreferences, preferredPeople, avgYear, genreExposure);
            var score = _ensemble.Score(features);
            scored.Add((candidate, features, score));
        }

        // Rank and select top-N from enriched candidates
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var topN = scored.Take(MaxPoolPerUser).ToList();

        // Build recommendations
        var recommendations = new List<DiscoveryRecommendation>(topN.Count);
        foreach (var (item, features, score) in topN)
        {
            var genres = TmdbGenreMap.ToJellyfinGenres(item.GenreIds);
            var (reasonKey, relatedInfo) = DetermineReason(features, item, topGenres, preferredPeople);

            recommendations.Add(new DiscoveryRecommendation
            {
                TmdbId = item.Id,
                MediaType = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    ? "tv"
                    : "movie",
                Title = item.DisplayTitle,
                Year = item.EffectiveReleaseDate?.Year,
                Score = score,
                ReasonKey = reasonKey,
                Reason = relatedInfo != null ? $"{reasonKey}: {relatedInfo}" : reasonKey,
                RelatedInfo = relatedInfo,
                Genres = genres,
                TmdbRating = item.VoteAverage,
                // Raw TMDb popularity carried through so RecordShown can persist it for
                // training. This lets DiscoveryFeedbackExampleBuilder reconstruct the exact
                // PopularityScore used at inference (NormalizePopularity) instead of the
                // previous entry.Score proxy, which was a train/serve skew + target leak.
                Popularity = item.Popularity,
                PosterPath = item.PosterPath,
                Overview = item.Overview,
                AlreadyRequested = false,
                KnownPeople = item.KnownPeople
            });
        }

        return new DiscoveryResult
        {
            UserId = profile.UserId,
            UserName = profile.UserName,
            Recommendations = recommendations,
            GeneratedAt = DateTime.UtcNow
        };
    }

    private async Task<List<TmdbDiscoverItem>> ExecuteDiscoverQueryAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        string queryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var request = BuildRequest(HttpMethod.Get, baseUri, queryPath, apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _pluginLog.LogDebug(
                    "SeerrDiscovery",
                    $"Query returned HTTP {(int)response.StatusCode}: {queryPath}",
                    _logger);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<TmdbDiscoverResponse>(json, JsonOptions);

            return page?.Results ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _pluginLog.LogWarning("SeerrDiscovery", $"Query timed out: {queryPath}", ex, _logger);
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            _pluginLog.LogWarning("SeerrDiscovery", $"Query failed: {queryPath} - {ex.Message}", ex, _logger);
            return [];
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(InterQueryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task<HashSet<(int TmdbId, string MediaType)>> BuildExclusionSetAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<(int TmdbId, string MediaType)>();

        // Exclude movies already in Radarr
        foreach (var instance in config.GetEffectiveRadarrInstances())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var movies = await _arrIntegration.GetRadarrMoviesAsync(
                    instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);
                if (movies != null)
                {
                    foreach (var movie in movies.Where(m => m.TmdbId > 0))
                    {
                        excluded.Add((movie.TmdbId, "movie"));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
            {
                _pluginLog.LogWarning(
                    "SeerrDiscovery",
                    $"Failed to fetch Radarr exclusion data from {instance.Url}: {ex.Message}. Continuing with remaining instances.",
                    ex,
                    _logger);
            }
        }

        // Exclude TV series already in Sonarr (Sonarr v4+ provides tmdbId; v3 entries are skipped via TmdbId > 0 guard)
        foreach (var instance in config.GetEffectiveSonarrInstances())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var series = await _arrIntegration.GetSonarrSeriesAsync(
                    instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);
                if (series != null)
                {
                    foreach (var show in series.Where(s => s.TmdbId > 0))
                    {
                        excluded.Add((show.TmdbId, "tv"));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
            {
                _pluginLog.LogWarning(
                    "SeerrDiscovery",
                    $"Failed to fetch Sonarr exclusion data from {instance.Url}: {ex.Message}. Continuing with remaining instances.",
                    ex,
                    _logger);
            }
        }

        return excluded;
    }

    /// <summary>
    ///     Deduplicates candidates against the exclusion set, removes low-rated items,
    ///     applies parental rating filtering, and optionally filters by year relevance.
    /// </summary>
    private static List<TmdbDiscoverItem> DeduplicateAndFilter(
        List<TmdbDiscoverItem> candidates,
        HashSet<(int TmdbId, string MediaType)> excludedTmdbIds,
        int? maxParentalRating,
        double minVoteAverage,
        double avgYear,
        bool isChildAccount)
    {
        // Use (Id, MediaType) tuple for deduplication because TMDb movie IDs and TV IDs
        // occupy separate ID spaces — the same integer can refer to both a movie and a TV show.
        var seen = new HashSet<(int Id, string MediaType)>();
        var result = new List<TmdbDiscoverItem>();

        // For year-based post-filtering: compute acceptable year range
        var currentYear = DateTime.UtcNow.Year;
        var minYear = 0;
        if (!isChildAccount && avgYear > 0)
        {
            // Users who watch modern content: exclude very old films
            if (avgYear >= currentYear - 6)
            {
                minYear = currentYear - 12; // Last 12 years
            }
            else if (avgYear >= 2000)
            {
                minYear = (int)avgYear - 15; // Wide window around their preference
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Id <= 0)
            {
                continue;
            }

            var mediaTypeKey = (candidate.MediaType ?? "movie").ToLowerInvariant();
            if (excludedTmdbIds.Contains((candidate.Id, mediaTypeKey)))
            {
                continue;
            }

            if (candidate.VoteAverage < minVoteAverage)
            {
                continue;
            }

            if (!seen.Add((candidate.Id, mediaTypeKey)))
            {
                continue;
            }

            // Parental rating filter: exclude adult content and restricted genres
            if (ParentalRatingHelper.ShouldExclude(candidate, maxParentalRating))
            {
                continue;
            }

            // Year-based post-filtering (soft: only if year is available and min is set)
            if (minYear > 0
                && candidate.EffectiveReleaseDate.HasValue
                && candidate.EffectiveReleaseDate.Value.Year < minYear)
            {
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static List<string> BuildGenreIdList(
        IEnumerable<string> genres,
        Func<string, int?> mapper)
    {
        return genres
            .Select(mapper)
            .Where(id => id.HasValue)
            .Select(id => id!.Value.ToString(CultureInfo.InvariantCulture))
            .ToList();
    }

    /// <summary>
    ///     Defensively stamps the <see cref="TmdbDiscoverItem.MediaType"/> on items fetched from
    ///     typed discover endpoints. Seerr typed endpoints (e.g. /discover/tv/...) normally include
    ///     mediaType in the response, but this guard ensures correct classification even if the
    ///     field is missing or defaults to "movie".
    /// </summary>
    private static void StampMediaType(List<TmdbDiscoverItem> items, string mediaType)
    {
        foreach (var item in items)
        {
            item.MediaType = mediaType;
        }
    }

    /// <summary>
    ///     Gets the primary language code for Seerr language-based discovery endpoints.
    ///     Returns a 2-letter ISO 639-1 code (e.g. "de", "en") if the user has a clear preference,
    ///     or null if no clear preference is detected.
    /// </summary>
    private static string? GetPrimaryLanguageForDiscovery(UserWatchProfile profile)
    {
        var primaryLang = profile.PrimaryLanguage;
        if (string.IsNullOrWhiteSpace(primaryLang))
        {
            return null;
        }

        // Only use language discovery if the user has actively chosen this language
        // at least 3 times (not just forced because it was the only option)
        if (profile.LanguageProfile.TryGetValue(primaryLang, out var entry) && entry.ChosenCount >= 3)
        {
            var lang = primaryLang.ToLowerInvariant();
            // Validate ISO 639-1 format here — the canonical place that owns the "primary language"
            // decision — so downstream URL-building receives a pre-validated code.
            return lang.Length == 2 && char.IsAsciiLetter(lang[0]) && char.IsAsciiLetter(lang[1]) ? lang : null;
        }

        return null;
    }

    /// <summary>
    ///     Builds the set of preferred people (actors/directors) from the user's watch history.
    ///     Uses the PeopleProfile aggregated by WatchHistoryService from BaseItem.People metadata.
    ///     Returns the top-20 most-watched people (appearing in at least 2 distinct items)
    ///     to filter out noise from single-watch appearances.
    /// </summary>
    private static HashSet<string> BuildPreferredPeopleSet(UserWatchProfile profile)
    {
        if (profile.PeopleProfile.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var topPeople = profile.TopPeople;
        return new HashSet<string>(topPeople, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Enriches the top candidates with credits (cast/director) data from Seerr.
    ///     Fetches /api/v1/movie/{id} or /api/v1/tv/{id} for each candidate and populates
    ///     the <see cref="TmdbDiscoverItem.KnownPeople"/> list with top-billed actors and directors.
    ///     Runs up to <see cref="CreditsEnrichmentParallelism"/> fetches concurrently, each
    ///     bounded by <see cref="CreditsEnrichmentTimeoutMs"/> so a single slow response
    ///     cannot stall the whole enrichment pass.
    /// </summary>
    private async Task EnrichTopCandidatesWithCreditsAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        List<TmdbDiscoverItem> candidates,
        CancellationToken cancellationToken)
    {
        var semaphore = new SemaphoreSlim(CreditsEnrichmentParallelism, CreditsEnrichmentParallelism);
        try
        {
            var tasks = candidates.Select(async candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMilliseconds(CreditsEnrichmentTimeoutMs));

                    var mediaPath = string.Equals(candidate.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                        ? $"api/v1/tv/{candidate.Id}"
                        : $"api/v1/movie/{candidate.Id}";

                    try
                    {
                        using var req = BuildRequest(HttpMethod.Get, baseUri, mediaPath, apiKey);
                        using var response = await client.SendAsync(req, cts.Token).ConfigureAwait(false);

                        if (!response.IsSuccessStatusCode)
                        {
                            return;
                        }

                        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>(json, JsonOptions);

                        if (detail?.Credits == null)
                        {
                            return;
                        }

                        var people = new List<string>(MaxCastPerCandidate);

                        if (detail.Credits.Crew is { Count: > 0 })
                        {
                            foreach (var crew in detail.Credits.Crew.Where(
                                c => string.Equals(c.Job, "Director", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace(c.Name)))
                            {
                                if (people.Count >= MaxCastPerCandidate)
                                {
                                    break;
                                }

                                people.Add(crew.Name);
                            }
                        }

                        if (detail.Credits.Cast is { Count: > 0 })
                        {
                            var actorsToTake = MaxCastPerCandidate - people.Count;
                            if (actorsToTake > 0)
                            {
                                var topActors = detail.Credits.Cast
                                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                                    .OrderBy(c => c.Order)
                                    .Take(actorsToTake)
                                    .Select(c => c.Name);
                                people.AddRange(topActors);
                            }
                        }

                        if (people.Count > 0)
                        {
                            candidate.KnownPeople = people;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or TimeoutException or OperationCanceledException)
                    {
                        _pluginLog.LogDebug(
                            "SeerrDiscovery",
                            $"Credits enrichment failed for {candidate.MediaType}#{candidate.Id}: {ex.Message}",
                            _logger);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Dispose();
        }
    }

    /// <summary>
    ///     Determines the primary recommendation reason based on the candidate's feature scores.
    ///     Priority: Person match > Genre match > Trending > Popular.
    /// </summary>
    private static (string ReasonKey, string? RelatedInfo) DetermineReason(
        CandidateFeatures features,
        TmdbDiscoverItem candidate,
        List<string> topGenres,
        HashSet<string> preferredPeople)
    {
        // Person-based reason: when a known actor/director matches the user's preferences
        if (features.PeopleSimilarity > 0.3 && candidate.KnownPeople is { Count: > 0 })
        {
            // Only surface a person reason if a preferred person was actually matched.
            // Avoids showing an arbitrary non-preferred person name due to case-mismatch edge cases.
            var matchedPerson = candidate.KnownPeople.FirstOrDefault(p => preferredPeople.Contains(p));
            if (matchedPerson != null)
            {
                return ("reasonPersonNamed", matchedPerson);
            }
        }

        if (features.GenreSimilarity > 0.7 && topGenres.Count > 0)
        {
            return ("reasonGenre", topGenres[0]);
        }

        if (features.RecencyScore > 0.8 && features.CombinedCriticScore > 0.7)
        {
            return ("reasonTrending", null);
        }

        return ("reasonPopular", null);
    }

    /// <summary>
    ///     Validates the Seerr base URL and API key and returns a pre-computed base
    ///     <see cref="Uri"/> and the sanitised key.  Does NOT retrieve or mutate an
    ///     <see cref="HttpClient"/> — callers obtain the client separately via
    ///     <see cref="GetSeerrClient"/> and attach headers per-request with
    ///     <see cref="BuildRequest"/>.
    /// </summary>
    /// <returns>A tuple of (normalised base URI, apiKey).</returns>
    /// <exception cref="UriFormatException">Thrown when the URL is not a valid http/https URI.</exception>
    /// <exception cref="ArgumentException">Thrown when the API key is empty or contains CR/LF.</exception>
    private static (Uri BaseUri, string ApiKey) ValidateSeerrConfig(string baseUrl, string apiKey)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsedBaseUrl) ||
            (parsedBaseUrl.Scheme != Uri.UriSchemeHttp && parsedBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException("Invalid Seerr base URL.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        EnsureApiKeyHeaderSafe(apiKey);

        var baseUri = new Uri(parsedBaseUrl.AbsoluteUri.TrimEnd('/') + "/");
        return (baseUri, apiKey);
    }

    /// <summary>
    ///     Returns a non-owning <see cref="HttpClient"/> from the factory.
    ///     The client must NOT be disposed — its lifetime is managed by
    ///     <see cref="IHttpClientFactory"/>.
    /// </summary>
    private HttpClient GetSeerrClient() =>
        _httpClientFactory.CreateClient("SeerrDiscovery");

    /// <summary>
    ///     Builds an <see cref="HttpRequestMessage"/> with per-request authentication headers.
    ///     Headers are set on the message, never on <c>HttpClient.DefaultRequestHeaders</c>,
    ///     so concurrent callers sharing the same pooled handler cannot observe each other's
    ///     headers and duplicate-header accumulation is eliminated.
    /// </summary>
    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        Uri baseUri,
        string relPath,
        string apiKey,
        HttpContent? content = null)
    {
        var requestUri = new Uri(baseUri, relPath);
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (content != null)
        {
            request.Content = content;
        }

        return request;
    }

    /// <summary>
    ///     Throws <see cref="ArgumentException"/> if <paramref name="apiKey"/> contains CR or LF.
    ///     <c>HttpRequestHeaders.TryAddWithoutValidation</c> tolerates non-ASCII keys but does
    ///     not strip CRLF sequences, which would allow HTTP header injection.
    /// </summary>
    private static void EnsureApiKeyHeaderSafe(string apiKey)
    {
        if (apiKey.Contains('\r', StringComparison.Ordinal)
            || apiKey.Contains('\n', StringComparison.Ordinal)
            || apiKey.Contains('\t', StringComparison.Ordinal)
            || apiKey.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("API key must not contain CR, LF, tab, or NUL characters.", nameof(apiKey));
        }
    }
}
