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
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Globalization;
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
    ///     Maximum degree of parallelism for metadata enrichment fetches.
    ///     Same budget as credits enrichment: bounded Seerr detail calls per candidate.
    /// </summary>
    private const int MetadataEnrichmentParallelism = 3;

    /// <summary>
    ///     Per-request timeout for metadata enrichment calls, in milliseconds.
    /// </summary>
    private const int MetadataEnrichmentTimeoutMs = 8_000;

    /// <summary>One separate deadline for mandatory certification metadata; partial results stay fail-closed.</summary>
    private const int RequiredMetadataBudgetMs = 30_000;

    /// <summary>
    ///     Maximum Jellyfin parental rating value that triggers the child-account discovery path.
    /// </summary>
    private const int ChildAccountMaxParentalRating = 6;

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

    /// <summary>Minimum people-similarity before a Discovery suggestion is attributed to a matched person.</summary>
    private const double ReasonPeopleThreshold = 0.3;

    /// <summary>Minimum genre-similarity before a Discovery suggestion is attributed to a genre.</summary>
    private const double ReasonGenreThreshold = 0.7;

    /// <summary>Minimum recency before a Discovery suggestion can be called "trending" (paired with the critic threshold).</summary>
    private const double ReasonRecencyThreshold = 0.8;

    /// <summary>Minimum popularity before a Discovery suggestion is attributed to crowd popularity.</summary>
    private const double ReasonPopularityThreshold = 0.7;

    /// <summary>Minimum combined critic score before a Discovery suggestion is attributed to critical acclaim.</summary>
    private const double ReasonCriticThreshold = 0.7;

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

    /// <summary>
    ///     TTL for the cached shared exclusion set (library + Arr titles). Scoring runs per user and per
    ///     view, but the underlying library and Arr rosters barely move within minutes.
    /// </summary>
    private static readonly TimeSpan ExclusionCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IArrIntegrationService _arrIntegration;
    private readonly ILibraryManager _libraryManager;
    private readonly VirtualItemDetector _virtualItemDetector;
    private readonly IPerUserEnsembleRegistry _perUserRegistry;
    private readonly DiscoveryCacheService _cache;
    private readonly IDiscoveryFeedbackStore _feedbackStore;
    private readonly IPluginLogService _pluginLog;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<SeerrDiscoveryService> _logger;
    private readonly ILocalizationManager? _localization;
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly ILinkedChildrenService? _linkedChildren;

    /// <summary>
    ///     Cached Seerr user list to avoid re-fetching the full paginated roster on every ResolveSeerrUserIdAsync call (e.g., every frontend request).
    /// </summary>
    private readonly Lock _userCacheLock = new();
    private readonly Lock _exclusionCacheLock = new();
    private readonly Lock _metadataCacheLock = new();
    private readonly Dictionary<string, CachedMediaDetail> _metadataCache = new(StringComparer.Ordinal);
    private IReadOnlyList<SeerrUser>? _cachedSeerrUsers;
    private DateTime _cachedSeerrUsersExpiry = DateTime.MinValue;

    // Holds the in-flight roster fetch so concurrent callers coalesce onto a single Seerr request
    // instead of each launching their own. Cleared when the fetch completes so the next miss refreshes.
    private Task<(IReadOnlyList<SeerrUser> Users, bool Complete)>? _inflightUserFetch;

    private HashSet<(int TmdbId, string MediaType)>? _cachedExclusions;
    private DateTime _exclusionsExpiry = DateTime.MinValue;

    // Holds the in-flight exclusion build so concurrent cache misses coalesce onto a single library
    // scan plus Arr roster download instead of each launching their own. Cleared when the build
    // settles so the next miss refreshes.
    private Task<HashSet<(int TmdbId, string MediaType)>>? _inflightExclusionBuild;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SeerrDiscoveryService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="arrIntegration">The Arr integration service.</param>
    /// <param name="libraryManager">The Jellyfin library manager, used to exclude titles already in the library.</param>
    /// <param name="perUserRegistry">The per-user ensemble registry, so discovery scores each user's candidates with that user's own blend, matching how recommendations are scored.</param>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="feedbackStore">The discovery feedback store for training data collection.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="secretProtector">Decrypts the stored Seerr API key before it is used for outbound calls.</param>
    /// <param name="virtualItemDetector">Excludes virtual stub library entries from ownership checks.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="localization">Jellyfin's rating mapper; missing certification data fails closed for restricted accounts.</param>
    /// <param name="serverConfiguration">Supplies the same metadata country Jellyfin uses for parental ratings.</param>
    /// <param name="linkedChildren">Supplies actual BoxSet memberships rather than inferring them from collection names.</param>
    public SeerrDiscoveryService(
        IHttpClientFactory httpClientFactory,
        IWatchHistoryService watchHistoryService,
        IArrIntegrationService arrIntegration,
        ILibraryManager libraryManager,
        IPerUserEnsembleRegistry perUserRegistry,
        DiscoveryCacheService cache,
        IDiscoveryFeedbackStore feedbackStore,
        IPluginLogService pluginLog,
        ISecretProtector secretProtector,
        VirtualItemDetector virtualItemDetector,
        ILogger<SeerrDiscoveryService> logger,
        ILocalizationManager? localization = null,
        IServerConfigurationManager? serverConfiguration = null,
        ILinkedChildrenService? linkedChildren = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(watchHistoryService);
        ArgumentNullException.ThrowIfNull(arrIntegration);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(perUserRegistry);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(pluginLog);
        ArgumentNullException.ThrowIfNull(secretProtector);
        ArgumentNullException.ThrowIfNull(virtualItemDetector);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _watchHistoryService = watchHistoryService;
        _arrIntegration = arrIntegration;
        _libraryManager = libraryManager;
        _perUserRegistry = perUserRegistry;
        _cache = cache;
        _feedbackStore = feedbackStore;
        _pluginLog = pluginLog;
        _secretProtector = secretProtector;
        _virtualItemDetector = virtualItemDetector;
        _logger = logger;
        _localization = localization;
        _serverConfiguration = serverConfiguration;
        _linkedChildren = linkedChildren;
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

    /// <summary>
    ///     Distinguishes why a per-user discovery generation produced no fresh result. A legitimately empty
    ///     result (no genre preferences, nothing survives filtering) must clear that user's cached pool, but a
    ///     transient failure (a swallowed non-fatal exception) must NOT: overwriting the cache with nothing on a
    ///     transient hiccup silently empties the user's Discovery sidebar until the next successful run.
    /// </summary>
    private enum UserGenerationStatus
    {
        Generated,
        EmptyByDesign,
        TransientFailure,
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

        // The live provider excludes deleted/disabled accounts. Enabled users without history
        // still need a metadata-only cold-start pool, not fabricated personal preferences.
        var activeProfiles = _watchHistoryService.GetAllUserWatchProfiles().ToList();

        if (activeProfiles.Count == 0)
        {
            _pluginLog.LogInfo(LogCategory, "No enabled user profiles found. Skipping.", _logger);
            return;
        }

        // Build exclusion set from the Jellyfin library plus the configured Arr instances
        var excludedTmdbIds = await GetCachedExclusionSetAsync(config, cancellationToken).ConfigureAwait(false);
        _pluginLog.LogDebug(
            LogCategory,
            $"Built exclusion set with {excludedTmdbIds.Count} TMDb IDs (library + Arr - per-user dismissed/requested merged later).",
            _logger);

        // Per-series total-episode-count map, built ONCE per discovery run and shared across all users.
        var seriesEpisodeCounts = _watchHistoryService.GetSeriesEpisodeCounts();

        // Process each user
        var allResults = new List<DiscoveryResult>(activeProfiles.Count);

        // Snapshot the previously-persisted pools once so a transient per-user failure can carry the user's
        // last-known-good pool forward instead of being wiped by the full-overwrite Save below. Keyed by user.
        var previousByUser = (await _cache.LoadAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.UserId);

        // Users whose pool in allResults is a carried-forward snapshot, not freshly generated this run. Their
        // items were already shown in a prior run, so they must be excluded from feedback recording to avoid
        // double-counting training signal.
        var carriedForwardUsers = new HashSet<Guid>();

        foreach (var profile in activeProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await TryGenerateForUserAsync(
                profile, config, excludedTmdbIds, seriesEpisodeCounts, cancellationToken).ConfigureAwait(false);

            switch (outcome.Status)
            {
                case UserGenerationStatus.Generated:
                    allResults.Add(outcome.Result!);
                    break;

                case UserGenerationStatus.TransientFailure
                    when previousByUser.TryGetValue(profile.UserId, out var priorPool):
                    // Preserve the user's last-known-good pool; a transient hiccup must not empty their sidebar.
                    allResults.Add(priorPool);
                    carriedForwardUsers.Add(profile.UserId);
                    _pluginLog.LogDebug(
                        LogCategory,
                        $"Retained previous pool for user {profile.UserName} after a transient generation failure ({priorPool.Recommendations.Count} recommendations).",
                        _logger);
                    break;

                case UserGenerationStatus.TransientFailure:
                case UserGenerationStatus.EmptyByDesign:
                default:
                    // TransientFailure with no prior pool: nothing to lose. EmptyByDesign: correct to clear.
                    break;
            }
        }

        // Persist or log
        if (dryRun)
        {
            _pluginLog.LogInfo(
                LogCategory,
                $"[Dry Run] Would persist {allResults.Count} user results with {allResults.Sum(r => r.Recommendations.Count)} total recommendations.",
                _logger);
        }
        else
        {
            PersistResultsAndRecordFeedback(allResults, carriedForwardUsers);
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
        var userResult = (await _cache.LoadAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(r => r.UserId.Equals(jellyfinUserId));
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
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, _secretProtector.Unprotect(config.SeerrApiKey));
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

        var client = GetSeerrClient(config.SeerrSkipCertificateValidation);

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
    ///     Generates discovery recommendations for a single user, converting any non-fatal, non-cancellation failure into a logged warning and a <see cref="UserGenerationStatus.TransientFailure"/> outcome so one user's failure does not abort the whole run nor discard their last-known-good cached pool. A legitimately empty generation maps to <see cref="UserGenerationStatus.EmptyByDesign"/>.
    /// </summary>
    private async Task<UserGenerationOutcome> TryGenerateForUserAsync(
        UserWatchProfile profile,
        PluginConfiguration config,
        HashSet<(int TmdbId, string MediaType)> excludedTmdbIds,
        IReadOnlyDictionary<Guid, int> seriesEpisodeCounts,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await GenerateForUserAsync(
                profile, config, excludedTmdbIds, seriesEpisodeCounts, cancellationToken).ConfigureAwait(false);

            return result != null
                ? new UserGenerationOutcome(UserGenerationStatus.Generated, result)
                : new UserGenerationOutcome(UserGenerationStatus.EmptyByDesign, null);
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
            return new UserGenerationOutcome(UserGenerationStatus.TransientFailure, null);
        }
    }

    /// <summary>
    ///     Persists the discovery results and, on success, records the freshly-generated shown items in the
    ///     feedback store for training-data collection (best-effort per user). Carried-forward pools (retained
    ///     from a prior run after a transient failure) are persisted but excluded from feedback recording so
    ///     their items are not counted as shown twice.
    /// </summary>
    /// <param name="allResults">All pools to persist: freshly generated plus any carried-forward.</param>
    /// <param name="carriedForwardUsers">Users whose pool is a carried-forward snapshot, not fresh this run.</param>
    private void PersistResultsAndRecordFeedback(
        List<DiscoveryResult> allResults,
        HashSet<Guid> carriedForwardUsers)
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

        // Record shown items in the feedback store for training data collection. Only record after successful persistence to prevent feedback/training state from referencing recommendations that never actually reached disk. Skip carried-forward pools: they were already recorded when first generated.
        foreach (var result in allResults)
        {
            if (carriedForwardUsers.Contains(result.UserId))
            {
                continue;
            }

            try
            {
                // Keep the established store contract; RecordShown is now explicitly a generation alias,
                // never evidence of a viewport impression. The displayed response supplies the exact generation time.
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
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, _secretProtector.Unprotect(config.SeerrApiKey));
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

        var client = GetSeerrClient(config.SeerrSkipCertificateValidation);
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
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, _secretProtector.Unprotect(config.SeerrApiKey));
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

        var client = GetSeerrClient(config.SeerrSkipCertificateValidation);
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
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, _secretProtector.Unprotect(config.SeerrApiKey));
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

        var client = GetSeerrClient(config.SeerrSkipCertificateValidation);
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

        // Resolve the Jellyfin user to their Seerr account
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

        // Check if the user has request permission for this media type
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

        // Determine which quality profiles to expose. Distinguish between "no services configured" (empty result from a successful lookup) and "service lookup failed" (transient error).
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

        // Expose quality profiles: all profiles for advanced users, default only for normal users.
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
        // Honor an already-cancelled token before any work. The shared fetch below runs on its own
        // token so one caller cannot abort it for the others, which means the caller's cancellation
        // would otherwise go unobserved on a cache hit or a synchronously completed fetch.
        cancellationToken.ThrowIfCancellationRequested();

        // Fast path: check if cache is still valid under lock to ensure atomicity
        // of the (_cachedSeerrUsers, _cachedSeerrUsersExpiry) pair across threads.
        lock (_userCacheLock)
        {
            if (_cachedSeerrUsers != null && DateTime.UtcNow < _cachedSeerrUsersExpiry)
            {
                return _cachedSeerrUsers;
            }
        }

        // Slow path: coalesce concurrent callers onto one fetch so a discovery run that fans out over
        // many users, or a burst of frontend requests, does not stampede Seerr with N parallel roster
        // reads. The shared fetch runs on its own token so one caller cancelling does not abort it for
        // the others; each caller still observes its own cancellation while awaiting.
        Task<(IReadOnlyList<SeerrUser> Users, bool Complete)> fetch;
        lock (_userCacheLock)
        {
            if (_cachedSeerrUsers != null && DateTime.UtcNow < _cachedSeerrUsersExpiry)
            {
                return _cachedSeerrUsers;
            }

            // Reuse an in-flight fetch, but never a settled one: a completed task holds a stale result
            // (including a failed fetch that must not be cached), so a fresh miss starts a new fetch
            // rather than replaying the old outcome. This does not depend on the completion cleanup
            // having run yet, which would otherwise race a caller arriving right after the fetch settles.
            if (_inflightUserFetch is not { IsCompleted: false })
            {
                _inflightUserFetch = StartSeerrUserFetchAsync();
            }

            fetch = _inflightUserFetch;
        }

        (IReadOnlyList<SeerrUser> freshUsers, bool complete) = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // The shared fetch has already written a successful, complete result to the cache before completing
        // (see StartSeerrUserFetchAsync), so once it settles the cache is warm and the fast path above catches
        // any later caller in this burst. Read the cached copy here so every caller returns the same instance.
        if (freshUsers.Count > 0 && complete)
        {
            lock (_userCacheLock)
            {
                if (_cachedSeerrUsers != null && DateTime.UtcNow < _cachedSeerrUsersExpiry)
                {
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

    // Starts a single shared roster fetch, publishes a successful complete result to the cache before the
    // task completes, and clears the in-flight slot once it settles. Publishing inside the task closes the
    // window where a caller arriving after the fetch settled but before the cache was written would start a
    // second fetch: by the time the task is observably complete the cache is already warm. An incomplete or
    // failed fetch writes nothing, so it stays on the retriable path and is never replayed from a settled task.
    // Runs on its own token so it is not cancelled by any one caller's token.
    private Task<(IReadOnlyList<SeerrUser> Users, bool Complete)> StartSeerrUserFetchAsync()
    {
        Task<(IReadOnlyList<SeerrUser> Users, bool Complete)> fetch = FetchAndCacheSeerrUsersAsync();
        _ = fetch.ContinueWith(
            _ =>
            {
                lock (_userCacheLock)
                {
                    // Clear only our own slot. A caller arriving after this fetch settles but before this
                    // continuation runs may have already installed a replacement fetch; wiping it would
                    // strand that fresh request and force the next caller to start a third one.
                    if (ReferenceEquals(_inflightUserFetch, fetch))
                    {
                        _inflightUserFetch = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return fetch;
    }

    // Fetches the roster and, on a successful complete read, writes it to the cache before returning, so the
    // cache is warm the instant the shared task is observably complete.
    private async Task<(IReadOnlyList<SeerrUser> Users, bool Complete)> FetchAndCacheSeerrUsersAsync()
    {
        (IReadOnlyList<SeerrUser> users, bool complete) = await FetchSeerrUsersInternalAsync(CancellationToken.None).ConfigureAwait(false);

        // Only cache complete, non-empty results to allow retry on the next call when Seerr is temporarily
        // unavailable or returns partial data.
        if (users.Count > 0 && complete)
        {
            lock (_userCacheLock)
            {
                if (_cachedSeerrUsers == null || DateTime.UtcNow >= _cachedSeerrUsersExpiry)
                {
                    _cachedSeerrUsers = users;
                    _cachedSeerrUsersExpiry = DateTime.UtcNow.Add(SeerrUserCacheTtl);
                }
            }
        }

        return (users, complete);
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

        var genreCatalogVersion = GenreNormalizer.CatalogVersion;
        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);

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

        // An invalid Seerr configuration is not "no recommendations". We could not even try. Let it
        // propagate so TryGenerateForUserAsync classifies it as a transient failure and preserves the
        // user's last-known-good pool rather than clearing it. (In practice the global config check at the
        // top of GenerateDiscoveryRecommendationsAsync already guards the common blank-config case.)
        var (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, _secretProtector.Unprotect(config.SeerrApiKey));

        var client = GetSeerrClient(config.SeerrSkipCertificateValidation);
        var allCandidates = new List<TmdbDiscoverItem>();

        // Correct: /api/v1/discover/movies/genre/{genreId}?page=1 Correct: /api/v1/discover/movies/language/{language}?page=1 WRONG: /api/v1/discover/movies?genre=16&sortBy=...

        // Children retain the age-appropriate retrieval routes; certifications remain authoritative.
        if (isChildAccount)
        {
            await GatherChildCandidatesAsync(client, baseUri, apiKey, allCandidates, cancellationToken, requireSuccess: topGenres.Count == 0).ConfigureAwait(false);
        }
        else if (topGenres.Count > 0)
        {
            await GatherNormalCandidatesAsync(client, baseUri, apiKey, topGenres, primaryLanguage, allCandidates, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await GatherColdStartCandidatesAsync(client, baseUri, apiKey, allCandidates, cancellationToken).ConfigureAwait(false);
        }

        // Add user-specific dismissed and previously requested items to the exclusion set.
        // Best-effort: failures don't break generation.
        var userExcluded = BuildUserExclusionSet(profile, excludedTmdbIds);

        // Deduplicate and filter (includes parental rating + year + quality post-filtering)
        var minVote = isChildAccount ? MinVoteAverageChild : MinVoteAverage;
        var uniqueCandidates = DeduplicateAndFilter(allCandidates, userExcluded, maxParentalRating: null, minVote, avgYear, isChildAccount);
        await EnrichAndFilterRestrictedCandidatesAsync(client, baseUri, apiKey, uniqueCandidates, profile.MaxParentalRating, cancellationToken).ConfigureAwait(false);

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

        var recommendations = await ScoreCandidatesForUserAsync(
            new CandidateScoringContext
            {
                Profile = profile,
                Candidates = uniqueCandidates,
                TopGenres = topGenres,
                GenrePreferences = genrePreferences,
                GenreCatalogVersion = genreCatalogVersion,
                PreferredPeople = preferredPeople,
                AvgYear = avgYear,
                GenreExposure = genreExposure,
                SeriesEpisodeCounts = seriesEpisodeCounts,
                Client = client,
                BaseUri = baseUri,
                ApiKey = apiKey,
            },
            cancellationToken).ConfigureAwait(false);

        return new DiscoveryResult
        {
            UserId = profile.UserId,
            UserName = profile.UserName,
            Recommendations = recommendations,
            GeneratedAt = DateTime.UtcNow
        };
    }

    private async Task EnrichAndFilterRestrictedCandidatesAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        List<TmdbDiscoverItem> candidates,
        int? maximumRating,
        CancellationToken cancellationToken)
    {
        if (!maximumRating.HasValue || maximumRating.Value >= ParentalRatingHelper.UnrestrictedThreshold)
        {
            return;
        }

        if (_localization is null)
        {
            throw new InvalidOperationException("Jellyfin certification mapping is unavailable.");
        }

        var fetched = await EnrichRequiredMetadataAsync(client, baseUri, apiKey, candidates, cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 0 && fetched.EnrichedKeys.Count == 0)
        {
            throw new HttpRequestException("No provider certification metadata could be fetched.");
        }

        candidates.RemoveAll(candidate => candidate.IsAlreadyAvailable || ParentalRatingHelper.ShouldExclude(candidate, maximumRating));
        if (!fetched.Complete && candidates.Count == 0)
        {
            throw new HttpRequestException("Certification enrichment was incomplete; no safe replacement pool could be verified.");
        }
    }

    /// <inheritdoc />
    public async Task<DiscoveryResult?> ScoreExternalCandidatesAsync(
        Guid jellyfinUserId,
        IReadOnlyList<ExternalDiscoveryCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return null;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            // Enrichment and the shared scorer both need a reachable Seerr; without it we cannot score.
            _pluginLog.LogDebug(LogCategory, "External scoring skipped: Seerr is not configured.", _logger);
            return null;
        }

        var profile = _watchHistoryService.GetUserWatchProfile(jellyfinUserId);
        if (profile is null)
        {
            _pluginLog.LogDebug(LogCategory, "External scoring skipped: no watch profile for the user.", _logger);
            return null;
        }

        var seriesEpisodeCounts = _watchHistoryService.GetSeriesEpisodeCounts();
        var genreCatalogVersion = GenreNormalizer.CatalogVersion;
        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);
        var topGenres = genrePreferences
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();

        var avgYear = ContentScoring.ComputeAverageYear(profile);
        var preferredPeople = BuildPreferredPeopleSet(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, profile);
        var isChildAccount = profile.MaxParentalRating.HasValue && profile.MaxParentalRating.Value <= ChildAccountMaxParentalRating;

        Uri baseUri;
        string apiKey;
        try
        {
            (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, _secretProtector.Unprotect(config.SeerrApiKey));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(LogCategory, $"External scoring: invalid Seerr configuration: {ex.Message}", logger: _logger);
            return null;
        }

        // Score external candidates through the same exclusion + parental + quality filter as local
        // ones, so owned titles are never suggested or offered for duplicate requests.
        var mapped = candidates.Select(ToTmdbItem).ToList();
        var mappedCount = mapped.Count;
        var client = GetSeerrClient(config.SeerrSkipCertificateValidation);
        var enrichedKeys = new HashSet<(int TmdbId, string MediaType)>();
        if (profile.MaxParentalRating.HasValue && profile.MaxParentalRating.Value < ParentalRatingHelper.UnrestrictedThreshold)
        {
            var required = await EnrichRequiredMetadataAsync(client, baseUri, apiKey, mapped, cancellationToken).ConfigureAwait(false);
            enrichedKeys = required.EnrichedKeys;
            // Fail closed for restricted profiles: without enriched genres the parental blacklist cannot
            // judge, so drop items the enrichment could not reach rather than risk restricted titles.
            mapped.RemoveAll(c => !enrichedKeys.Contains((c.Id, c.MediaType)));
        }

        var sharedExclusions = await GetCachedExclusionSetAsync(config, cancellationToken).ConfigureAwait(false);
        var userExcluded = BuildUserExclusionSet(profile, sharedExclusions);

        // Drop anything our own local discovery already recommends to this user, so the Trakt tab never
        // duplicates a title the "For you" tab is already showing. Best-effort: a cache miss or read failure
        // must not break Trakt scoring.
        await AddOwnRecommendationExclusionsAsync(jellyfinUserId, userExcluded).ConfigureAwait(false);

        // The Trakt tab preserves Trakt's own ranking, so our soft quality floors (minimum rating, minimum
        // year) must not silently prune what Trakt deliberately surfaced. Disable both for this path by passing
        // 0 - ComputeMinYear returns 0 when avgYear is 0. The real avgYear still flows into the scoring context
        // below as a feature input; only the filter thresholds are relaxed. Hard filters (parental, owned,
        // excluded set, dedup) stay fully active.
        var uniqueCandidates = DeduplicateAndFilter(mapped, userExcluded, profile.MaxParentalRating, minVoteAverage: 0, avgYear: 0, isChildAccount);
        if (uniqueCandidates.Count == 0)
        {
            // The stage counts make an ownership-exclusion collapse (every candidate already owned) distinguishable
            // from an empty input, an enrichment drop, or the parental/dedup filter. mappedCount is the raw total
            // before the restricted-profile enrichment gate (RemoveAll above), so the real candidate count stays
            // visible even when the enrichment gate removed everything. The parental and dedup pruning happens
            // inside DeduplicateAndFilter, so it is folded into the "after filter" count, not the enrichment delta.
            _pluginLog.LogDebug(
                LogCategory,
                $"External scoring: {mappedCount} mapped, {mapped.Count} after enrichment → 0 after filter (exclusions={userExcluded.Count}, enriched={enrichedKeys.Count}, parental={profile.MaxParentalRating}).",
                _logger);
            return null;
        }

        _pluginLog.LogDebug(
            LogCategory,
            $"External scoring: {mapped.Count} mapped → {uniqueCandidates.Count} after filter (exclusions={userExcluded.Count}).",
            _logger);

        var recommendations = await ScoreCandidatesForUserAsync(
            new CandidateScoringContext
            {
                Profile = profile,
                Candidates = uniqueCandidates,
                TopGenres = topGenres,
                GenrePreferences = genrePreferences,
                GenreCatalogVersion = genreCatalogVersion,
                PreferredPeople = preferredPeople,
                AvgYear = avgYear,
                GenreExposure = genreExposure,
                SeriesEpisodeCounts = seriesEpisodeCounts,
                Client = client,
                BaseUri = baseUri,
                ApiKey = apiKey,

                // Preserve the external source's own ranking (e.g. Trakt's recommendation order) instead of
                // re-ranking by our local score; the score still shows on the card as supporting info.
                RankBasedOrdering = true,
            },
            cancellationToken).ConfigureAwait(false);

        if (recommendations.Count == 0)
        {
            return null;
        }

        // Keep the per-item feature reason that BuildRecommendation computed (e.g. "reasonGenre: Sci-Fi") so the
        // card explains WHY this title fits the user. The Trakt tab itself signals the source, so no generic
        // source stamp is layered on top (that would clobber the feature reason the card now shows).

        return new DiscoveryResult
        {
            UserId = profile.UserId,
            UserName = profile.UserName,
            Recommendations = recommendations,
            GeneratedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public DiscoveryResult FilterConsumedItems(Guid jellyfinUserId, DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var policy = CurrentParentalPolicy(jellyfinUserId);
        var certificationCountry = CurrentCertificationCountry();
        if (!policy.Available)
        {
            return new DiscoveryResult { UserId = jellyfinUserId, GeneratedAt = result.GeneratedAt };
        }

        var excluded = ConsumedItemKeys(jellyfinUserId);
        var filtered = new DiscoveryResult
        {
            UserId = result.UserId,
            UserName = result.UserName,
            Recommendations = result.Recommendations.Where(recommendation =>
                IsCertifiedForPolicy(recommendation, policy.MaximumRating, certificationCountry) && !IsConsumed(recommendation, excluded)).ToList(),
            GeneratedAt = result.GeneratedAt
        };
        if (policy.MaximumRating.HasValue && policy.MaximumRating.Value < ParentalRatingHelper.UnrestrictedThreshold
            && certificationCountry != CurrentCertificationCountry())
        {
            filtered.Recommendations = [];
        }

        return filtered;
    }

    private HashSet<(int TmdbId, string MediaType)>? ConsumedItemKeys(Guid jellyfinUserId)
    {
        HashSet<(int TmdbId, string MediaType)>? excluded = null;
        try
        {
            var dismissed = _feedbackStore.GetDismissedItems(jellyfinUserId);
            var requested = _feedbackStore.GetRequestedItems(jellyfinUserId);
            if ((dismissed?.Count ?? 0) > 0 || (requested?.Count ?? 0) > 0)
            {
                excluded = new HashSet<(int TmdbId, string MediaType)>();
                if (dismissed is not null)
                {
                    excluded.UnionWith(dismissed);
                }

                if (requested is not null)
                {
                    excluded.UnionWith(requested);
                }
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Consumed-item filter unavailable for user {jellyfinUserId}; serving unfiltered: {ex.Message}",
                _logger);
        }

        return excluded;
    }

    private static bool IsCertifiedForPolicy(DiscoveryRecommendation recommendation, int? maximumRating, string country)
        => !maximumRating.HasValue || maximumRating.Value >= ParentalRatingHelper.UnrestrictedThreshold
            || (recommendation.CertificationPolicyCountry == country && recommendation.CertificationScore.HasValue
                && recommendation.CertificationScore.Value >= 0 && recommendation.CertificationScore.Value <= maximumRating.Value);

    private static bool IsConsumed(DiscoveryRecommendation recommendation, HashSet<(int TmdbId, string MediaType)>? excluded)
    {
        var mediaType = string.IsNullOrWhiteSpace(recommendation.MediaType) ? MediaTypeMovie : recommendation.MediaType.Trim().ToLowerInvariant();
        return recommendation.AlreadyRequested || (excluded is not null && excluded.Contains((recommendation.TmdbId, mediaType)));
    }

    private string CurrentCertificationCountry()
    {
        var country = _serverConfiguration?.Configuration?.MetadataCountryCode;
        return string.IsNullOrWhiteSpace(country) ? "*" : country.Trim().ToUpperInvariant();
    }

    private (bool Available, int? MaximumRating) CurrentParentalPolicy(Guid userId)
    {
        try
        {
            // The interface's default implementation already supports legacy providers.
            // An explicit unavailable policy must never be overridden by a stale profile.
            return _watchHistoryService.GetUserParentalPolicy(userId);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "Could not verify current discovery parental policy for user {UserId}", userId);
            return (false, null);
        }
    }

    /// <inheritdoc />
    public async Task<(bool Allowed, bool Verified)> VerifyRequestForUserAsync(Guid userId, int tmdbId, string mediaType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tmdbId <= 0 || mediaType is not ("movie" or "tv"))
        {
            return (false, false);
        }

        var policy = CurrentParentalPolicy(userId);
        if (!policy.Available)
        {
            return (false, true);
        }

        if (!policy.MaximumRating.HasValue || policy.MaximumRating.Value >= ParentalRatingHelper.UnrestrictedThreshold)
        {
            return (true, true);
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return (false, false);
        }

        try
        {
            var (baseUri, apiKey) = ValidateSeerrConfig(config.SeerrUrl, _secretProtector.Unprotect(config.SeerrApiKey));
            var candidate = new TmdbDiscoverItem { Id = tmdbId, MediaType = mediaType };
            var fetched = await EnrichCandidateWithMetadataAsync(GetSeerrClient(config.SeerrSkipCertificateValidation), baseUri, apiKey, candidate, cancellationToken, forceRefresh: true).ConfigureAwait(false);
            // The provider round-trip can outlive a parental-policy change or account disablement.
            policy = CurrentParentalPolicy(userId);
            if (!policy.Available)
            {
                return (false, true);
            }

            if (!policy.MaximumRating.HasValue || policy.MaximumRating.Value >= ParentalRatingHelper.UnrestrictedThreshold)
            {
                return (true, true);
            }

            var currentCountry = candidate.CertificationPolicyCountry == CurrentCertificationCountry();
            return (fetched && currentCountry && !ParentalRatingHelper.ShouldExclude(candidate, policy.MaximumRating), fetched && currentCountry && candidate.CertificationScore.HasValue);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not validate discovery certification endpoint");
            return (false, false);
        }
    }

    // Projects a public external candidate onto the internal TMDb candidate shape the scorer consumes.
    private static TmdbDiscoverItem ToTmdbItem(ExternalDiscoveryCandidate c)
    {
        var isTv = string.Equals(c.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

        // Only build a date from a year the DateTime constructor accepts: an external candidate can carry a
        // 0 / negative / >9999 year, and letting that reach the constructor would throw and sink the whole
        // list. An out-of-range year is treated as no date, so the rest of the item still scores.
        var yearForDate = c.Year is >= 1 and <= 9999 ? c.Year : null;
        return new TmdbDiscoverItem
        {
            Id = c.TmdbId,
            MediaType = isTv ? "tv" : MediaTypeMovie,
            Title = isTv ? null : c.Title,
            Name = isTv ? c.Title : null,
            GenreIds = [.. c.GenreIds],
            VoteAverage = c.VoteAverage,
            Popularity = c.Popularity,
            PosterPath = c.PosterPath,
            Overview = c.Overview,
            Adult = c.Adult,
            ReleaseDate = !isTv && yearForDate.HasValue ? new DateTime(yearForDate.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            FirstAirDate = isTv && yearForDate.HasValue ? new DateTime(yearForDate.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            TraktSlug = c.TraktSlug,
            SourceRank = c.SourceRank,
        };
    }

    /// <summary>
    ///     Runs the shared three-phase scoring pipeline: pre-score with the user's ensemble, enrich under
    ///     the shared detail budget, then score eligible candidates and project recommendations. Ranked
    ///     sources additionally require verified availability for every selected replacement. Extracted from
    ///     GenerateForUserAsync so the Trakt external source scores candidates through the exact same path.
    /// </summary>
    private async Task<List<DiscoveryRecommendation>> ScoreCandidatesForUserAsync(
        CandidateScoringContext context,
        CancellationToken cancellationToken)
    {
        ApplyCachedCandidateMetadata(context);
        // The user's own ensemble (per-user blend when they have enough history, else the global fallback),
        // so discovery scores a candidate exactly as the recommendations tab would for this user. Resolve it
        // once per user and read the feature means from the SAME instance that scores, so a per-user model
        // materializing mid-run cannot leave scoring and feature means on two different models.
        var catalogVersion = GenreNormalizer.CatalogVersion;
        var userContext = ExternalCandidateFeatureBuilder.PrepareUserContext(context.Profile);
        var featureContext = PrepareFeatureContext(context, userContext, null, catalogVersion);
        var isPersonalized = HasPersonalPreferences(context, userContext) || featureContext.WatchedCollections is { Count: > 0 };
        if (!isPersonalized)
        {
            _pluginLog.LogDebug(LogCategory, $"External scoring uses a metadata-only cold-start prior for {context.Profile.UserName}.", _logger);
        }

        var userStrategy = isPersonalized ? _perUserRegistry.GetEnsembleForUser(context.Profile.UserId) : null;
        featureContext = featureContext with { FeatureMeans = userStrategy?.LearnedStrategy.GetFeatureMeans() };
        var preScored = PreScoreCandidates(context, featureContext, userStrategy);
        await EnrichOptionalCandidatesAsync(context, preScored, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (GenreNormalizer.CatalogVersion != catalogVersion)
        {
            var genres = PreferenceBuilder.BuildGenrePreferenceVector(context.Profile, context.SeriesEpisodeCounts);
            featureContext = featureContext with
            {
                GenrePreferences = genres,
                GenreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genres, context.Profile),
                User = userContext with { Engagement = ContentScoring.BuildGenreEngagementContext(context.Profile) }
            };
        }

        return FinalScoreCandidates(context, featureContext, userStrategy);
    }

    private static bool HasPersonalPreferences(CandidateScoringContext context, ExternalCandidateFeatureBuilder.UserContext user)
        => context.GenrePreferences.Count > 0 || context.PreferredPeople.Count > 0
            || user.Franchises.Count > 0 || user.Tags.Count > 0 || user.Studios.Count > 0;

    private ExternalCandidateFeatureBuilder.Context PrepareFeatureContext(CandidateScoringContext context, ExternalCandidateFeatureBuilder.UserContext user, IReadOnlyList<double>? means, int catalogVersion)
    {
        var genres = context.GenreCatalogVersion == catalogVersion
            ? GenreNormalizer.Preferences(context.GenrePreferences)
            : PreferenceBuilder.BuildGenrePreferenceVector(context.Profile, context.SeriesEpisodeCounts);
        return new ExternalCandidateFeatureBuilder.Context
        {
            GenrePreferences = genres,
            PreferredPeople = context.PreferredPeople,
            AverageYear = context.AvgYear,
            GenreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genres, context.Profile),
            User = user,
            IsWeekend = TemporalFeatures.ResolveIsWeekend(context.Profile),
            FeatureMeans = means,
            WatchedCollections = GetWatchedCollections(context.Profile)
        };
    }

    private void ApplyCachedCandidateMetadata(CandidateScoringContext context)
    {
        foreach (var candidate in context.Candidates.Where(candidate => !candidate.MetadataFetchAttempted))
        {
            var detail = CachedDetail(MetadataCacheKey(context.BaseUri, context.ApiKey, MetadataPath(candidate)));
            if (detail is not null)
            {
                candidate.MetadataFetchAttempted = true;
                candidate.MetadataFetchSucceeded = ApplyFetchedDetail(candidate, detail);
            }
        }
    }

    private static List<TmdbDiscoverItem> PreScoreCandidates(CandidateScoringContext context, ExternalCandidateFeatureBuilder.Context features, EnsembleScoringStrategy? strategy)
    {
        // Phase 1: pre-score all candidates, including any reusable provider metadata already in cache.
        var preScored = new List<(TmdbDiscoverItem Item, double Score)>(context.Candidates.Count);
        foreach (var candidate in context.Candidates)
        {
            var vector = ExternalCandidateFeatureBuilder.Build(candidate, features);
            var score = ScoreExternalCandidate(vector, HasCandidatePersonalEvidence(candidate, vector, features) ? strategy : null);
            preScored.Add((candidate, score));
        }

        // Sort for enrichment selection: by source rank when the source supplies its own ordering (Trakt),
        // otherwise by pre-score. Sorting before the Take is load-bearing. A score-based Take would drop
        // low-score-but-high-rank items the rank-based path must keep.
        if (context.RankBasedOrdering)
        {
            preScored.Sort((a, b) => CompareBySourceRank(a.Item, b.Item));
        }
        else
        {
            preScored.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        return preScored.Select(row => row.Item).ToList();
    }

    private async Task EnrichOptionalCandidatesAsync(CandidateScoringContext context, List<TmdbDiscoverItem> ordered, CancellationToken cancellationToken)
    {
        // Phase 2: one bounded shared detail pass for top candidates. Credits, collection, keywords,
        // studios and certifications come from the same response; failed required fetches are not retried here.
        using (var enrichmentBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            enrichmentBudget.CancelAfter(TimeSpan.FromMilliseconds(MetadataEnrichmentTimeoutMs));
            try
            {
                if (context.RankBasedOrdering)
                {
                    await EnrichRankedCandidatesAsync(context, ordered, enrichmentBudget.Token).ConfigureAwait(false);
                }
                else
                {
                    var pending = ordered.Take(CreditsEnrichmentBudget).Where(candidate => !candidate.MetadataFetchAttempted).ToList();
                    await FetchOptionalDetailsAsync(context, pending, enrichmentBudget.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Optional metadata times out as one pass, not one timeout per batch/user candidate.
            }

            var inspected = ordered.Take(CreditsEnrichmentBudget).ToList();
            var enrichedCount = inspected.Count(c => c.KnownPeople != null);
            _pluginLog.LogDebug(
                LogCategory,
                $"User {context.Profile.UserName}: Enriched {enrichedCount}/{inspected.Count} candidates with credits data.",
                _logger);
        }
    }

    private async Task FetchOptionalDetailsAsync(CandidateScoringContext context, List<TmdbDiscoverItem> candidates, CancellationToken cancellationToken)
    {
        if (context.PreferredPeople.Count > 0)
        {
            await EnrichTopCandidatesWithCreditsAsync(context.Client, context.BaseUri, context.ApiKey, candidates, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await EnrichCandidatesWithMetadataAsync(context.Client, context.BaseUri, context.ApiKey, candidates, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnrichRankedCandidatesAsync(CandidateScoringContext context, List<TmdbDiscoverItem> ordered, CancellationToken cancellationToken)
    {
        var requests = 0;
        while (requests < CreditsEnrichmentBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordered.Count(candidate => IsVerifiedRankedCandidate(candidate, context.Profile.MaxParentalRating)) >= MaxPoolPerUser)
            {
                break;
            }

            var pending = ordered.Where(candidate => !candidate.MetadataFetchAttempted)
                .Take(Math.Min(MetadataEnrichmentParallelism, CreditsEnrichmentBudget - requests)).ToList();
            if (pending.Count == 0)
            {
                break;
            }

            requests += pending.Count;
            await EnrichCandidatesWithMetadataAsync(context.Client, context.BaseUri, context.ApiKey, pending, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsVerifiedRankedCandidate(TmdbDiscoverItem candidate, int? maximumRating)
        => candidate.MetadataFetchSucceeded && !candidate.IsAlreadyAvailable && !ParentalRatingHelper.ShouldExclude(candidate, maximumRating);

    private static List<DiscoveryRecommendation> FinalScoreCandidates(CandidateScoringContext context, ExternalCandidateFeatureBuilder.Context featureContext, EnsembleScoringStrategy? userStrategy)
    {
        // Phase 3: score eligible candidates; personal-library discovery retains candidates outside the
        // enrichment budget, while ranked external sources require a successful availability check.
        var scored = new List<(TmdbDiscoverItem Item, CandidateFeatures Features, double Score, bool Personalized)>(context.Candidates.Count);
        foreach (var candidate in context.Candidates)
        {
            if (!CanFinalScore(context, candidate))
            {
                continue;
            }

            var features = ExternalCandidateFeatureBuilder.Build(candidate, featureContext);
            var personalized = userStrategy is not null && HasCandidatePersonalEvidence(candidate, features, featureContext);
            var score = ScoreExternalCandidate(features, personalized ? userStrategy : null);
            scored.Add((candidate, features, score, personalized));
        }

        // Final ordering: honor the source rank for a rank-based source, else rank by score.
        if (context.RankBasedOrdering)
        {
            scored.Sort((a, b) => CompareBySourceRank(a.Item, b.Item));
        }
        else
        {
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        var topN = scored;

        // Build recommendations
        var recommendations = new List<DiscoveryRecommendation>(topN.Count);
        var reasonPeople = userStrategy is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : context.PreferredPeople;
        foreach (var (item, features, score, personalized) in topN)
        {
            var recommendation = BuildRecommendation(item, features, score, context.TopGenres, reasonPeople, featureContext.GenrePreferences, personalized ? featureContext.User : null);
            recommendation.IsPersonalized = personalized;
            recommendation.TmdbCollectionId = item.CollectionId;
            if (recommendation.ReasonKey == "reasonGenre" && recommendation.RelatedInfo is { } genre)
            {
                recommendation.RelatedInfo = GenreNormalizer.DisplayName(genre, context.Profile.GenreDistribution.Keys);
            }

            recommendations.Add(recommendation);
        }

        return DiscoverySelection.Select(recommendations, MaxPoolPerUser, context.RankBasedOrdering);
    }

    private static bool HasCandidatePersonalEvidence(TmdbDiscoverItem candidate, CandidateFeatures features, ExternalCandidateFeatureBuilder.Context context)
        => features.HasGenreEvidence
            || (context.PreferredPeople.Count > 0 && candidate.KnownPeople is not null)
            || (context.User.Tags.Count > 0 && features.IsExternalFeatureKnown(FeatureIndex.TagSimilarity))
            || (context.User.Studios.Count > 0 && features.IsExternalFeatureKnown(FeatureIndex.StudioMatch))
            || (HasCollectionPreference(context) && features.IsExternalFeatureKnown(FeatureIndex.FranchiseAffinity));

    private static bool HasCollectionPreference(ExternalCandidateFeatureBuilder.Context context)
        => context.User.Franchises.Count > 0 || context.WatchedCollections is { Count: > 0 };

    private static bool CanFinalScore(CandidateScoringContext context, TmdbDiscoverItem candidate)
        => !candidate.IsAlreadyAvailable && !ParentalRatingHelper.ShouldExclude(candidate, context.Profile.MaxParentalRating)
            && (!context.RankBasedOrdering || candidate.MetadataFetchSucceeded);

    private Dictionary<int, int>? GetWatchedCollections(UserWatchProfile profile)
    {
        try
        {
            var queried = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true });
            if (queried is null)
            {
                return null;
            }

            var collections = queried.OfType<MediaBrowser.Controller.Entities.Movies.BoxSet>();
            return CollectionMembershipResolver.ExternalCounts(profile.WatchedItems, collections, _linkedChildren is null ? null : id => _linkedChildren.GetLinkedChildrenIds(id, (int)LinkedChildType.Manual));
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(LogCategory, "External collection lookup unavailable; retaining unknown-metadata priors.", _logger);
            return null;
        }
    }

    private static double ScoreExternalCandidate(CandidateFeatures features, EnsembleScoringStrategy? strategy)
        => strategy is not null ? strategy.Score(features) : EngineConstants.ComputeColdStartPrior(features.CombinedCriticScore, features.RecencyScore, features.PopularityScore);

    /// <summary>Retrieves public popular/trending titles without requiring personal history or changing account filters.</summary>
    private async Task GatherColdStartCandidatesAsync(HttpClient client, Uri baseUri, string apiKey, List<TmdbDiscoverItem> candidates, CancellationToken cancellationToken)
    {
        var queries = new[]
        {
            (Path: "api/v1/discover/movies?page=1", MediaType: (string?)MediaTypeMovie),
            (Path: "api/v1/discover/tv?page=1", MediaType: (string?)"tv"),
            (Path: "api/v1/discover/trending?mediaType=all&timeWindow=week&page=1", MediaType: (string?)null)
        };
        foreach (var query in queries)
        {
            var items = await ExecuteDiscoverQueryAsync(client, baseUri, apiKey, query.Path, cancellationToken, requireSuccess: true).ConfigureAwait(false);
            if (query.MediaType is not null)
            {
                StampMediaType(items, query.MediaType);
            }

            candidates.AddRange(items);
        }
    }

    // Orders by source rank ascending (lower rank = better), with null ranks sorted last so a candidate the
    // source did not rank never displaces a ranked one. Ties keep the input order (stable callers use List.Sort,
    // which is not stable, so equal ranks are unspecified among themselves. Source ranks are unique in practice).
    private static int CompareBySourceRank(TmdbDiscoverItem a, TmdbDiscoverItem b)
    {
        if (a.SourceRank == b.SourceRank)
        {
            return 0;
        }

        if (a.SourceRank is null)
        {
            return 1;
        }

        if (b.SourceRank is null)
        {
            return -1;
        }

        return a.SourceRank.Value.CompareTo(b.SourceRank.Value);
    }

    /// <summary>
    ///     Projects a scored, enriched candidate into a <see cref="DiscoveryRecommendation"/>.
    /// </summary>
    private static DiscoveryRecommendation BuildRecommendation(
        TmdbDiscoverItem item,
        CandidateFeatures features,
        double score,
        List<string> topGenres,
        HashSet<string> preferredPeople,
        Dictionary<string, double>? genrePreferences = null,
        ExternalCandidateFeatureBuilder.UserContext? userContext = null)
    {
        var genres = TmdbGenreMap.ToJellyfinGenres(item.GenreIds);
        var (reasonKey, relatedInfo) = genrePreferences is null
            ? DetermineReason(features, item, topGenres, preferredPeople)
            : DetermineReasonFromPreferences(features, item, genrePreferences, preferredPeople);

        if (userContext is not null && reasonKey != "reasonPersonNamed")
        {
            var metadataReason = DetermineMetadataReason(features, item, userContext);
            if (metadataReason.HasValue)
            {
                (reasonKey, relatedInfo) = metadataReason.Value;
            }
        }

        return new DiscoveryRecommendation
        {
            TmdbId = item.Id,
            MediaType = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? "tv"
                : MediaTypeMovie,
            Title = item.DisplayTitle,
            Year = item.EffectiveReleaseDate?.Year,
            Score = score,
            ScoringFeatures = features.Copy(),
            CertificationScore = item.CertificationScore,
            CertificationPolicyCountry = item.CertificationPolicyCountry,
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
            KnownPeople = item.KnownPeople,
            TraktSlug = item.TraktSlug,
            SourceRank = item.SourceRank
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
        CancellationToken cancellationToken,
        bool requireSuccess = false)
    {
        // For child accounts: query Family genre for movies, Kids for TV
        var familyItems = await ExecuteDiscoverQueryAsync(
            client, baseUri, apiKey, $"api/v1/discover/movies/genre/{TmdbGenreFamily}?page=1", cancellationToken, requireSuccess).ConfigureAwait(false);
        allCandidates.AddRange(familyItems);

        var familyItems2 = await ExecuteDiscoverQueryAsync(
            client, baseUri, apiKey, $"api/v1/discover/movies/genre/{TmdbGenreFamily}?page=2", cancellationToken, requireSuccess).ConfigureAwait(false);
        allCandidates.AddRange(familyItems2);

        // Animation + Family for movies (children's animation)
        var animItems = await ExecuteDiscoverQueryAsync(
            client, baseUri, apiKey, $"api/v1/discover/movies/genre/{TmdbGenreAnimation}?page=1", cancellationToken, requireSuccess).ConfigureAwait(false);
        allCandidates.AddRange(animItems);

        // Kids TV genre
        var kidsItems = await ExecuteDiscoverQueryAsync(
            client, baseUri, apiKey, $"api/v1/discover/tv/genre/{TmdbGenreTvKids}?page=1", cancellationToken, requireSuccess).ConfigureAwait(false);
        StampMediaType(kidsItems, "tv");
        allCandidates.AddRange(kidsItems);

        var kidsItems2 = await ExecuteDiscoverQueryAsync(
            client, baseUri, apiKey, $"api/v1/discover/tv/genre/{TmdbGenreTvKids}?page=2", cancellationToken, requireSuccess).ConfigureAwait(false);
        StampMediaType(kidsItems2, "tv");
        allCandidates.AddRange(kidsItems2);

        // Family TV genre
        var familyTvItems = await ExecuteDiscoverQueryAsync(
            client, baseUri, apiKey, $"api/v1/discover/tv/genre/{TmdbGenreFamily}?page=1", cancellationToken, requireSuccess).ConfigureAwait(false);
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
        if (movieGenreIds.Count == 0 && tvGenreIds.Count == 0 && string.IsNullOrEmpty(primaryLanguage))
        {
            // Custom or newly localized genres must not leave retrieval empty; metadata can resolve new aliases before scoring.
            await GatherColdStartCandidatesAsync(client, baseUri, apiKey, allCandidates, cancellationToken).ConfigureAwait(false);
            return;
        }

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

    // Folds the user's own currently-cached local discovery recommendations into an exclusion set, so an
    // external source (Trakt) never surfaces a title the "For you" tab already shows. Keyed on
    // (TmdbId, lowercased MediaType) to match DeduplicateAndFilter's exclusion lookup. Best-effort: any read
    // failure leaves the set untouched rather than aborting external scoring.
    private async Task AddOwnRecommendationExclusionsAsync(Guid jellyfinUserId, HashSet<(int TmdbId, string MediaType)> userExcluded)
    {
        try
        {
            var ownResult = (await _cache.LoadAsync(CancellationToken.None).ConfigureAwait(false))
                .FirstOrDefault(r => r.UserId.Equals(jellyfinUserId));
            if (ownResult is null)
            {
                return;
            }

            foreach (var rec in ownResult.Recommendations)
            {
                var mediaType = string.IsNullOrWhiteSpace(rec.MediaType) ? MediaTypeMovie : rec.MediaType.Trim().ToLowerInvariant();
                userExcluded.Add((rec.TmdbId, mediaType));
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Could not load own recommendations to exclude for user {jellyfinUserId}: {ex.Message}",
                _logger);
        }
    }

    private async Task<List<TmdbDiscoverItem>> ExecuteDiscoverQueryAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        string queryPath,
        CancellationToken cancellationToken,
        bool requireSuccess = false)
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
                if (requireSuccess)
                {
                    response.EnsureSuccessStatusCode();
                }

                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<TmdbDiscoverResponse>(json, JsonOptions);
            if (requireSuccess && page is null)
            {
                throw new JsonException("Provider discovery response was null.");
            }

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
            if (requireSuccess)
            {
                throw;
            }

            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            _pluginLog.LogWarning(LogCategory, $"Query failed: {queryPath} - {ex.Message}", ex, _logger);
            if (requireSuccess)
            {
                throw;
            }

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

    /// <summary>
    ///     Returns the shared exclusion set (library + Arr titles), serving the cached copy while fresh.
    ///     A defensive copy is returned so callers can never mutate the cached set.
    /// </summary>
    private async Task<HashSet<(int TmdbId, string MediaType)>> GetCachedExclusionSetAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        Task<HashSet<(int TmdbId, string MediaType)>> build;
        lock (_exclusionCacheLock)
        {
            if (_cachedExclusions is not null && DateTime.UtcNow < _exclusionsExpiry)
            {
                return new HashSet<(int TmdbId, string MediaType)>(_cachedExclusions);
            }

            // Reuse an in-flight build, but never a settled one: same pattern as the Seerr user
            // roster, so concurrent Trakt tab loads share one rebuild instead of N parallel ones.
            if (_inflightExclusionBuild is not { IsCompleted: false })
            {
                _inflightExclusionBuild = StartExclusionBuildAsync(config);
            }

            build = _inflightExclusionBuild;
        }

        var fresh = await build.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // The shared build already wrote a successful result to the cache before completing, so
        // once it settles the cache is warm and the fast path above catches any later caller in
        // this burst. Read the cached copy here so every caller returns the same data.
        lock (_exclusionCacheLock)
        {
            if (_cachedExclusions is not null && DateTime.UtcNow < _exclusionsExpiry)
            {
                return new HashSet<(int TmdbId, string MediaType)>(_cachedExclusions);
            }
        }

        return new HashSet<(int TmdbId, string MediaType)>(fresh);
    }

    // Starts a single shared exclusion build, publishes the result to the cache before the task
    // completes, and clears the in-flight slot once it settles. Publishing inside the task closes
    // the window where a caller arriving after the build settled but before the cache was written
    // would start a second build. Runs on its own token so it is not cancelled by any one caller's
    // token; a failed build writes nothing, so it stays on the retriable path.
    private Task<HashSet<(int TmdbId, string MediaType)>> StartExclusionBuildAsync(PluginConfiguration config)
    {
        Task<HashSet<(int TmdbId, string MediaType)>> build = BuildAndCacheExclusionSetAsync(config);
        _ = build.ContinueWith(
            _ =>
            {
                lock (_exclusionCacheLock)
                {
                    // Clear only our own slot. A caller arriving after this build settles but before
                    // this continuation runs may have already installed a replacement build; wiping it
                    // would strand that fresh request and force yet another build.
                    if (ReferenceEquals(_inflightExclusionBuild, build))
                    {
                        _inflightExclusionBuild = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return build;
    }

    private async Task<HashSet<(int TmdbId, string MediaType)>> BuildAndCacheExclusionSetAsync(PluginConfiguration config)
    {
        var fresh = await BuildExclusionSetAsync(config, CancellationToken.None).ConfigureAwait(false);
        lock (_exclusionCacheLock)
        {
            _cachedExclusions = fresh;
            _exclusionsExpiry = DateTime.UtcNow.Add(ExclusionCacheTtl);
        }

        return fresh;
    }

    private async Task<HashSet<(int TmdbId, string MediaType)>> BuildExclusionSetAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<(int TmdbId, string MediaType)>();

        // Exclude titles already in the Jellyfin library. This covers items that no configured Arr
        // instance tracks (manually added, imported, or when Arr is not configured at all). A failed
        // library scan is contained inside the helper so discovery still runs the Arr sources; only a
        // requested cancellation propagates.
        cancellationToken.ThrowIfCancellationRequested();
        AddLibraryExclusions(excluded, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Exclude movies already in Radarr
        await AddRadarrExclusionsAsync(config, excluded, cancellationToken).ConfigureAwait(false);

        // Exclude TV series already in Sonarr (Sonarr v4+ provides tmdbId; v3 entries are skipped via TmdbId > 0 guard)
        await AddSonarrExclusionsAsync(config, excluded, cancellationToken).ConfigureAwait(false);

        return excluded;
    }

    /// <summary>
    ///     Adds the TMDb IDs of movies and series already present in the Jellyfin library to the exclusion set.
    /// </summary>
    /// <remarks>
    ///     This scans the whole library and deliberately ignores the recommendation ExcludedLibraries
    ///     setting. Discovery surfaces titles the user does not yet own, so anything already in any
    ///     library, even one excluded from recommendation scoring, must not be suggested for a new
    ///     request. Excluding a library hides its titles from recommendations; it does not make
    ///     Discovery offer to acquire copies the user already has.
    /// </remarks>
    /// <param name="excluded">The exclusion set to add library entries into.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    private void AddLibraryExclusions(HashSet<(int TmdbId, string MediaType)> excluded, CancellationToken cancellationToken)
    {
        // A library-query failure is non-fatal to discovery: the Radarr and Sonarr sources still
        // provide exclusions, so a broken library scan must not abort the whole generation before
        // they run. Contain it here the way the Arr fetches contain their own failures.
        try
        {
            var libraryItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
            });

            // Virtual stub entries carry a real TMDb id but no owned media file, so they must not join the
            // exclusion set. Filtering here keeps TmdbLibraryMapper free of location concerns.
            var ownedItems = libraryItems.Where(item => !_virtualItemDetector.IsVirtual(item)).ToList();

            var before = excluded.Count;
            foreach (var key in TmdbLibraryMapper.BuildTmdbKeySet(ownedItems))
            {
                excluded.Add(key);
            }

            _pluginLog.LogDebug(
                LogCategory,
                $"Library exclusions: {libraryItems.Count} scanned, {libraryItems.Count - ownedItems.Count} virtual skipped, {excluded.Count - before} added.",
                _logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to read Jellyfin library for discovery exclusions: {ex.Message}. Continuing with Arr sources.",
                ex,
                _logger);
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
            (instance, apiKey, ct) => _arrIntegration.GetRadarrMoviesAsync(instance.Url, apiKey, instance.SkipCertificateValidation, ct),
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
            (instance, apiKey, ct) => _arrIntegration.GetSonarrSeriesAsync(instance.Url, apiKey, instance.SkipCertificateValidation, ct),
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
    /// <param name="fetch">Delegate fetching the items for an instance (instance, unprotected API key, token).</param>
    /// <param name="tmdbSelector">Selects the TMDb ID from an item.</param>
    /// <param name="mediaType">The media type recorded in the exclusion set.</param>
    /// <param name="arrName">The Arr display name used in log messages (Radarr/Sonarr).</param>
    /// <param name="excluded">The exclusion set to add entries into.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task AddArrExclusionsAsync<T>(
        IEnumerable<ArrInstanceConfig> instances,
        Func<ArrInstanceConfig, string, CancellationToken, Task<List<T>?>> fetch,
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
                    instance, _secretProtector.Unprotect(instance.ApiKey), cancellationToken).ConfigureAwait(false);
                if (items != null)
                {
                    foreach (var tmdbId in items.Select(tmdbSelector).Where(tmdbId => tmdbId > 0))
                    {
                        excluded.Add((tmdbId, mediaType));
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

            if (!PassesPreDedupFilters(candidate, mediaTypeKey, excludedTmdbIds, minVoteAverage))
            {
                continue;
            }

            // Dedup sits between the pre- and post-dedup filters, matching the original ordering:
            // a candidate rejected by the parental/year gates below has still claimed its key here,
            // so a later duplicate that would pass is suppressed.
            if (!seen.Add((candidate.Id, mediaTypeKey)))
            {
                continue;
            }

            if (!PassesPostDedupFilters(candidate, maxParentalRating, minYear))
            {
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    ///     Filters applied before deduplication: exclusion set, Seerr availability, and minimum rating.
    /// </summary>
    private static bool PassesPreDedupFilters(
        TmdbDiscoverItem candidate,
        string mediaTypeKey,
        HashSet<(int TmdbId, string MediaType)> excludedTmdbIds,
        double minVoteAverage)
    {
        if (excludedTmdbIds.Contains((candidate.Id, mediaTypeKey)))
        {
            return false;
        }

        // Seerr already knows this title is (partially) available in the library, so recommending
        // it would just surface something the user can already watch.
        if (candidate.IsAlreadyAvailable)
        {
            return false;
        }

        return candidate.VoteAverage >= minVoteAverage;
    }

    /// <summary>
    ///     Filters applied after deduplication: parental rating and year relevance.
    /// </summary>
    private static bool PassesPostDedupFilters(
        TmdbDiscoverItem candidate,
        int? maxParentalRating,
        int minYear)
    {
        // Parental rating filter: exclude adult content and restricted genres
        if (ParentalRatingHelper.ShouldExclude(candidate, maxParentalRating))
        {
            return false;
        }

        // Year-based post-filtering (soft: only if year is available and min is set)
        return !(minYear > 0
            && candidate.EffectiveReleaseDate.HasValue
            && candidate.EffectiveReleaseDate.Value.Year < minYear);
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
            .Distinct(StringComparer.Ordinal)
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
        using var semaphore = new SemaphoreSlim(CreditsEnrichmentParallelism, CreditsEnrichmentParallelism);
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

        try
        {
            await EnrichCandidateWithMetadataAsync(client, baseUri, apiKey, candidate, cts.Token).ConfigureAwait(false);
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
    ///     Enriches mapped external candidates with Seerr metadata (genres, adult flag, poster, rating
    ///     fallback, popularity, availability) BEFORE filtering, with bounded parallelism. External sources
    ///     arrive without this data, and restricted accounts require verified certifications. Returns the
    ///     keys that were successfully enriched; failures leave the item untouched for the caller to fail
    ///     closed on. Source-provided values are never overwritten, only missing ones are filled.
    /// </summary>
    private async Task<MetadataEnrichmentResult> EnrichRequiredMetadataAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        List<TmdbDiscoverItem> candidates,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(RequiredMetadataBudgetMs));
        try
        {
            var enriched = await EnrichCandidatesWithMetadataAsync(client, baseUri, apiKey, candidates, budget.Token).ConfigureAwait(false);
            return new MetadataEnrichmentResult(enriched, candidates.All(candidate => candidate.MetadataFetchSucceeded));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var enriched = candidates.Where(candidate => candidate.MetadataFetchSucceeded).Select(candidate => (candidate.Id, candidate.MediaType)).ToHashSet();
            return new MetadataEnrichmentResult(enriched, false);
        }
    }

    private async Task<HashSet<(int TmdbId, string MediaType)>> EnrichCandidatesWithMetadataAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        List<TmdbDiscoverItem> candidates,
        CancellationToken cancellationToken)
    {
        var enriched = new HashSet<(int TmdbId, string MediaType)>();
        using var semaphore = new SemaphoreSlim(MetadataEnrichmentParallelism, MetadataEnrichmentParallelism);
        var tasks = candidates.Select(async candidate =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await EnrichCandidateWithMetadataAsync(
                    client, baseUri, apiKey, candidate, cancellationToken).ConfigureAwait(false))
                {
                    lock (enriched)
                    {
                        enriched.Add((candidate.Id, candidate.MediaType));
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return enriched;
    }

    /// <summary>
    ///     Fetches Seerr metadata for a single candidate and fills missing fields, bounded by MetadataEnrichmentTimeoutMs.
    /// </summary>
    /// <returns>True when the detail fetch succeeded (even if it carried no new fields).</returns>
    private async Task<bool> EnrichCandidateWithMetadataAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        TmdbDiscoverItem candidate,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!forceRefresh && candidate.MetadataFetchAttempted)
        {
            return candidate.MetadataFetchSucceeded;
        }

        candidate.MetadataFetchAttempted = true;
        candidate.MetadataFetchSucceeded = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(MetadataEnrichmentTimeoutMs));

        var mediaPath = MetadataPath(candidate);
        var cacheKey = MetadataCacheKey(baseUri, apiKey, mediaPath);
        var cachedDetail = forceRefresh ? null : CachedDetail(cacheKey);
        if (cachedDetail is not null)
        {
            candidate.MetadataFetchSucceeded = ApplyFetchedDetail(candidate, cachedDetail);
            return candidate.MetadataFetchSucceeded;
        }

        try
        {
            using var req = BuildRequest(HttpMethod.Get, baseUri, mediaPath, apiKey);
            using var response = await client.SendAsync(req, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>(json, JsonOptions);
            if (detail is null || !ApplyFetchedDetail(candidate, detail))
            {
                return false;
            }

            CacheDetail(cacheKey, detail);

            candidate.MetadataFetchSucceeded = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or TimeoutException)
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Metadata enrichment failed for {candidate.MediaType}#{candidate.Id}: {ex.Message}",
                _logger);
            return false;
        }
    }

    private string MetadataPath(TmdbDiscoverItem candidate)
    {
        var path = string.Equals(candidate.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? $"api/v1/tv/{candidate.Id}"
            : $"api/v1/movie/{candidate.Id}";
        var language = _serverConfiguration?.Configuration?.PreferredMetadataLanguage;
        return string.IsNullOrWhiteSpace(language) ? path : path + "?language=" + Uri.EscapeDataString(language);
    }

    private static string MetadataCacheKey(Uri baseUri, string apiKey, string mediaPath)
        => baseUri.AbsoluteUri + mediaPath + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    private SeerrMediaDetailResponse? CachedDetail(string key)
    {
        lock (_metadataCacheLock)
        {
            return _metadataCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow ? cached.Detail : null;
        }
    }

    private void CacheDetail(string key, SeerrMediaDetailResponse detail)
    {
        lock (_metadataCacheLock)
        {
            if (_metadataCache.Count >= 512 && !_metadataCache.ContainsKey(key))
            {
                _metadataCache.Remove(_metadataCache.MinBy(pair => pair.Value.ExpiresAtUtc).Key);
            }

            _metadataCache[key] = new CachedMediaDetail(detail, DateTime.UtcNow.Add(SeerrUserCacheTtl));
        }
    }

    private bool ApplyFetchedDetail(TmdbDiscoverItem candidate, SeerrMediaDetailResponse detail)
    {
        if (detail.Id <= 0 || detail.Id != candidate.Id)
        {
            return false;
        }

        ApplyEnrichedMetadata(candidate, detail);
        if (detail.Genres is not null)
        {
            foreach (var genre in detail.Genres.OfType<SeerrMediaDetailResponse.GenreRef>())
            {
                GenreNormalizer.RegisterProviderGenre(genre.Id, genre.Name);
            }
        }

        if (detail.HasCollectionMetadata)
        {
            candidate.HasCollectionMetadata = true;
            candidate.CollectionId = detail.Collection?.Id > 0 ? detail.Collection.Id : 0;
            candidate.CollectionName = detail.Collection?.Name;
        }

        if (detail.Keywords is not null)
        {
            candidate.KnownKeywords = detail.Keywords.OfType<SeerrMediaDetailResponse.GenreRef>().Select(keyword => keyword.Name).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        }

        if (detail.ProductionCompanies is not null)
        {
            candidate.KnownStudios = detail.ProductionCompanies.OfType<SeerrMediaDetailResponse.GenreRef>().Select(studio => studio.Name).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        }

        var certificationCountry = CurrentCertificationCountry();
        candidate.CertificationScore = CertificationResolver.Resolve(detail, _localization, candidate.Adult, certificationCountry == "*" ? null : certificationCountry);
        candidate.CertificationPolicyCountry = candidate.CertificationScore.HasValue ? certificationCountry : null;
        if (detail.Credits is not null)
        {
            candidate.KnownPeople = ExtractTopPeople(detail.Credits);
        }

        return true;
    }

    // Fills a candidate's missing fields from a fetched detail payload. Every rule is latch-only or
    // fill-if-empty: enrichment must never clear what is already known.
    private static void ApplyEnrichedMetadata(TmdbDiscoverItem candidate, SeerrMediaDetailResponse detail)
    {
        if (detail.Genres is { Count: > 0 })
        {
            candidate.GenreIds = detail.Genres.Select(g => g.Id).ToList();
        }

        // Latch-only: a missing flag must never clear what is already known.
        if (detail.Adult == true)
        {
            candidate.Adult = true;
        }

        if (string.IsNullOrEmpty(candidate.PosterPath) && !string.IsNullOrEmpty(detail.PosterPath))
        {
            candidate.PosterPath = detail.PosterPath;
        }

        // Seerr wins over the source's overview whenever it has one: an external source like Trakt
        // supplies English text, while Seerr returns the synopsis in its configured locale. Only an
        // empty Seerr overview falls back to the source value so a card is never blanked.
        if (!string.IsNullOrWhiteSpace(detail.Overview))
        {
            candidate.Overview = detail.Overview;
        }

        if (candidate.VoteAverage <= 0 && detail.VoteAverage > 0)
        {
            candidate.VoteAverage = detail.VoteAverage;
        }

        if (candidate.Popularity <= 0 && detail.Popularity > 0)
        {
            candidate.Popularity = detail.Popularity;
        }

        if (detail.MediaInfo is not null)
        {
            candidate.MediaInfo = detail.MediaInfo;
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
    ///     Determines the primary recommendation reason for an external (TMDb) candidate from the signals that are
    ///     genuinely available for it. Verified collection/franchise/studio reasons are added at projection time.
    ///     Priority: person > genre > trending (new + highly rated) > popular with viewers > highly rated.
    /// </summary>
    private static (string ReasonKey, string? RelatedInfo) DetermineReason(
        CandidateFeatures features,
        TmdbDiscoverItem candidate,
        List<string> topGenres,
        HashSet<string> preferredPeople)
    {
        var preferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < topGenres.Count; i++)
        {
            preferences[topGenres[i]] = topGenres.Count - i;
        }

        var result = DetermineReasonFromPreferences(features, candidate, preferences, preferredPeople);
        return result.ReasonKey == "reasonGenre" && result.RelatedInfo is { } genre
            ? (result.ReasonKey, GenreNormalizer.DisplayName(genre, topGenres))
            : result;
    }

    /// <summary>Explains only observed personal metadata, never positive values produced by mean imputation.</summary>
    /// <param name="features">The final candidate features.</param>
    /// <param name="candidate">The actual provider metadata.</param>
    /// <param name="userContext">Prepared preferences for the current user.</param>
    /// <returns>A truthful metadata reason, or null if no observed match exists.</returns>
    internal static (string ReasonKey, string? RelatedInfo)? DetermineMetadataReason(
        CandidateFeatures features,
        TmdbDiscoverItem candidate,
        ExternalCandidateFeatureBuilder.UserContext userContext)
    {
        if (features.IsExternalFeatureKnown(FeatureIndex.CollectionProgressionBoost) && features.CollectionProgressionBoost > 0 && candidate.CollectionId > 0)
        {
            return ("reasonCollectionContinuation", null);
        }

        if (features.IsExternalFeatureKnown(FeatureIndex.FranchiseAffinity) && features.FranchiseAffinity > 0.5)
        {
            return string.IsNullOrWhiteSpace(candidate.CollectionName)
                ? ("reasonFranchise", null)
                : ("reasonFranchiseNamed", candidate.CollectionName);
        }

        if (features.IsExternalFeatureKnown(FeatureIndex.StudioMatch) && features.StudioMatch)
        {
            var studio = candidate.KnownStudios?.FirstOrDefault(userContext.Studios.Contains);
            if (!string.IsNullOrWhiteSpace(studio))
            {
                return ("reasonStudioNamed", studio);
            }
        }

        return null;
    }

    private static (string ReasonKey, string? RelatedInfo) DetermineReasonFromPreferences(
        CandidateFeatures features,
        TmdbDiscoverItem candidate,
        Dictionary<string, double> genrePreferences,
        HashSet<string> preferredPeople)
    {
        if (features.PeopleSimilarity > ReasonPeopleThreshold && candidate.KnownPeople is { Count: > 0 })
        {
            var matchedPerson = candidate.KnownPeople.FirstOrDefault(preferredPeople.Contains);
            if (matchedPerson != null)
            {
                return ("reasonPersonNamed", matchedPerson);
            }
        }

        var matchedGenre = GenreNormalizer.BestMatch(TmdbGenreMap.ToJellyfinGenres(candidate.GenreIds), genrePreferences);
        if (features.GenreSimilarity > ReasonGenreThreshold && matchedGenre is not null)
        {
            return ("reasonGenre", matchedGenre);
        }

        if (features.RecencyScore > ReasonRecencyThreshold && features.CombinedCriticScore > ReasonCriticThreshold)
        {
            return ("reasonTrending", null);
        }

        // Popularity and critic rating are distinct signals. Popularity is checked first by design: when a title
        // is both widely watched and well reviewed, "popular with other viewers" is the more relatable headline
        // and critical acclaim is the secondary note, so popularity dominates the single-line reason.
        if (features.PopularityScore > ReasonPopularityThreshold)
        {
            return ("reasonPopularAmongViewers", null);
        }

        if (features.CombinedCriticScore > ReasonCriticThreshold)
        {
            return ("reasonHighlyRated", null);
        }

        // No signal cleared its threshold: stay honest with a generic reason instead of claiming the candidate
        // is "popular and highly rated" when it is neither.
        return ("reasonDefault", null);
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

        // Same central SSRF guard the integration and Arr paths enforce: the discovery path reaches the
        // network with an admin-supplied base URL too, so a cloud-metadata host must be blocked here as well.
        SsrfGuard.ThrowIfCloudMetadataHost(parsedBaseUrl.Host, nameof(baseUrl));

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
    private HttpClient GetSeerrClient(bool skipCertificateValidation) =>
        _httpClientFactory.CreateClient(skipCertificateValidation ? "SeerrDiscoveryInsecure" : "SeerrDiscovery");

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

    /// <summary>
    ///     Result of generating discovery for a single user: the status plus the fresh result (only set when
    ///     <see cref="UserGenerationStatus.Generated"/>).
    /// </summary>
    private readonly record struct UserGenerationOutcome(UserGenerationStatus Status, DiscoveryResult? Result);

    private sealed record CachedMediaDetail(SeerrMediaDetailResponse Detail, DateTime ExpiresAtUtc);

    /// <summary>Separates verified detail keys from the completion of the required provider pass.</summary>
    /// <param name="EnrichedKeys">Successfully fetched provider identities.</param>
    /// <param name="Complete">Whether every candidate's detail metadata was available.</param>
    private sealed record MetadataEnrichmentResult(HashSet<(int TmdbId, string MediaType)> EnrichedKeys, bool Complete);

    /// <summary>
    ///     Inputs for <see cref="ScoreCandidatesForUserAsync"/>: everything the shared scoring pipeline
    ///     needs for one user. Cancellation stays a separate parameter by convention.
    /// </summary>
    private sealed class CandidateScoringContext
    {
        public required UserWatchProfile Profile { get; init; }

        public required List<TmdbDiscoverItem> Candidates { get; init; }

        public required List<string> TopGenres { get; init; }

        public required Dictionary<string, double> GenrePreferences { get; init; }

        /// <summary>Gets the provider-alias version used to prepare retrieval preferences.</summary>
        public required int GenreCatalogVersion { get; init; }

        public required HashSet<string> PreferredPeople { get; init; }

        public required double AvgYear { get; init; }

        public required PreferenceBuilder.GenreExposureAnalysis GenreExposure { get; init; }

        /// <summary>Gets the same series totals used to rebuild genre preferences after provider alias updates.</summary>
        public IReadOnlyDictionary<Guid, int>? SeriesEpisodeCounts { get; init; }

        public required HttpClient Client { get; init; }

        public required Uri BaseUri { get; init; }

        public required string ApiKey { get; init; }

        /// <summary>
        ///     Gets a value indicating whether to order and truncate by the candidate's
        ///     <see cref="TmdbDiscoverItem.SourceRank"/> (the external source's own ranking) instead of by local
        ///     score. The score is still computed and attached for display; it just does not drive the ordering.
        ///     Default false (local score ordering).
        /// </summary>
        public bool RankBasedOrdering { get; init; }
    }
}
