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

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

    /// <summary>
    ///     Delay between TMDb discovery queries via Seerr to respect rate limits.
    /// </summary>
    private static readonly TimeSpan InterQueryDelay = TimeSpan.FromMilliseconds(500);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IArrIntegrationService _arrIntegration;
    private readonly HeuristicScoringStrategy _heuristic;
    private readonly DiscoveryCacheService _cache;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<SeerrDiscoveryService> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SeerrDiscoveryService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="arrIntegration">The Arr integration service.</param>
    /// <param name="heuristic">The heuristic scoring strategy.</param>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public SeerrDiscoveryService(
        IHttpClientFactory httpClientFactory,
        IWatchHistoryService watchHistoryService,
        IArrIntegrationService arrIntegration,
        HeuristicScoringStrategy heuristic,
        DiscoveryCacheService cache,
        IPluginLogService pluginLog,
        ILogger<SeerrDiscoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(watchHistoryService);
        ArgumentNullException.ThrowIfNull(arrIntegration);
        ArgumentNullException.ThrowIfNull(heuristic);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(pluginLog);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _watchHistoryService = watchHistoryService;
        _arrIntegration = arrIntegration;
        _heuristic = heuristic;
        _cache = cache;
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
        var maxPerUser = config.SeerrDiscoveryMaxPerUser;

        _pluginLog.LogInfo(
            "SeerrDiscovery",
            dryRun
                ? "Starting discovery generation (Dry Run - will not persist)."
                : $"Starting discovery generation (max {maxPerUser} per user).",
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
                    profile, config, excludedTmdbIds, maxPerUser, cancellationToken).ConfigureAwait(false);

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
            return (false, $"Invalid Seerr configuration: {ex.Message}");
        }

        using (client)
        {
            try
            {
                var payloadDict = new Dictionary<string, object>
                {
                    ["mediaType"] = mediaType,
                    ["mediaId"] = tmdbId,
                    ["is4k"] = false,
                    ["seasons"] = "all"
                };

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

                object payload = payloadDict;

                var content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
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
                    $"Request failed for TMDb#{tmdbId}: HTTP {(int)response.StatusCode}",
                    logger: _logger);
                return (false, $"Seerr returned HTTP {(int)response.StatusCode}: {body}");
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
                return (false, $"Request timed out: {ex.Message}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
            {
                _pluginLog.LogWarning(
                    "SeerrDiscovery",
                    $"Request failed for TMDb#{tmdbId}: {ex.Message}",
                    ex,
                    _logger);
                return (false, $"Request failed: {ex.Message}");
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
                using var response = await client.GetAsync(
                    new Uri("api/v1/user?take=50&skip=0&sort=displayname", UriKind.Relative),
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var page = JsonSerializer.Deserialize<SeerrUserPage>(json, JsonOptions);
                return page?.Results ?? [];
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
                var enrichedServers = new List<SeerrServiceInfo>();
                foreach (var server in servers)
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
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

    private async Task<DiscoveryResult?> GenerateForUserAsync(
        UserWatchProfile profile,
        PluginConfiguration config,
        HashSet<int> excludedTmdbIds,
        int maxPerUser,
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
        var isChildAccount = profile.MaxParentalRating.HasValue && profile.MaxParentalRating.Value <= 60;

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

            // Score candidates using heuristic strategy (same features as recommendation engine)
            var scored = new List<(TmdbDiscoverItem Item, CandidateFeatures Features, double Score)>(uniqueCandidates.Count);
            foreach (var candidate in uniqueCandidates)
            {
                var features = ExternalCandidateFeatureBuilder.Build(
                    candidate, genrePreferences, preferredPeople, avgYear);
                var score = _heuristic.Score(features);
                scored.Add((candidate, features, score));
            }

            // Rank and select top-N
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            var topN = scored.Take(maxPerUser).ToList();

            // Build recommendations
            var recommendations = new List<DiscoveryRecommendation>(topN.Count);
            foreach (var (item, features, score) in topN)
            {
                var genres = TmdbGenreMap.ToJellyfinGenres(item.GenreIds);
                var (reasonKey, relatedInfo) = DetermineReason(features, item, topGenres);

                recommendations.Add(new DiscoveryRecommendation
                {
                    TmdbId = item.Id,
                    MediaType = item.MediaType ?? "movie",
                    Title = item.DisplayTitle,
                    Year = item.EffectiveReleaseDate?.Year,
                    Score = score,
                    ReasonKey = reasonKey,
                    Reason = reasonKey,
                    RelatedInfo = relatedInfo,
                    Genres = genres,
                    TmdbRating = item.VoteAverage,
                    PosterPath = item.PosterPath,
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
            await Task.Delay(InterQueryDelay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<HashSet<int>> BuildExclusionSetAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<int>();

        foreach (var instance in config.GetEffectiveRadarrInstances())
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (minYear > 0 && candidate.EffectiveReleaseDate.HasValue)
            {
                if (candidate.EffectiveReleaseDate.Value.Year < minYear)
                {
                    continue;
                }
            }

            result.Add(candidate);
        }

        return result;
    }

    private static List<string> BuildGenreIdList(
        IEnumerable<string> genres,
        Func<string, int?> mapper)
    {
        var ids = new List<string>();
        foreach (var genre in genres)
        {
            var id = mapper(genre);
            if (id.HasValue)
            {
                ids.Add(id.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }

    private static double ComputeMinRating(UserWatchProfile profile)
    {
        return Math.Max(6.0, profile.AverageCommunityRating > 0 ? profile.AverageCommunityRating : 6.0);
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
    ///     Extracts people names from watched items if available.
    /// </summary>
    private static HashSet<string> BuildPreferredPeopleSet(UserWatchProfile profile)
    {
        // Currently TMDb discover responses don't include cast data,
        // so people-based filtering has limited value. Return empty for now.
        // Future: could extract from WatchedItems if people metadata is cached.
        _ = profile;
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Builds a language query parameter from the user's language profile.
    ///     Uses the primary preferred language (if determined via active choice, not forced).
    /// </summary>
    private static string? BuildLanguageParam(UserWatchProfile profile)
    {
        var primaryLang = profile.PrimaryLanguage;
        if (string.IsNullOrWhiteSpace(primaryLang))
        {
            return null;
        }

        // Only apply language filter if the user has a clear preference
        // (at least 3 chosen instances of the language)
        if (profile.LanguageProfile.TryGetValue(primaryLang, out var entry) && entry.ChosenCount >= 3)
        {
            return $"&language={Uri.EscapeDataString(primaryLang)}";
        }

        return null;
    }

    /// <summary>
    ///     Builds a year filter parameter based on the user's average watched year.
    ///     For users who watch mostly modern content, restricts results to recent years.
    ///     For child accounts, no year restriction (children watch both old and new content).
    /// </summary>
    private static string? BuildYearParam(double avgYear, bool isChildAccount)
    {
        if (isChildAccount)
        {
            // Children watch Disney classics from 1990s as well as new content - no year filter
            return null;
        }

        if (avgYear <= 0)
        {
            return null;
        }

        // If user's average year is recent (e.g. 2018+), restrict to last ~8 years
        var currentYear = DateTime.UtcNow.Year;
        if (avgYear >= currentYear - 6)
        {
            var minYear = currentYear - 8;
            return $"&primary_release_date.gte={minYear}-01-01";
        }

        // If average is older, use a wider window (average - 10 years)
        if (avgYear >= 2000)
        {
            var minYear = (int)avgYear - 10;
            return $"&primary_release_date.gte={minYear}-01-01";
        }

        // Very old average year - don't restrict
        return null;
    }

    private static (string ReasonKey, string? RelatedInfo) DetermineReason(
        CandidateFeatures features,
        TmdbDiscoverItem candidate,
        List<string> topGenres)
    {
        if (features.PeopleSimilarity > 0.5 && candidate.KnownPeople is { Count: > 0 })
        {
            return ("reasonPersonNamed", candidate.KnownPeople[0]);
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
