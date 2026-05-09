using System;
using System.Collections.Generic;
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
                var payload = new { mediaType, mediaId = tmdbId, is4k = false, seasons = "all" };
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
                    _pluginLog.LogInfo(
                        "SeerrDiscovery",
                        $"Request submitted: {mediaType} TMDb#{tmdbId}",
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

        // Build certification query parameter based on user's parental rating
        var certParam = ParentalRatingHelper.GetCertificationQueryParam(profile.MaxParentalRating);

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

            // Query A: Top genres (one query per genre for movies + TV)
            if (topGenres.Count >= 1)
            {
                var movieGenreIds = BuildGenreIdList(topGenres.Take(2), TmdbGenreMap.ToMovieTmdbId);
                foreach (var genreId in movieGenreIds)
                {
                    var queryPath = $"api/v1/discover/movies?genre={genreId}&page=1";
                    if (certParam != null)
                    {
                        queryPath += certParam;
                    }

                    var items = await ExecuteDiscoverQueryAsync(
                        client, queryPath, cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(items);
                }

                var tvGenreIds = BuildGenreIdList(topGenres.Take(2), TmdbGenreMap.ToTvTmdbId);
                foreach (var genreId in tvGenreIds)
                {
                    var queryPath = $"api/v1/discover/tv?genre={genreId}&page=1";
                    // Note: certification_lte works primarily for movies on TMDb;
                    // TV uses content_ratings which requires different parameters.
                    // We rely on genre-based filtering for TV series parental control.

                    var items = await ExecuteDiscoverQueryAsync(
                        client, queryPath, cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(items);
                }
            }

            // Query B: Top-1 genre, high-rated movies
            if (topGenres.Count >= 1)
            {
                var topGenreId = TmdbGenreMap.ToMovieTmdbId(topGenres[0]);
                if (topGenreId.HasValue)
                {
                    var minRating = ComputeMinRating(profile);
                    var queryPath = $"api/v1/discover/movies?genre={topGenreId.Value}&voteAverageGte={minRating:F1}&page=1";
                    if (certParam != null)
                    {
                        queryPath += certParam;
                    }

                    var items = await ExecuteDiscoverQueryAsync(
                        client, queryPath, cancellationToken).ConfigureAwait(false);
                    allCandidates.AddRange(items);
                }
            }

            // Deduplicate and filter (now includes parental rating filtering)
            var uniqueCandidates = DeduplicateAndFilter(allCandidates, excludedTmdbIds, profile.MaxParentalRating);

            if (uniqueCandidates.Count == 0)
            {
                _pluginLog.LogDebug(
                    "SeerrDiscovery",
                    $"No viable candidates for user {profile.UserName} after filtering.",
                    _logger);
                return null;
            }

            // Score candidates
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

        // Note: Sonarr series exclusion skipped - ArrSeries does not expose TmdbId.
        // TV series deduplication relies on the Seerr request-level filtering.

        return excluded;
    }

    /// <summary>
    ///     Deduplicates candidates against the exclusion set, removes low-rated items,
    ///     and applies parental rating filtering (adult flag + genre blacklist for children).
    /// </summary>
    /// <param name="candidates">The raw candidate list from all queries.</param>
    /// <param name="excludedTmdbIds">TMDb IDs already in library or requested.</param>
    /// <param name="maxParentalRating">The user's max parental rating (null = unrestricted).</param>
    /// <returns>Filtered and deduplicated candidate list.</returns>
    private static List<TmdbDiscoverItem> DeduplicateAndFilter(
        List<TmdbDiscoverItem> candidates,
        HashSet<int> excludedTmdbIds,
        int? maxParentalRating)
    {
        var seen = new HashSet<int>();
        var result = new List<TmdbDiscoverItem>();

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

            if (candidate.VoteAverage < MinVoteAverage)
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
                ids.Add(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }

    private static double ComputeMinRating(UserWatchProfile profile)
    {
        return Math.Max(6.0, profile.AverageCommunityRating > 0 ? profile.AverageCommunityRating : 6.0);
    }

    private static HashSet<string> BuildPreferredPeopleSet(UserWatchProfile profile)
    {
        _ = profile;
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
