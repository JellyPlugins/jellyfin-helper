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
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Generates personalized content discovery recommendations by querying the configured Overseerr/Jellyseerr instance, scoring candidates against user watch profiles, and persisting results for frontend consumption.
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
    /// </summary>
    private const int MaxPoolPerUser = 20;

    /// <summary>
    ///     Number of top candidates (by pre-score) to enrich with credits data. Credits calls are expensive (1 API call per item), so we only enrich the most promising candidates after an initial genre/rating-based pre-score.
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
    /// </summary>
    private const int ChildAccountMaxParentalRating = 60;

    /// <summary>
    ///     Page size for the requestedBy-scoped request enumeration used by reconciliation.
    /// </summary>
    private const int ReconcilePageSize = 50;

    /// <summary>
    ///     Safety cap on the number of request pages fetched during reconciliation to bound a single user's enumeration.
    /// </summary>
    private const int ReconcileMaxPages = 20;

    /// <summary>TMDb genre ID for Family content (movies and TV).</summary>
    private const int TmdbGenreFamily = 10751;

    /// <summary>TMDb genre ID for Animation (movies).</summary>
    private const int TmdbGenreAnimation = 16;

    /// <summary>TMDb genre ID for Kids TV.</summary>
    private const int TmdbGenreTvKids = 10762;

    private const string LogCategory = "SeerrDiscovery";

    private const string PluginInstanceUnavailableMessage = "Plugin instance is not available; skipping.";

    private const string MediaTypeMovie = "movie";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

    /// <summary>
    ///     Delay between TMDb discovery queries via Seerr to respect rate limits.
    /// </summary>
    private static readonly TimeSpan InterQueryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     TTL for the cached Seerr user list used by ResolveSeerrUserIdAsync. Avoids re-fetching the full paginated user roster on every request submission.
    /// </summary>
    private static readonly TimeSpan SeerrUserCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IArrIntegrationService _arrIntegration;
    private readonly ILibraryManager _libraryManager;
    private readonly EnsembleScoringStrategy _ensemble;
    private readonly DiscoveryCacheService _cache;
    private readonly IDiscoveryFeedbackStore _feedbackStore;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<SeerrDiscoveryService> _logger;

    /// <summary>
    ///     Cached Seerr user list to avoid re-fetching the full paginated roster on every ResolveSeerrUserIdAsync call (e.g., every frontend request).
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
    /// <param name="libraryManager">The Jellyfin library manager, used to exclude titles already in the library.</param>
    /// <param name="ensemble">The ensemble scoring strategy (combines heuristic + learned + neural).</param>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="feedbackStore">The discovery feedback store for training data collection.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public SeerrDiscoveryService(
        IHttpClientFactory httpClientFactory,
        IWatchHistoryService watchHistoryService,
        IArrIntegrationService arrIntegration,
        ILibraryManager libraryManager,
        EnsembleScoringStrategy ensemble,
        DiscoveryCacheService cache,
        IDiscoveryFeedbackStore feedbackStore,
        IPluginLogService pluginLog,
        ILogger<SeerrDiscoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(watchHistoryService);
        ArgumentNullException.ThrowIfNull(arrIntegration);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(ensemble);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(pluginLog);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _watchHistoryService = watchHistoryService;
        _arrIntegration = arrIntegration;
        _libraryManager = libraryManager;
        _ensemble = ensemble;
        _cache = cache;
        _feedbackStore = feedbackStore;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Outcome of inspecting one reconcile request page: the scan is done, must keep paging, or hit an inconsistency that invalidates the whole snapshot.
    /// </summary>
    private enum ReconcilePageDecision
    {
        Continue,
        Complete,
        Incomplete,
    }

    /// <inheritdoc />
    int ISeerrDiscoveryService.MaxVisiblePerUser => MaxVisiblePerUser;

    /// <inheritdoc />
    public async Task GenerateDiscoveryRecommendationsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _pluginLog.LogWarning(LogCategory, PluginInstanceUnavailableMessage, null, _logger);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            _pluginLog.LogInfo(LogCategory, "Seerr not configured. Skipping discovery.", _logger);
            return;
        }

        if (config.RecommendationsTaskMode == TaskMode.Deactivate)
        {
            _pluginLog.LogInfo(LogCategory, "Discovery task is deactivated. Skipping.", _logger);
            return;
        }

        var dryRun = config.RecommendationsTaskMode == TaskMode.DryRun;

        _pluginLog.LogInfo(
            LogCategory,
            dryRun
                ? "Starting discovery generation (Dry Run - will not persist)."
                : $"Starting discovery generation (pool={MaxPoolPerUser}, visible={MaxVisiblePerUser} per user).",
            _logger);

        // Step 1: Load user profiles. Include users who have either played content OR have enough favorites to build genre preferences from.
        var profiles = _watchHistoryService.GetAllUserWatchProfiles();
        var activeProfiles = profiles
            .Where(p => p.WatchedMovieCount + p.WatchedEpisodeCount > 0 || p.FavoriteCount >= 3)
            .ToList();

        if (activeProfiles.Count == 0)
        {
            _pluginLog.LogInfo(LogCategory, "No users with watch history or sufficient favorites found. Skipping.", _logger);
            return;
        }

        // Step 1b: Build exclusion set from the Jellyfin library plus the configured Arr instances
        var excludedTmdbIds = await BuildExclusionSetAsync(config, cancellationToken).ConfigureAwait(false);
        _pluginLog.LogDebug(
            LogCategory,
            $"Built exclusion set with {excludedTmdbIds.Count} TMDb IDs (library + Arr - per-user dismissed/requested merged later).",
            _logger);

        // Per-series total-episode-count map, built ONCE per discovery run and shared across all users.
        var seriesEpisodeCounts = _watchHistoryService.GetSeriesEpisodeCounts();

        // Step 2: Process each user
        var allResults = new List<DiscoveryResult>(activeProfiles.Count);

        foreach (var profile in activeProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var userResult = await TryGenerateForUserAsync(
                profile, config, excludedTmdbIds, seriesEpisodeCounts, cancellationToken).ConfigureAwait(false);

            if (userResult != null)
            {
                allResults.Add(userResult);
            }
        }

        // Step 3: Persist or log
        if (dryRun)
        {
            _pluginLog.LogInfo(
                LogCategory,
                $"[Dry Run] Would persist {allResults.Count} user results with {allResults.Sum(r => r.Recommendations.Count)} total recommendations.",
                _logger);
        }
        else
        {
            PersistResultsAndRecordFeedback(allResults);
        }
    }

    /// <inheritdoc />
    public async Task<int> ReconcileRequestedItemsAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        if (jellyfinUserId == Guid.Empty)
        {
            return 0;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrWhiteSpace(config.SeerrUrl)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return 0;
        }

        var seerrUserId = await ResolveSeerrUserIdAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (seerrUserId is not > 0)
        {
            return 0;
        }

        var requestedKeys = await FetchRequestedKeysForSeerrUserAsync(
            config, seerrUserId.Value, cancellationToken).ConfigureAwait(false);
        if (requestedKeys is null || requestedKeys.Count == 0)
        {
            return 0;
        }

        return await ApplyReconciliationAsync(jellyfinUserId, requestedKeys).ConfigureAwait(false);
    }

    /// <summary>
    ///     Records the intersection of a user's Seerr-requested keys with their cached recommendations as a positive feedback signal and marks each match as requested in the cache. Items already recorded as requested are skipped so repeated reconciliation does not rewrite the feedback file.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user whose cache and feedback are updated.</param>
    /// <param name="requestedKeys">The user's requested (TmdbId, MediaType) keys fetched from Seerr.</param>
    /// <returns>The number of cached recommendations newly reconciled.</returns>
    private async Task<int> ApplyReconciliationAsync(
        Guid jellyfinUserId,
        HashSet<(int TmdbId, string MediaType)> requestedKeys)
    {
        var userResult = _cache.Load().FirstOrDefault(r => r.UserId.Equals(jellyfinUserId));
        if (userResult == null || userResult.Recommendations.Count == 0)
        {
            return 0;
        }

        HashSet<(int TmdbId, string MediaType)> alreadyRecorded;
        try
        {
            alreadyRecorded = new HashSet<(int, string)>(_feedbackStore.GetRequestedItems(jellyfinUserId));
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Reconcile: could not load already-requested items for user {jellyfinUserId}: {ex.Message}",
                _logger);
            alreadyRecorded = [];
        }

        var reconciled = 0;
        foreach (var rec in userResult.Recommendations)
        {
            (int TmdbId, string MediaType) key = (rec.TmdbId, NormalizeReconcileMediaType(rec.MediaType));
            if (rec.TmdbId <= 0 || alreadyRecorded.Contains(key) || !requestedKeys.Contains(key))
            {
                continue;
            }

            if (await ReconcileSingleItemAsync(jellyfinUserId, key.TmdbId, key.MediaType).ConfigureAwait(false))
            {
                reconciled++;
            }
        }

        if (reconciled > 0)
        {
            _pluginLog.LogInfo(
                LogCategory,
                $"Reconciled {reconciled} out-of-band Seerr request(s) into discovery for user {jellyfinUserId}.",
                _logger);
        }

        return reconciled;
    }

    /// <summary>
    ///     Records a single reconciled item as requested and marks it in the cache. Side effects use CancellationToken.None so a disconnected caller cannot leave the feedback and cache stores inconsistent.
    /// </summary>
    /// <param name="jellyfinUserId">The owning Jellyfin user.</param>
    /// <param name="tmdbId">The TMDb ID of the item.</param>
    /// <param name="mediaType">The normalized media type.</param>
    /// <returns><see langword="true"/> when the item was recorded without a fatal failure.</returns>
    private async Task<bool> ReconcileSingleItemAsync(Guid jellyfinUserId, int tmdbId, string mediaType)
    {
        try
        {
            // Record the durable feedback signal before touching the cache. The training signal is
            // the point of reconciliation, and GetRequestedItems reads only the feedback store, so a
            // later cache failure still leaves the item excluded from the view and from regeneration.
            // The reverse order would risk stamping the cache while the durable signal is lost.
            _feedbackStore.RecordRequested(jellyfinUserId, tmdbId, mediaType);
            await _cache.MarkAsRequestedAsync(tmdbId, mediaType, jellyfinUserId, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Reconcile: failed to record requested item {mediaType}#{tmdbId} for user {jellyfinUserId}: {ex.Message}",
                _logger);
            return false;
        }
    }

    /// <summary>
    ///     Fetches the set of (TmdbId, MediaType) keys the given Seerr user has requested, paginating the requestedBy-scoped request endpoint. Returns null on any failure or incomplete fetch so callers treat a partial snapshot as "do nothing" rather than acting on truncated data.
    /// </summary>
    /// <param name="config">The plugin configuration providing the Seerr endpoint and key.</param>
    /// <param name="seerrUserId">The resolved Seerr user ID to scope the query to.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The requested keys, or null when the fetch could not complete.</returns>
    private async Task<HashSet<(int TmdbId, string MediaType)>?> FetchRequestedKeysForSeerrUserAsync(
        PluginConfiguration config,
        int seerrUserId,
        CancellationToken cancellationToken)
    {
        Uri baseUri;
        string apiKey;
        try
        {
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, config.SeerrApiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Reconcile: invalid Seerr configuration: {ex.Message}",
                ex,
                _logger);
            return null;
        }

        var client = GetSeerrClient();

        try
        {
            return await AccumulateRequestedKeysAsync(client, baseUri, apiKey, seerrUserId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Reconcile: failed to fetch requests for Seerr user #{seerrUserId}: {ex.Message}",
                ex,
                _logger);
            return null;
        }
    }

    /// <summary>
    ///     Pages through the requestedBy-scoped request endpoint, accumulating (TmdbId, MediaType) keys until the scan completes. Returns null when a page fetch fails or pagination metadata is inconsistent so the caller treats a partial snapshot as "do nothing".
    /// </summary>
    private async Task<HashSet<(int TmdbId, string MediaType)>?> AccumulateRequestedKeysAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        int seerrUserId,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<(int TmdbId, string MediaType)>();
        var skip = 0;

        for (var page = 0; page < ReconcileMaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageResult = await FetchRequestedPageAsync(
                client, baseUri, apiKey, seerrUserId, skip, cancellationToken).ConfigureAwait(false);
            if (pageResult is null)
            {
                return null;
            }

            var results = pageResult.Results;
            if (results.Count == 0)
            {
                return keys;
            }

            AddRequestedKeys(keys, results);

            skip += ReconcilePageSize;
            var pageDecision = DecideReconcilePagination(results.Count, skip, pageResult.PageInfo.Results, page, seerrUserId);
            if (pageDecision == ReconcilePageDecision.Complete)
            {
                return keys;
            }

            if (pageDecision == ReconcilePageDecision.Incomplete)
            {
                return null;
            }
        }

        return keys;
    }

    /// <summary>
    ///     Adds the normalized (TmdbId, MediaType) key of each row with usable media to the accumulator.
    /// </summary>
    private static void AddRequestedKeys(HashSet<(int TmdbId, string MediaType)> keys, List<SeerrRequest> results)
    {
        foreach (var key in results
            .Select(item => item.Media)
            .Where(media => media != null && media.TmdbId > 0)
            .Select(media => (media!.TmdbId, NormalizeReconcileMediaType(media.MediaType))))
        {
            keys.Add(key);
        }
    }

    /// <summary>
    ///     Fetches and deserializes a single requestedBy-scoped request page. Returns null on any non-success status or unparseable body so the caller treats the whole snapshot as incomplete.
    /// </summary>
    private async Task<SeerrRequestPage?> FetchRequestedPageAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        int seerrUserId,
        int skip,
        CancellationToken cancellationToken)
    {
        var relPath = $"api/v1/request?take={ReconcilePageSize}&skip={skip}&sort=added&filter=all&requestedBy={seerrUserId}";
        using var request = BuildRequest(HttpMethod.Get, baseUri, relPath, apiKey);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Reconcile: request page returned HTTP {(int)response.StatusCode} for Seerr user #{seerrUserId}.",
                _logger);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SeerrRequestPage>(json, JsonOptions);
    }

    /// <summary>
    ///     Decides whether pagination is done, must continue, or is inconsistent. A short page ends the scan. When the reported total is usable, exceeding it is an inconsistency the caller rejects; when the total is missing or zero we keep paging on a full page rather than trusting a bogus total and returning a truncated snapshot.
    /// </summary>
    private ReconcilePageDecision DecideReconcilePagination(int pageCount, int skip, int reportedTotal, int page, int seerrUserId)
    {
        if (pageCount < ReconcilePageSize)
        {
            return ReconcilePageDecision.Complete;
        }

        if (reportedTotal > 0)
        {
            if (skip == reportedTotal)
            {
                return ReconcilePageDecision.Complete;
            }

            if (skip > reportedTotal)
            {
                return ReconcilePageDecision.Incomplete;
            }
        }

        if (page == ReconcileMaxPages - 1)
        {
            // Hit the page cap without exhausting the result set; the snapshot is incomplete.
            _pluginLog.LogWarning(
                LogCategory,
                $"Reconcile: request pagination hit the {ReconcileMaxPages}-page cap for Seerr user #{seerrUserId}; skipping to avoid acting on a partial snapshot.",
                null,
                _logger);
            return ReconcilePageDecision.Incomplete;
        }

        return ReconcilePageDecision.Continue;
    }

    /// <summary>
    ///     Normalizes a media type to the lowercase "movie"/"tv" form used as the composite key across the feedback store and cache. Unknown or empty values collapse to "movie" to match DiscoveryFeedbackStore.
    /// </summary>
    private static string NormalizeReconcileMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return MediaTypeMovie;
        }

        var normalized = mediaType.Trim().ToLowerInvariant();
        return normalized == "tv" ? "tv" : MediaTypeMovie;
    }

    /// <summary>
    ///     Generates discovery recommendations for a single user, converting any non-fatal, non-cancellation failure into a logged warning and a null result so one user's failure does not abort the whole run.
    /// </summary>
    private async Task<DiscoveryResult?> TryGenerateForUserAsync(
        UserWatchProfile profile,
        PluginConfiguration config,
        HashSet<(int TmdbId, string MediaType)> excludedTmdbIds,
        IReadOnlyDictionary<Guid, int> seriesEpisodeCounts,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GenerateForUserAsync(
                profile, config, excludedTmdbIds, seriesEpisodeCounts, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to generate discovery for user {profile.UserName}: {ex.Message}",
                ex,
                _logger);
            return null;
        }
    }

    /// <summary>
    ///     Persists the discovery results and, on success, records the shown items in the
    ///     feedback store for training-data collection (best-effort per user).
    /// </summary>
    private void PersistResultsAndRecordFeedback(List<DiscoveryResult> allResults)
    {
        var persisted = _cache.Save(allResults);
        if (!persisted)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to persist {allResults.Count} user results. Skipping feedback recording to avoid stale training data.",
                null,
                _logger);
            return;
        }

        _pluginLog.LogInfo(
            LogCategory,
            $"Persisted {allResults.Count} user results with {allResults.Sum(r => r.Recommendations.Count)} total recommendations.",
            _logger);

        // Step 4: Record shown items in the feedback store for training data collection. Only record after successful persistence to prevent feedback/training state from referencing recommendations that never actually reached disk.
        foreach (var result in allResults)
        {
            try
            {
                _feedbackStore.RecordShown(result.UserId, result.UserName, result.Recommendations);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogDebug(
                    LogCategory,
                    $"Failed to record feedback for user {result.UserName}: {ex.Message}",
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
        if (mediaType is not (MediaTypeMovie or "tv"))
        {
            return (false, "mediaType must be 'movie' or 'tv'.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _pluginLog.LogWarning(LogCategory, PluginInstanceUnavailableMessage, null, _logger);
            return (false, "Seerr is not configured.");
        }

        if (string.IsNullOrWhiteSpace(config.SeerrUrl)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return (false, "Seerr is not configured.");
        }

        var boundaryError = ValidateSubmitBoundaries(serverId, profileId, rootFolder);
        if (boundaryError != null)
        {
            return (false, boundaryError);
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
                LogCategory,
                $"Invalid Seerr configuration: {ex.Message}",
                ex,
                _logger);
            return (false, "Invalid Seerr configuration.");
        }

        var client = GetSeerrClient();
        var requestParams = new SeerrRequestParams(tmdbId, mediaType, seerrUserId, serverId, profileId, rootFolder);
        return await SendSubmitRequestAsync(client, baseUri, apiKey, requestParams, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Serializes and POSTs the Seerr request payload, then maps the HTTP outcome to a result.
    /// </summary>
    /// <param name="client">The Seerr HTTP client.</param>
    /// <param name="baseUri">The validated Seerr base URI.</param>
    /// <param name="apiKey">The validated Seerr API key.</param>
    /// <param name="p">The cohesive Seerr request field group (TMDb ID, media type, and optional targeting).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A success flag and a client-safe message.</returns>
    private async Task<(bool Success, string Message)> SendSubmitRequestAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        SeerrRequestParams p,
        CancellationToken cancellationToken)
    {
        var tmdbId = p.TmdbId;
        var mediaType = p.MediaType;
        var seerrUserId = p.SeerrUserId;
        try
        {
            var payloadDict = BuildRequestPayload(tmdbId, mediaType, seerrUserId, p.ServerId, p.ProfileId, p.RootFolder);

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
                    LogCategory,
                    $"Request submitted: {mediaType} TMDb#{tmdbId}{userInfo}",
                    _logger);
                return (true, "Request submitted successfully.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _pluginLog.LogWarning(
                LogCategory,
                $"Request failed for TMDb#{tmdbId}: HTTP {(int)response.StatusCode} - {body}",
                null,
                _logger);

            // The full error body is already logged above for admin diagnostics. Only return a generic status code to the client to avoid leaking internal Seerr server details (hostnames, config paths, stack traces).
            return (false, $"Seerr returned HTTP {(int)response.StatusCode}. Check the plugin log for details.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Request timed out for TMDb#{tmdbId}",
                ex,
                _logger);
            return (false, "Request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Request failed for TMDb#{tmdbId}: {ex.Message}",
                ex,
                _logger);
            return (false, "Request failed.");
        }
    }

    /// <summary>
    ///     Validates the numeric and path boundary constraints for a submit request. Returns an error message for a rejected numeric ID, or null when valid.
    /// </summary>
    private static string? ValidateSubmitBoundaries(int? serverId, int? profileId, string? rootFolder)
    {
        // Defensive boundary guard: reject negative IDs at the service boundary. DTO validation covers the controller path, but this method is public and may be called from other contexts (e.g., admin controller, future internal callers).
        if (serverId.HasValue && serverId.Value < 0)
        {
            return "serverId must be 0 or greater.";
        }

        if (profileId.HasValue && profileId.Value < 0)
        {
            return "profileId must be 0 or greater.";
        }

        if (!string.IsNullOrWhiteSpace(rootFolder)
            && (rootFolder.Contains("..", StringComparison.Ordinal)
                || rootFolder.StartsWith('~')
                || rootFolder.Any(c => char.IsControl(c))
                || rootFolder.Length > 512))
        {
            throw new ArgumentException("rootFolder contains invalid path content.", nameof(rootFolder));
        }

        return null;
    }

    /// <summary>
    ///     Builds the Overseerr/Jellyseerr request payload dictionary, including only the optional fields (seasons, userId, serverId, profileId, rootFolder) that apply.
    /// </summary>
    private static Dictionary<string, object> BuildRequestPayload(
        int tmdbId,
        string mediaType,
        int? seerrUserId,
        int? serverId,
        int? profileId,
        string? rootFolder)
    {
        var payloadDict = new Dictionary<string, object>
        {
            ["mediaType"] = mediaType,
            ["mediaId"] = tmdbId,
            ["is4k"] = false
        };

        // For TV requests, include "seasons": "all" to request all available seasons.
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

        return payloadDict;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeerrUser>> GetSeerrUsersAsync(CancellationToken cancellationToken)
    {
        return await GetCachedSeerrUsersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Fetches the paginated Seerr user list and returns both the user roster and a flag indicating whether all pages were fetched successfully.
    /// </summary>
    private async Task<(IReadOnlyList<SeerrUser> Users, bool Complete)> FetchSeerrUsersInternalAsync(
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _pluginLog.LogWarning(LogCategory, PluginInstanceUnavailableMessage, null, _logger);
            return ([], false);
        }

        if (string.IsNullOrWhiteSpace(config.SeerrUrl)
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
                LogCategory,
                $"Invalid Seerr configuration for user fetch: {ex.Message}",
                ex,
                _logger);
            return ([], false);
        }

        var client = GetSeerrClient();
        try
        {
            return await FetchAllUserPagesAsync(client, baseUri, apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to fetch Seerr users: {ex.Message}",
                ex,
                _logger);
            return ([], false);
        }
    }

    /// <summary>
    ///     Fetches every page of the Seerr user roster up to the safety cap. Extracted verbatim from FetchSeerrUsersInternalAsync; the pagination bounds, break conditions, and completeness flagging are unchanged.
    /// </summary>
    /// <param name="client">The Seerr HTTP client.</param>
    /// <param name="baseUri">The validated Seerr base URI.</param>
    /// <param name="apiKey">The validated Seerr API key.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The accumulated users and whether all pages were fetched.</returns>
    private async Task<(IReadOnlyList<SeerrUser> Users, bool Complete)> FetchAllUserPagesAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var allUsers = new List<SeerrUser>();
        var skip = 0;
        const int take = 50;
        const int maxPages = 20; // Safety limit to prevent infinite loops
        var fetchComplete = true;

        for (var page = 0; page < maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (ok, pageResult) = await FetchUserPageAsync(
                client, baseUri, apiKey, take, skip, allUsers.Count, cancellationToken).ConfigureAwait(false);

            if (!ok)
            {
                fetchComplete = false;
                break;
            }

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
                    LogCategory,
                    $"User list pagination hit the {maxPages}-page safety cap ({allUsers.Count} users fetched). Returning partial result.",
                    null,
                    _logger);
                fetchComplete = false;
            }
        }

        return (allUsers, fetchComplete);
    }

    /// <summary>
    ///     Fetches a single page of the Seerr user roster. Returns Ok = false on a non-success HTTP status (logged as a partial-result warning); otherwise returns the deserialized page.
    /// </summary>
    private async Task<(bool Ok, SeerrUserPage? Page)> FetchUserPageAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        int take,
        int skip,
        int fetchedSoFar,
        CancellationToken cancellationToken)
    {
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
                LogCategory,
                $"User list pagination failed at skip={skip}: HTTP {(int)response.StatusCode}. Returning partial result ({fetchedSoFar} users fetched so far).",
                null,
                _logger);
            return (false, null);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var pageResult = JsonSerializer.Deserialize<SeerrUserPage>(json, JsonOptions);
        return (true, pageResult);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeerrServiceInfo>> GetServiceInfoAsync(
        string serviceType,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(serviceType, "radarr", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(serviceType, "sonarr", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported service type '{serviceType}'. Expected 'radarr' or 'sonarr'.", nameof(serviceType));
        }

        var (services, _) = await GetServiceInfoWithStatusAsync(serviceType, cancellationToken).ConfigureAwait(false);
        return services;
    }

    /// <summary>
    ///     Internal variant of GetServiceInfoAsync that also returns a success flag indicating whether the fetch completed without errors.
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
        if (config == null)
        {
            _pluginLog.LogWarning(LogCategory, PluginInstanceUnavailableMessage, null, _logger);
            return ([], true);
        }

        if (string.IsNullOrWhiteSpace(config.SeerrUrl)
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
                LogCategory,
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
                    LogCategory,
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
                await EnrichServerDetailAsync(
                    client, baseUri, apiKey, serviceType, server, cancellationToken).ConfigureAwait(false);
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
                LogCategory,
                $"Failed to fetch Seerr {serviceType} service info: {ex.Message}",
                ex,
                _logger);
            return ([], false);
        }
    }

    /// <summary>
    ///     Fetches the detail payload for a single Seerr service server and merges the profiles, root folders and active selections into .
    /// </summary>
    private async Task EnrichServerDetailAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        string serviceType,
        SeerrServiceInfo server,
        CancellationToken cancellationToken)
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
                    server.Profiles = detail.Profiles ?? server.Profiles;
                    server.RootFolders = detail.RootFolders ?? server.RootFolders;
                    server.ActiveProfileId = detail.ActiveProfileId;
                    server.ActiveDirectory = detail.ActiveDirectory;
                }
            }
            else
            {
                _pluginLog.LogDebug(
                    LogCategory,
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
                LogCategory,
                $"Failed to fetch profiles for {serviceType} server #{server.Id}: {ex.Message}",
                _logger);
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
                // Empty list means either Seerr is unavailable or a partial fetch occurred. Return null - callers on the admin request path (DiscoveryController) treat this as "omit userId" which falls back to the API-key owner.
                return null;
            }

            var match = FindSeerrUserByJellyfinId(seerrUsers, jellyfinUserId);
            if (match != null)
            {
                _pluginLog.LogDebug(
                    LogCategory,
                    $"Resolved Jellyfin user {jellyfinUserId} to Seerr user #{match.Id} ({match.DisplayName}).",
                    _logger);
                return match.Id;
            }

            _pluginLog.LogDebug(
                LogCategory,
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
                LogCategory,
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

        if (mediaType is not (MediaTypeMovie or "tv"))
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
                LogCategory,
                $"Permission check: Jellyfin user {jellyfinUserId} - {deniedReason}",
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
                LogCategory,
                $"Permission check: Seerr user #{seerrUser.Id} ({seerrUser.DisplayName}) lacks request permission for {mediaType}.",
                _logger);

            return new UserRequestPermissionResult
            {
                CanRequest = false,
                DeniedReason = "You do not have permission to submit requests."
            };
        }

        // Step 3: Determine which quality profiles to expose. Distinguish between "no services configured" (empty result from a successful lookup) and "service lookup failed" (transient error).
        var (services, servicesFetchSucceeded) = await GetServiceInfoWithStatusAsync(serviceType, cancellationToken).ConfigureAwait(false);

        if (services.Count == 0)
        {
            if (!servicesFetchSucceeded)
            {
                // Transient failure: allow request with Seerr defaults (no profile selection).
                // Log for admin diagnostics but don't block the user.
                _pluginLog.LogDebug(
                    LogCategory,
                    $"Permission check: Service info lookup failed for {serviceType}. Allowing request with server defaults.",
                    _logger);
            }

            // No services configured or transient failure - user can still request with server defaults
            return new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = []
            };
        }

        // Step 4: Expose quality profiles - all profiles for advanced users, default only for normal users.
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

            // Fast path: if the Seerr ID is already 32 chars (no hyphens), compare directly without allocating a new string.
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
                // Has hyphens - must normalize (allocates, but only for 36-char IDs)
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
    ///     Builds the list of AllowedQualityProfile entries from the service info. When is true, only the server's active (default) profile is included per server - this is the path for normal users without advanced permissions.
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
                AddDefaultProfileForServer(result, server);
            }
            else
            {
                AddAllProfilesForServer(result, server);
            }
        }

        return result;
    }

    /// <summary>
    ///     Adds only the server's active/default quality profile to . If Seerr does not report a resolvable active/default profile, nothing is added and the request path falls back to Seerr's own server defaults.
    /// </summary>
    private static void AddDefaultProfileForServer(List<AllowedQualityProfile> result, SeerrServiceInfo server)
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

        // If Seerr does not report a resolvable active/default profile for this server, do not synthesize one from Profiles[0].
    }

    /// <summary>
    ///     Adds every profile on the server to , emitting one entry per allowed root folder so the controller's exact-match (ServerId, ProfileId, RootFolder) triple validation can accept any valid combination.
    /// </summary>
    private static void AddAllProfilesForServer(List<AllowedQualityProfile> result, SeerrServiceInfo server)
    {
        // Advanced users: all profiles on all servers. Expose each available root folder per profile so the user can select any valid combination.
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
                // No root folders at all - emit with empty RootFolder. SubmitMyRequest will reject any client-specified rootFolder for this profile, and the request falls back to Seerr's server-configured default.
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

    /// <summary>
    ///     Returns the Seerr user list from the in-memory TTL cache, refreshing it from the Seerr API if the cache is expired or empty.
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
        var (freshUsers, complete) = await FetchSeerrUsersInternalAsync(cancellationToken).ConfigureAwait(false);

        // Only cache complete, non-empty results to allow retry on next call when Seerr is temporarily unavailable or returns partial data.
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
                else
                {
                    // Another thread already populated the cache; return its copy
                    return _cachedSeerrUsers;
                }
            }
        }

        // Return empty list for incomplete fetches so callers stay on the retriable "temporarily unavailable" path instead of consuming truncated data that would incorrectly mark users on unfetched pages as "not linked to Seerr".
        if (!complete)
        {
            return [];
        }

        lock (_userCacheLock)
        {
            return _cachedSeerrUsers ?? freshUsers;
        }
    }

    private async Task<DiscoveryResult?> GenerateForUserAsync(
        UserWatchProfile profile,
        PluginConfiguration config,
        HashSet<(int TmdbId, string MediaType)> excludedTmdbIds,
        IReadOnlyDictionary<Guid, int> seriesEpisodeCounts,
        CancellationToken cancellationToken)
    {
        // Fold in any requests the user made outside discovery before building the exclusion set, so the fresh signal both excludes those items from this run and reaches training.
        await ReconcileRequestedItemsAsync(profile.UserId, cancellationToken).ConfigureAwait(false);

        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);
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

        // Pre-build the genre exposure analysis ONCE per user.
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
                LogCategory,
                $"Invalid Seerr configuration for user {profile.UserName}: {ex.Message}",
                ex,
                _logger);
            return null;
        }

        var client = GetSeerrClient();
        var allCandidates = new List<TmdbDiscoverItem>();

        // Correct: /api/v1/discover/movies/genre/{genreId}?page=1 Correct: /api/v1/discover/movies/language/{language}?page=1 WRONG: /api/v1/discover/movies?genre=16&sortBy=...

        // Query A: Top genres (use all top-3 genres for movies + TV)
        if (topGenres.Count > 0)
        {
            if (isChildAccount)
            {
                await GatherChildCandidatesAsync(
                    client, baseUri, apiKey, allCandidates, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await GatherNormalCandidatesAsync(
                    client, baseUri, apiKey, topGenres, primaryLanguage, allCandidates, cancellationToken).ConfigureAwait(false);
            }
        }

        // Add user-specific dismissed and previously requested items to the exclusion set.
        // Best-effort: failures don't break generation.
        var userExcluded = BuildUserExclusionSet(profile, excludedTmdbIds);

        // Deduplicate and filter (includes parental rating + year + quality post-filtering)
        var minVote = isChildAccount ? MinVoteAverageChild : MinVoteAverage;
        var uniqueCandidates = DeduplicateAndFilter(allCandidates, userExcluded, profile.MaxParentalRating, minVote, avgYear, isChildAccount);

        if (uniqueCandidates.Count == 0)
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"No viable candidates for user {profile.UserName} after filtering (parental={profile.MaxParentalRating}).",
                _logger);
            return null;
        }

        _pluginLog.LogDebug(
            LogCategory,
            $"User {profile.UserName}: {allCandidates.Count} raw candidates → {uniqueCandidates.Count} after filtering.",
            _logger);

        // Persisted training-set means so the features we cannot compute for a TMDb candidate are imputed
        // to the distribution the model was trained on (matches DiscoveryFeedbackExampleBuilder). Snapshot
        // once per user; null on a cold model keeps the legacy neutral constants.
        var featureMeans = _ensemble.LearnedStrategy.GetFeatureMeans();

        // Phase 1: PRE-SCORE all candidates (without credits/people data from TMDb) This uses genre similarity, rating, recency, year proximity, and popularity but PeopleSimilarity will be 0 since candidates don't have KnownPeople yet.
        var preScored = new List<(TmdbDiscoverItem Item, double Score)>(uniqueCandidates.Count);
        foreach (var candidate in uniqueCandidates)
        {
            var features = ExternalCandidateFeatureBuilder.Build(
                candidate, genrePreferences, preferredPeople, avgYear, genreExposure, profile, featureMeans);
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
                LogCategory,
                $"User {profile.UserName}: Enriched {enrichedCount}/{enrichmentCandidates.Count} candidates with credits data.",
                _logger);
        }

        // Phase 3: FINAL SCORE the enriched candidates (now with PeopleSimilarity)
        var scored = new List<(TmdbDiscoverItem Item, CandidateFeatures Features, double Score)>(enrichmentCandidates.Count);
        foreach (var candidate in enrichmentCandidates)
        {
            var features = ExternalCandidateFeatureBuilder.Build(
                candidate, genrePreferences, preferredPeople, avgYear, genreExposure, profile, featureMeans);
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
            recommendations.Add(BuildRecommendation(item, features, score, topGenres, preferredPeople));
        }

        return new DiscoveryResult
        {
            UserId = profile.UserId,
            UserName = profile.UserName,
            Recommendations = recommendations,
            GeneratedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Projects a scored, enriched candidate into a <see cref="DiscoveryRecommendation"/>.
    /// </summary>
    private static DiscoveryRecommendation BuildRecommendation(
        TmdbDiscoverItem item,
        CandidateFeatures features,
        double score,
        List<string> topGenres,
        HashSet<string> preferredPeople)
    {
        var genres = TmdbGenreMap.ToJellyfinGenres(item.GenreIds);
        var (reasonKey, relatedInfo) = DetermineReason(features, item, topGenres, preferredPeople);

        return new DiscoveryRecommendation
        {
            TmdbId = item.Id,
            MediaType = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? "tv"
                : MediaTypeMovie,
            Title = item.DisplayTitle,
            Year = item.EffectiveReleaseDate?.Year,
            Score = score,
            ReasonKey = reasonKey,
            Reason = relatedInfo != null ? $"{reasonKey}: {relatedInfo}" : reasonKey,
            RelatedInfo = relatedInfo,
            Genres = genres,
            TmdbRating = item.VoteAverage,
            // Raw TMDb popularity carried through so RecordShown can persist it for training.
            Popularity = item.Popularity,
            PosterPath = item.PosterPath,
            Overview = item.Overview,
            AlreadyRequested = false,
            KnownPeople = item.KnownPeople
        };
    }

    /// <summary>
    ///     Runs the child-account discovery queries (Family/Animation/Kids genres for movies and TV) and appends the results to .
    /// </summary>
    private async Task GatherChildCandidatesAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        List<TmdbDiscoverItem> allCandidates,
        CancellationToken cancellationToken)
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

    /// <summary>
    ///     Runs the standard discovery queries (top-3 preferred genres, page-2 variety, and optional language-based discovery) and appends the results to .
    /// </summary>
    private async Task GatherNormalCandidatesAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        List<string> topGenres,
        string? primaryLanguage,
        List<TmdbDiscoverItem> allCandidates,
        CancellationToken cancellationToken)
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

    /// <summary>
    ///     Builds the per-user exclusion set by merging the shared library exclusions with the user's dismissed and previously requested items (best-effort; failures are logged).
    /// </summary>
    private HashSet<(int TmdbId, string MediaType)> BuildUserExclusionSet(
        UserWatchProfile profile,
        HashSet<(int TmdbId, string MediaType)> excludedTmdbIds)
    {
        var userExcluded = new HashSet<(int TmdbId, string MediaType)>(excludedTmdbIds);
        try
        {
            var dismissed = _feedbackStore.GetDismissedItems(profile.UserId);
            var requested = _feedbackStore.GetRequestedItems(profile.UserId);
            if (dismissed.Count > 0 || requested.Count > 0)
            {
                userExcluded.UnionWith(dismissed);
                userExcluded.UnionWith(requested);
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Could not load dismissed/requested items for user {profile.UserName}: {ex.Message}",
                _logger);
        }

        return userExcluded;
    }

    private async Task<List<TmdbDiscoverItem>> ExecuteDiscoverQueryAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        string queryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var delayAfter = false;
        try
        {
            using var request = BuildRequest(HttpMethod.Get, baseUri, queryPath, apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _pluginLog.LogDebug(
                    LogCategory,
                    $"Query returned HTTP {(int)response.StatusCode}: {queryPath}",
                    _logger);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<TmdbDiscoverResponse>(json, JsonOptions);
            delayAfter = true;
            return page?.Results ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _pluginLog.LogWarning(LogCategory, $"Query timed out: {queryPath}", ex, _logger);
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            _pluginLog.LogWarning(LogCategory, $"Query failed: {queryPath} - {ex.Message}", ex, _logger);
            return [];
        }
        finally
        {
            if (delayAfter && !cancellationToken.IsCancellationRequested)
            {
                await ApplyInterQueryDelayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Applies the inter-query rate-limit delay, swallowing the benign cancellation
    ///     of the delay itself.
    /// </summary>
    private static async Task ApplyInterQueryDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InterQueryDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // intentionally empty: cancellation of the inter-query delay is expected and benign.
        }
    }

    private async Task<HashSet<(int TmdbId, string MediaType)>> BuildExclusionSetAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<(int TmdbId, string MediaType)>();

        // Exclude titles already in the Jellyfin library. This covers items that no configured Arr
        // instance tracks (manually added, imported, or when Arr is not configured at all).
        AddLibraryExclusions(excluded);

        // Exclude movies already in Radarr
        await AddRadarrExclusionsAsync(config, excluded, cancellationToken).ConfigureAwait(false);

        // Exclude TV series already in Sonarr (Sonarr v4+ provides tmdbId; v3 entries are skipped via TmdbId > 0 guard)
        await AddSonarrExclusionsAsync(config, excluded, cancellationToken).ConfigureAwait(false);

        return excluded;
    }

    /// <summary>
    ///     Adds the TMDb IDs of movies and series already present in the Jellyfin library to the exclusion set.
    /// </summary>
    /// <param name="excluded">The exclusion set to add library entries into.</param>
    private void AddLibraryExclusions(HashSet<(int TmdbId, string MediaType)> excluded)
    {
        var libraryItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
        });

        foreach (var key in TmdbLibraryMapper.BuildTmdbKeySet(libraryItems))
        {
            excluded.Add(key);
        }
    }

    /// <summary>
    ///     Adds TMDb IDs of movies already present in the configured Radarr instances to the exclusion set.
    /// </summary>
    /// <param name="config">The plugin configuration providing Radarr instances.</param>
    /// <param name="excluded">The exclusion set to add movie entries into.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task AddRadarrExclusionsAsync(
        PluginConfiguration config,
        HashSet<(int TmdbId, string MediaType)> excluded,
        CancellationToken cancellationToken)
    {
        await AddArrExclusionsAsync(
            config.GetEffectiveRadarrInstances(),
            _arrIntegration.GetRadarrMoviesAsync,
            static m => m.TmdbId,
            MediaTypeMovie,
            "Radarr",
            excluded,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds TMDb IDs of series already present in the configured Sonarr instances to the exclusion set.
    /// </summary>
    /// <param name="config">The plugin configuration providing Sonarr instances.</param>
    /// <param name="excluded">The exclusion set to add series entries into.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task AddSonarrExclusionsAsync(
        PluginConfiguration config,
        HashSet<(int TmdbId, string MediaType)> excluded,
        CancellationToken cancellationToken)
    {
        await AddArrExclusionsAsync(
            config.GetEffectiveSonarrInstances(),
            _arrIntegration.GetSonarrSeriesAsync,
            static s => s.TmdbId,
            "tv",
            "Sonarr",
            excluded,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Shared exclusion-set builder for Radarr and Sonarr. Iterates the given Arr instances, fetches each instance's items, and adds the TMDb IDs (filtered by TmdbId &gt; 0) under the supplied media type.
    /// </summary>
    /// <typeparam name="T">The Arr item type (movie or series).</typeparam>
    /// <param name="instances">The effective Arr instances to query.</param>
    /// <param name="fetch">Delegate fetching the items for an instance (URL, API key, token).</param>
    /// <param name="tmdbSelector">Selects the TMDb ID from an item.</param>
    /// <param name="mediaType">The media type recorded in the exclusion set.</param>
    /// <param name="arrName">The Arr display name used in log messages (Radarr/Sonarr).</param>
    /// <param name="excluded">The exclusion set to add entries into.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task AddArrExclusionsAsync<T>(
        IEnumerable<ArrInstanceConfig> instances,
        Func<string, string, CancellationToken, Task<List<T>?>> fetch,
        Func<T, int> tmdbSelector,
        string mediaType,
        string arrName,
        HashSet<(int TmdbId, string MediaType)> excluded,
        CancellationToken cancellationToken)
    {
        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var items = await fetch(
                    instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var tmdbId = tmdbSelector(item);
                        if (tmdbId > 0)
                        {
                            excluded.Add((tmdbId, mediaType));
                        }
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
                    LogCategory,
                    $"Failed to fetch {arrName} exclusion data from {instance.Url}: {ex.Message}. Continuing with remaining instances.",
                    ex,
                    _logger);
            }
        }
    }

    /// <summary>
    ///     Deduplicates candidates against the exclusion set, removes low-rated items, applies parental rating filtering, and optionally filters by year relevance.
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
        // occupy separate ID spaces - the same integer can refer to both a movie and a TV show.
        var seen = new HashSet<(int Id, string MediaType)>();
        var result = new List<TmdbDiscoverItem>();

        // For year-based post-filtering: compute acceptable year range
        var minYear = ComputeMinYear(avgYear, isChildAccount);

        foreach (var candidate in candidates)
        {
            if (candidate.Id <= 0)
            {
                continue;
            }

            var mediaTypeKey = (candidate.MediaType ?? MediaTypeMovie).ToLowerInvariant();
            if (excludedTmdbIds.Contains((candidate.Id, mediaTypeKey)))
            {
                continue;
            }

            // Seerr already knows this title is (partially) available in the library, so recommending
            // it would just surface something the user can already watch.
            if (candidate.IsAlreadyAvailable)
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

    /// <summary>
    ///     Computes the minimum acceptable release year for year-based post-filtering, or 0 when no year filtering applies (child accounts or no average year signal).
    /// </summary>
    private static int ComputeMinYear(double avgYear, bool isChildAccount)
    {
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

        return minYear;
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
    ///     Defensively stamps the MediaType on items fetched from typed discover endpoints. Seerr typed endpoints (e.g.
    /// </summary>
    private static void StampMediaType(List<TmdbDiscoverItem> items, string mediaType)
    {
        foreach (var item in items)
        {
            item.MediaType = mediaType;
        }
    }

    /// <summary>
    ///     Gets the primary language code for Seerr language-based discovery endpoints. Returns a 2-letter ISO 639-1 code (e.g.
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
            // Validate ISO 639-1 format here - the canonical place that owns the "primary language"
            // decision - so downstream URL-building receives a pre-validated code.
            return lang.Length == 2 && char.IsAsciiLetter(lang[0]) && char.IsAsciiLetter(lang[1]) ? lang : null;
        }

        return null;
    }

    /// <summary>
    ///     Builds the set of preferred people (actors/directors) from the user's watch history.
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
    ///     Enriches the top candidates with credits (cast/director) data from Seerr. Fetches /api/v1/movie/{id} or /api/v1/tv/{id} for each candidate and populates the KnownPeople list with top-billed actors and directors.
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
                    await EnrichCandidateWithCreditsAsync(
                        client, baseUri, apiKey, candidate, cancellationToken).ConfigureAwait(false);
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
    ///     Fetches credits for a single candidate and populates its KnownPeople list, bounded by CreditsEnrichmentTimeoutMs.
    /// </summary>
    private async Task EnrichCandidateWithCreditsAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        TmdbDiscoverItem candidate,
        CancellationToken cancellationToken)
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

            var people = ExtractTopPeople(detail.Credits);
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
                LogCategory,
                $"Credits enrichment failed for {candidate.MediaType}#{candidate.Id}: {ex.Message}",
                _logger);
        }
    }

    /// <summary>
    ///     Extracts up to <see cref="MaxCastPerCandidate"/> top-billed people (directors first,
    ///     then top-ordered cast) from the given credits payload.
    /// </summary>
    private static List<string> ExtractTopPeople(SeerrCredits credits)
    {
        var people = new List<string>(MaxCastPerCandidate);

        if (credits.Crew is { Count: > 0 })
        {
            foreach (var crew in credits.Crew.Where(
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

        if (credits.Cast is { Count: > 0 })
        {
            var actorsToTake = MaxCastPerCandidate - people.Count;
            if (actorsToTake > 0)
            {
                var topActors = credits.Cast
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .OrderBy(c => c.Order)
                    .Take(actorsToTake)
                    .Select(c => c.Name);
                people.AddRange(topActors);
            }
        }

        return people;
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
    ///     Validates the Seerr base URL and API key and returns a pre-computed base Uri and the sanitised key.
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
    ///     Returns a non-owning HttpClient from the factory. The client must NOT be disposed - its lifetime is managed by IHttpClientFactory.
    /// </summary>
    private HttpClient GetSeerrClient() =>
        _httpClientFactory.CreateClient("SeerrDiscovery");

    /// <summary>
    ///     Builds an HttpRequestMessage with per-request authentication headers.
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
    ///     Throws ArgumentException if contains CR or LF. HttpRequestHeaders.TryAddWithoutValidation tolerates non-ASCII keys but does not strip CRLF sequences, which would allow HTTP header injection.
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

    /// <summary>
    ///     Cohesive group of Seerr request fields passed together through the submit pipeline.
    /// </summary>
    /// <param name="TmdbId">The TMDb ID being requested.</param>
    /// <param name="MediaType">The normalized media type ('movie' or 'tv').</param>
    /// <param name="SeerrUserId">Optional Seerr user id to request as.</param>
    /// <param name="ServerId">Optional target server id.</param>
    /// <param name="ProfileId">Optional quality profile id.</param>
    /// <param name="RootFolder">Optional root folder path.</param>
    private readonly record struct SeerrRequestParams(
        int TmdbId,
        string MediaType,
        int? SeerrUserId,
        int? ServerId,
        int? ProfileId,
        string? RootFolder);
}
