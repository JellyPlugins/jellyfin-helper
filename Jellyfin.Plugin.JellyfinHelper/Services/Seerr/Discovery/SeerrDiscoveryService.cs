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
    ///     Fixed number of discovery recommendations per user. Not configurable.
    /// </summary>
    private const int MaxDiscoveryPerUser = 10;

    /// <summary>
    ///     Number of top candidates (by pre-score) to enrich with credits data.
    ///     Credits calls are expensive (1 API call per item × 500ms delay), so we only
    ///     enrich the most promising candidates after an initial genre/rating-based pre-score.
    /// </summary>
    private const int CreditsEnrichmentBudget = 20;

    /// <summary>
    ///     Maximum number of cast members to extract per candidate during credits enrichment.
    ///     Only top-billed actors (by order) and directors are included.
    /// </summary>
    private const int MaxCastPerCandidate = 10;

    /// <summary>
    ///     Maximum Jellyfin parental rating value that triggers the child-account discovery path.
    ///     Corresponds to FSK-6 / G / PG — users with this rating or below receive only
    ///     Family/Kids/Animation content from discovery queries.
    /// </summary>
    private const int ChildAccountMaxParentalRating = 60;

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
                : $"Starting discovery generation (max {MaxDiscoveryPerUser} per user).",
            _logger);

        // Step 1: Load user profiles
        var profiles = _watchHistoryService.GetAllUserWatchProfiles();
        var activeProfiles = profiles
            .Where(p => p.WatchedMovieCount + p.WatchedEpisodeCount > 0)
            .ToList();

        if (activeProfiles.Count == 0)
        {
            _pluginLog.LogInfo("SeerrDiscovery", "No users with watch history found. Skipping.", _logger);
            return;
        }

        // Step 1b: Build exclusion set from Arr libraries
        var excludedTmdbIds = await BuildExclusionSetAsync(config, cancellationToken).ConfigureAwait(false);
        _pluginLog.LogDebug(
            "SeerrDiscovery",
            $"Built exclusion set with {excludedTmdbIds.Count} TMDb IDs (library + requests).",
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
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
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
            _cache.Save(allResults);
            _pluginLog.LogInfo(
                "SeerrDiscovery",
                $"Persisted {allResults.Count} user results with {allResults.Sum(r => r.Recommendations.Count)} total recommendations.",
                _logger);

            // Step 4: Record shown items in the feedback store for training data collection.
            // Best-effort: feedback persistence must not break the discovery task.
            foreach (var result in allResults)
            {
                try
                {
                    _feedbackStore.RecordShown(result.UserId, result.UserName, result.Recommendations);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    _pluginLog.LogDebug(
                        "SeerrDiscovery",
                        $"Failed to record feedback for user {result.UserName}: {ex.Message}",
                        _logger);
                }
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

        HttpClient client;
        try
        {
            client = CreateClient(config.SeerrUrl, config.SeerrApiKey);
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

        using (client)
        {
            try
            {
                var payloadDict = new Dictionary<string, object>
                {
                    ["mediaType"] = mediaType,
                    ["mediaId"] = tmdbId,
                    ["is4k"] = false
                };

                // For TV requests, omit "seasons" entirely. Overseerr/Jellyseerr
                // auto-requests all available seasons when the key is absent.
                // Sending "all" as a string is not a valid API format.

                if (seerrUserId is > 0)
                {
                    payloadDict["userId"] = seerrUserId.Value;
                }

                if (serverId is > 0)
                {
                    payloadDict["serverId"] = serverId.Value;
                }

                if (profileId is > 0)
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

                using var response = await client.PostAsync(
                    new Uri("api/v1/request", UriKind.Relative),
                    content,
                    cancellationToken).ConfigureAwait(false);

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
                return (false, $"Seerr returned HTTP {(int)response.StatusCode}.");
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
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeerrUser>> GetSeerrUsersAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrWhiteSpace(config.SeerrUrl)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return [];
        }

        HttpClient client;
        try
        {
            client = CreateClient(config.SeerrUrl, config.SeerrApiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Invalid Seerr configuration for user fetch: {ex.Message}",
                ex,
                _logger);
            return [];
        }

        using (client)
        {
            try
            {
                var allUsers = new List<SeerrUser>();
                var skip = 0;
                const int take = 50;
                const int maxPages = 20; // Safety limit to prevent infinite loops

                for (var page = 0; page < maxPages; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var response = await client.GetAsync(
                        new Uri($"api/v1/user?take={take}&skip={skip}&sort=displayname", UriKind.Relative),
                        cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
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
                }

                return allUsers;
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
                return [];
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeerrServiceInfo>> GetServiceInfoAsync(
        string serviceType,
        CancellationToken cancellationToken)
    {
        if (serviceType is not ("radarr" or "sonarr"))
        {
            return [];
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrWhiteSpace(config.SeerrUrl)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return [];
        }

        HttpClient client;
        try
        {
            client = CreateClient(config.SeerrUrl, config.SeerrApiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "SeerrDiscovery",
                $"Invalid Seerr configuration for service info ({serviceType}): {ex.Message}",
                ex,
                _logger);
            return [];
        }

        using (client)
        {
            try
            {
                // Step 1: Get the server list (basic info without profiles)
                // Seerr API: GET /service/radarr → server list (id, name, isDefault, is4k)
                using var listResponse = await client.GetAsync(
                    new Uri($"api/v1/service/{serviceType}", UriKind.Relative),
                    cancellationToken).ConfigureAwait(false);

                if (!listResponse.IsSuccessStatusCode)
                {
                    return [];
                }

                var listJson = await listResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var servers = JsonSerializer.Deserialize<List<SeerrServiceInfo>>(listJson, JsonOptions);
                if (servers == null || servers.Count == 0)
                {
                    return [];
                }

                // Step 2: For each server, fetch quality profiles and root folders
                // Seerr API: GET /service/radarr/{radarrId} → { profiles: [...], rootFolders: [...] }
                // Safety limit: no realistic setup has >10 Radarr/Sonarr servers
                const int maxServerIterations = 10;
                var enrichedServers = new List<SeerrServiceInfo>();
                foreach (var server in servers.Take(maxServerIterations))
                {
                    try
                    {
                        using var detailResponse = await client.GetAsync(
                            new Uri($"api/v1/service/{serviceType}/{server.Id}", UriKind.Relative),
                            cancellationToken).ConfigureAwait(false);

                        if (detailResponse.IsSuccessStatusCode)
                        {
                            var detailJson = await detailResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            var detail = JsonSerializer.Deserialize<SeerrServiceInfo>(detailJson, JsonOptions);
                            if (detail != null)
                            {
                                // Merge: keep the server-level info but add profiles/rootFolders from detail
                                server.Profiles = detail.Profiles;
                                server.RootFolders = detail.RootFolders;
                                server.ActiveProfileId = detail.ActiveProfileId;
                                server.ActiveDirectory = detail.ActiveDirectory;
                            }
                        }
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

                return enrichedServers;
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
                return [];
            }
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
                return null;
            }

            // Normalize the Jellyfin user ID: no hyphens (ToString("N") already returns lowercase hex)
            var normalizedJellyfinId = jellyfinUserId.ToString("N");

            foreach (var seerrUser in seerrUsers)
            {
                if (string.IsNullOrWhiteSpace(seerrUser.JellyfinUserId))
                {
                    continue;
                }

                // Normalize the Seerr-stored Jellyfin ID: remove hyphens, lowercase
                var normalizedSeerrJellyfinId = seerrUser.JellyfinUserId
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant();

                if (string.Equals(normalizedJellyfinId, normalizedSeerrJellyfinId, StringComparison.Ordinal))
                {
                    _pluginLog.LogDebug(
                        "SeerrDiscovery",
                        $"Resolved Jellyfin user {jellyfinUserId} to Seerr user #{seerrUser.Id} ({seerrUser.DisplayName}).",
                        _logger);
                    return seerrUser.Id;
                }
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
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
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
        // Step 1: Resolve the Jellyfin user to their Seerr account
        var seerrUsers = await GetCachedSeerrUsersAsync(cancellationToken).ConfigureAwait(false);
        var seerrUser = FindSeerrUserByJellyfinId(seerrUsers, jellyfinUserId);

        if (seerrUser == null)
        {
            _pluginLog.LogDebug(
                "SeerrDiscovery",
                $"Permission check: Jellyfin user {jellyfinUserId} has no linked Seerr account.",
                _logger);

            return new UserRequestPermissionResult
            {
                CanRequest = false,
                DeniedReason = "Your Jellyfin account is not linked to a Seerr account."
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

        // Step 3: Determine which quality profiles to expose
        var services = await GetServiceInfoAsync(serviceType, cancellationToken).ConfigureAwait(false);

        if (services.Count == 0)
        {
            // No services configured — user can still request with server defaults
            return new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = []
            };
        }

        // Step 4: If user has advanced profile selection permission, expose all profiles
        if (seerrUser.CanSelectQualityProfile())
        {
            var allProfiles = BuildAllowedProfileList(services, filterToDefault: false);
            return new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = allProfiles
            };
        }

        // Step 5: Normal user — only expose the default profile per server
        var defaultProfiles = BuildAllowedProfileList(services, filterToDefault: true);
        return new UserRequestPermissionResult
        {
            CanRequest = true,
            Profiles = defaultProfiles
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

        var normalizedJellyfinId = jellyfinUserId.ToString("N");

        foreach (var user in seerrUsers)
        {
            if (string.IsNullOrWhiteSpace(user.JellyfinUserId))
            {
                continue;
            }

            var normalizedSeerrJellyfinId = user.JellyfinUserId
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            if (string.Equals(normalizedJellyfinId, normalizedSeerrJellyfinId, StringComparison.Ordinal))
            {
                return user;
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
                else if (server.Profiles.Count > 0)
                {
                    // Fallback: if active profile ID doesn't match, use the first one
                    var fallback = server.Profiles[0];
                    result.Add(new AllowedQualityProfile
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        ProfileId = fallback.Id,
                        ProfileName = fallback.Name,
                        IsDefault = true,
                        RootFolder = server.ActiveDirectory
                    });
                }
            }
            else
            {
                // Advanced users: all profiles on all servers
                foreach (var profile in server.Profiles)
                {
                    result.Add(new AllowedQualityProfile
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        ProfileId = profile.Id,
                        ProfileName = profile.Name,
                        IsDefault = profile.Id == server.ActiveProfileId,
                        RootFolder = server.ActiveDirectory
                    });
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
        // Fast path: check if cache is still valid (no lock needed for read)
        if (_cachedSeerrUsers != null && DateTime.UtcNow < _cachedSeerrUsersExpiry)
        {
            return _cachedSeerrUsers;
        }

        // Slow path: refresh from Seerr API
        var freshUsers = await GetSeerrUsersAsync(cancellationToken).ConfigureAwait(false);

        // Only cache successful (non-empty) results to allow retry on next call
        // when Seerr is temporarily unavailable.
        if (freshUsers.Count > 0)
        {
            lock (_userCacheLock)
            {
                _cachedSeerrUsers = freshUsers;
                _cachedSeerrUsersExpiry = DateTime.UtcNow.Add(SeerrUserCacheTtl);
            }
        }

        return freshUsers;
    }

    private async Task<DiscoveryResult?> GenerateForUserAsync(
        UserWatchProfile profile,
        PluginConfiguration config,
        HashSet<int> excludedTmdbIds,
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
        var isChildAccount = profile.MaxParentalRating.HasValue && profile.MaxParentalRating.Value <= ChildAccountMaxParentalRating;

        // Determine user's primary language for language-based discovery
        var primaryLanguage = GetPrimaryLanguageForDiscovery(profile);

        HttpClient client;
        try
        {
            client = CreateClient(config.SeerrUrl, config.SeerrApiKey);
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

        using (client)
        {
            var allCandidates = new List<TmdbDiscoverItem>();

            // === Seerr API uses PATH-based endpoints, NOT query parameters ===
            // Correct: /api/v1/discover/movies/genre/{genreId}?page=1
            // Correct: /api/v1/discover/movies/language/{language}?page=1
            // WRONG:   /api/v1/discover/movies?genre=16&sortBy=... (causes HTTP 400!)

            // Query A: Top genres (use all top-3 genres for movies + TV)
            if (topGenres.Count >= 1)
            {
                if (isChildAccount)
                {
                    // For child accounts: query Family (10751) genre for movies, Kids (10762) for TV
                    var familyItems = await ExecuteDiscoverQueryAsync(
                        client, "api/v1/discover/movies/genre/10751?page=1", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(familyItems);

                    var familyItems2 = await ExecuteDiscoverQueryAsync(
                        client, "api/v1/discover/movies/genre/10751?page=2", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(familyItems2);

                    // Animation + Family for movies (children's animation)
                    var animItems = await ExecuteDiscoverQueryAsync(
                        client, "api/v1/discover/movies/genre/16?page=1", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(animItems);

                    // Kids TV genre
                    var kidsItems = await ExecuteDiscoverQueryAsync(
                        client, "api/v1/discover/tv/genre/10762?page=1", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(kidsItems);

                    var kidsItems2 = await ExecuteDiscoverQueryAsync(
                        client, "api/v1/discover/tv/genre/10762?page=2", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(kidsItems2);

                    // Family TV genre
                    var familyTvItems = await ExecuteDiscoverQueryAsync(
                        client, "api/v1/discover/tv/genre/10751?page=1", cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(familyTvItems);
                }
                else
                {
                    // Normal users: query their top-3 preferred genres
                    var movieGenreIds = BuildGenreIdList(topGenres, TmdbGenreMap.ToMovieTmdbId);
                    foreach (var genreId in movieGenreIds)
                    {
                        var items = await ExecuteDiscoverQueryAsync(
                            client, $"api/v1/discover/movies/genre/{genreId}?page=1", cancellationToken).ConfigureAwait(false);
                        allCandidates.AddRange(items);
                    }

                    var tvGenreIds = BuildGenreIdList(topGenres, TmdbGenreMap.ToTvTmdbId);
                    foreach (var genreId in tvGenreIds)
                    {
                        var items = await ExecuteDiscoverQueryAsync(
                            client, $"api/v1/discover/tv/genre/{genreId}?page=1", cancellationToken).ConfigureAwait(false);
                        allCandidates.AddRange(items);
                    }

                    // Query B: Page 2 of top genre for more variety
                    if (movieGenreIds.Count > 0)
                    {
                        var items = await ExecuteDiscoverQueryAsync(
                            client, $"api/v1/discover/movies/genre/{movieGenreIds[0]}?page=2", cancellationToken).ConfigureAwait(false);
                        allCandidates.AddRange(items);
                    }

                    if (tvGenreIds.Count > 0)
                    {
                        var items = await ExecuteDiscoverQueryAsync(
                            client, $"api/v1/discover/tv/genre/{tvGenreIds[0]}?page=2", cancellationToken).ConfigureAwait(false);
                        allCandidates.AddRange(items);
                    }

                    // Query C: Language-based discovery if user has clear preference
                    if (!string.IsNullOrEmpty(primaryLanguage))
                    {
                        var langMovies = await ExecuteDiscoverQueryAsync(
                            client, $"api/v1/discover/movies/language/{primaryLanguage}?page=1", cancellationToken).ConfigureAwait(false);
                        allCandidates.AddRange(langMovies);

                        var langTv = await ExecuteDiscoverQueryAsync(
                            client, $"api/v1/discover/tv/language/{primaryLanguage}?page=1", cancellationToken).ConfigureAwait(false);
                        allCandidates.AddRange(langTv);
                    }
                }
            }

            // Deduplicate and filter (includes parental rating + year + quality post-filtering)
            var minVote = isChildAccount ? MinVoteAverageChild : MinVoteAverage;
            var uniqueCandidates = DeduplicateAndFilter(allCandidates, excludedTmdbIds, profile.MaxParentalRating, minVote, avgYear, isChildAccount);

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
                    candidate, genrePreferences, preferredPeople, avgYear);
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
                    client, enrichmentCandidates, cancellationToken).ConfigureAwait(false);

                _pluginLog.LogDebug(
                    "SeerrDiscovery",
                    $"User {profile.UserName}: Enriched {enrichmentCandidates.Count(c => c.KnownPeople != null)}/{enrichmentCandidates.Count} candidates with credits data.",
                    _logger);
            }

            // Phase 3: FINAL SCORE the enriched candidates (now with PeopleSimilarity)
            var scored = new List<(TmdbDiscoverItem Item, CandidateFeatures Features, double Score)>(enrichmentCandidates.Count);
            foreach (var candidate in enrichmentCandidates)
            {
                var features = ExternalCandidateFeatureBuilder.Build(
                    candidate, genrePreferences, preferredPeople, avgYear);
                var score = _ensemble.Score(features);
                scored.Add((candidate, features, score));
            }

            // Rank and select top-N from enriched candidates
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            var topN = scored.Take(MaxDiscoveryPerUser).ToList();

            // Build recommendations
            var recommendations = new List<DiscoveryRecommendation>(topN.Count);
            foreach (var (item, features, score) in topN)
            {
                var genres = TmdbGenreMap.ToJellyfinGenres(item.GenreIds);
                var (reasonKey, relatedInfo) = DetermineReason(features, item, topGenres, preferredPeople);

                recommendations.Add(new DiscoveryRecommendation
                {
                    TmdbId = item.Id,
                    MediaType = item.MediaType ?? "movie",
                    Title = item.DisplayTitle,
                    Year = item.EffectiveReleaseDate?.Year,
                    Score = score,
                    ReasonKey = reasonKey,
                    Reason = relatedInfo != null ? $"{reasonKey}: {relatedInfo}" : reasonKey,
                    RelatedInfo = relatedInfo,
                    Genres = genres,
                    TmdbRating = item.VoteAverage,
                    PosterPath = item.PosterPath,
                    Overview = item.Overview,
                    AlreadyRequested = false
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
    }

    private async Task<List<TmdbDiscoverItem>> ExecuteDiscoverQueryAsync(
        HttpClient client,
        string queryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var response = await client.GetAsync(
                new Uri(queryPath, UriKind.Relative),
                cancellationToken).ConfigureAwait(false);

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
            // Skip the rate-limit delay if cancellation has been requested —
            // no point sleeping when the entire operation is being torn down.
            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(InterQueryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<HashSet<int>> BuildExclusionSetAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<int>();

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
                        excluded.Add(movie.TmdbId);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                _pluginLog.LogWarning(
                    "SeerrDiscovery",
                    $"Failed to fetch Radarr exclusion data from {instance.Url}: {ex.Message}. Continuing with remaining instances.",
                    ex,
                    _logger);
            }
        }

        // Exclude TV series already in Sonarr (Sonarr v3+ provides tmdbId)
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
                        excluded.Add(show.TmdbId);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
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
        HashSet<int> excludedTmdbIds,
        int? maxParentalRating,
        double minVoteAverage,
        double avgYear,
        bool isChildAccount)
    {
        var seen = new HashSet<int>();
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

            if (excludedTmdbIds.Contains(candidate.Id))
            {
                continue;
            }

            if (candidate.VoteAverage < minVoteAverage)
            {
                continue;
            }

            if (!seen.Add(candidate.Id))
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
            return primaryLang.ToLowerInvariant();
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
    ///     Respects rate limiting with inter-query delay.
    /// </summary>
    private async Task EnrichTopCandidatesWithCreditsAsync(
        HttpClient client,
        List<TmdbDiscoverItem> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mediaPath = string.Equals(candidate.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? $"api/v1/tv/{candidate.Id}"
                : $"api/v1/movie/{candidate.Id}";

            try
            {
                using var response = await client.GetAsync(
                    new Uri(mediaPath, UriKind.Relative),
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>(json, JsonOptions);

                if (detail?.Credits == null)
                {
                    continue;
                }

                var people = new List<string>(MaxCastPerCandidate);

                // Add directors first (high signal value)
                if (detail.Credits.Crew is { Count: > 0 })
                {
                    foreach (var crew in detail.Credits.Crew.Where(
                        c => string.Equals(c.Job, "Director", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(c.Name)))
                    {
                        people.Add(crew.Name);
                    }
                }

                // Add top-billed actors (sorted by order)
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
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or TimeoutException)
            {
                _pluginLog.LogDebug(
                    "SeerrDiscovery",
                    $"Credits enrichment failed for {candidate.MediaType}#{candidate.Id}: {ex.Message}",
                    _logger);
            }
            finally
            {
                // Skip the rate-limit delay if cancellation has been requested —
                // no point sleeping when the entire operation is being torn down.
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(InterQueryDelay, CancellationToken.None).ConfigureAwait(false);
                }
            }
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
            // Return the actually matched person name (not just the first in the list)
            var matchedPerson = candidate.KnownPeople.FirstOrDefault(p => preferredPeople.Contains(p));
            return ("reasonPerson", matchedPerson ?? candidate.KnownPeople[0]);
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

    private HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = _httpClientFactory.CreateClient("SeerrDiscovery");

        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsedBaseUrl) ||
            (parsedBaseUrl.Scheme != Uri.UriSchemeHttp && parsedBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException("Invalid Seerr base URL.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        client.BaseAddress = new Uri(parsedBaseUrl.AbsoluteUri.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }
}
