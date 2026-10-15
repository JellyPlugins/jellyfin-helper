using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Default <see cref="ITraktDiscoveryService"/>: fetches Trakt personal recommendations (OAuth) and global
///     trending (client id), maps them to TMDb candidates, and scores them through the shared discovery seam.
/// </summary>
public sealed class TraktDiscoveryService : ITraktDiscoveryService
{
    private const string LogSource = "Trakt";
    private const string MediaTypeMovie = "movie";
    private const string MediaTypeTv = "tv";

    // Items fetched per Trakt list. A fixed value, not configurable: the ceiling is never reached in practice
    // and there is no UI to set it, so a serialized config field would be dead weight.
    private const int TraktLimit = 20;

    // Caches are warmed by the scheduled task; request-time serves a fresh entry or does a lazy live fetch.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    // Overall ceiling on one shared fetch (personal or trending). A hung Trakt socket must not pin the
    // in-flight slot forever and starve every subsequent request for that user.
    private static readonly TimeSpan PersonalFetchTimeout = TimeSpan.FromSeconds(90);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITraktPersonalSourceService _personalSources;
    private readonly ISeerrDiscoveryService _discoveryService;
    private readonly TraktCacheService _cache;
    private readonly IPluginLogService _pluginLog;
    private readonly IUserManager _userManager;
    private readonly ILogger<TraktDiscoveryService> _logger;

    // Per-user single-flight for personal fetches: concurrent requests for the same user (for example the same
    // account open on two devices) share one live fetch instead of each firing their own. The slot is published
    // and cleared under _personalFlightLock, and cleared only by a continuation on the shared task (not an
    // awaiting caller's finally), so a waiter cancelling cannot strand or re-spawn the shared fetch. A fetch
    // checks slot membership and writes to the cache/backoff under the same lock, so an invalidation (switch-off
    // or unlink on a concurrent call) that removed the slot bars the stale write with no check-then-act gap.
    // Trending is pool-backed on the fetch, but its per-user scoring is just as expensive and gets its own
    // single-flight.
    private readonly ConcurrentDictionary<Guid, PersonalFlight> _inflightPersonal = new();
    private readonly ConcurrentDictionary<Guid, Task<DiscoveryResult?>> _inflightTrending = new();
    private readonly Lock _personalFlightLock = new();
    private readonly Lock _trendingFlightLock = new();

    // Users currently in an empty "Trakt for you" outcome. The transition into empty logs once at Info; staying
    // empty logs at Debug so opening the tab repeatedly does not spam the admin log for the whole empty cohort.
    // A user is removed when they next produce a non-empty result, so a later recurrence is reported again.
    private readonly ConcurrentDictionary<Guid, byte> _loggedEmptyUsers = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktDiscoveryService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory (uses the hardened "Trakt" client).</param>
    /// <param name="personalSources">The per-user personal-source lifecycle (official Trakt plugin token, read-only).</param>
    /// <param name="discoveryService">The discovery service exposing the external-candidate scoring seam.</param>
    /// <param name="cache">The Trakt result cache.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="userManager">The Jellyfin user manager, used only to resolve a username for diagnostic logs.</param>
    /// <param name="logger">The logger.</param>
    public TraktDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ITraktPersonalSourceService personalSources,
        ISeerrDiscoveryService discoveryService,
        TraktCacheService cache,
        IPluginLogService pluginLog,
        IUserManager userManager,
        ILogger<TraktDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _personalSources = personalSources ?? throw new ArgumentNullException(nameof(personalSources));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Gets or sets the clock used to resolve an HTTP-date Retry-After header. Tests substitute a fixed clock;
    ///     production uses the system UTC clock. Kept off the constructor so dependency injection stays at seven arguments.
    /// </summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetPersonalAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktSourcingEnabled)
        {
            // Master switch off: source nothing from either own creds or the official plugin, and drop any warm
            // pool so flipping the switch takes effect immediately rather than on the 12h TTL. Route through the
            // service-level invalidation so a concurrent in-flight fetch is cancelled too, not just the cache
            // dropped, or that fetch could write a stale result after this returns.
            InvalidatePersonal(userId);
            return null;
        }

        // Cheap link gate first (memory + file read, no network): unlinked users never fetch, and any
        // leftover cache from before a disconnect is dropped instead of served stale. A full source
        // resolution runs only on a cache miss, so a transient failure never drops a warm cache and cache
        // hits never touch the network or the disk.
        // The resolution rules themselves live in ITraktPersonalSourceService; this method only orchestrates.
        if (!_personalSources.IsLinked(userId, config))
        {
            // Route through the service-level invalidation so a concurrent in-flight fetch is cancelled, not just
            // the cache dropped; otherwise that fetch could write a stale result for a user who just unlinked.
            InvalidatePersonal(userId);
            return null;
        }

        var cached = _cache.GetPersonal(userId, CacheTtl);
        if (cached is not null)
        {
            return _discoveryService.FilterConsumedItems(userId, cached);
        }

        // Negative-cache gate: a recent failure or empty result backs off further live fetches, so a rate-limited
        // or down Trakt is not re-hit on every tab load and duplicate loads coalesce onto the first outcome.
        if (_cache.IsPersonalBackoffActive(userId))
        {
            // Log the gated skip so an admin seeing an empty tab can tell a backoff from a genuine empty result;
            // Debug because it is per tab load and expected during the backoff window.
            _pluginLog.LogDebug(LogSource, $"Personal fetch suppressed by active backoff for {DescribeUser(userId)}.", _logger);
            return null;
        }

        // Single-flight the live fetch per user so two concurrent requests (e.g. two devices) share one fetch.
        // The shared work runs on an independent, time-boxed token so one caller cancelling cannot abort the
        // others; each caller still observes its own cancellation on the WaitAsync below.
        cancellationToken.ThrowIfCancellationRequested();

        Task<DiscoveryResult?> fetch;
        lock (_personalFlightLock)
        {
            // Reuse only an in-flight (not completed) task; start one otherwise. GetOrAdd could run the factory
            // more than once under contention, double-fetching the same user; the lock plus this check makes the
            // start exactly-once per burst.
            if (!_inflightPersonal.TryGetValue(userId, out var existing) || existing.Task.IsCompleted)
            {
                // The flight's own cancellation source represents its validity: an invalidation cancels it so the
                // surviving fetch skips its cache/backoff write. The completion continuation (wired below under
                // this same lock) owns and disposes the source once the shared task settles.
                var flightCts = new CancellationTokenSource();
                fetch = FetchAndScorePersonalAsync(userId, config, flightCts);

                // Publish the slot and wire the cleanup continuation while still holding the lock. The continuation
                // also takes this lock, so even a synchronously-completing fetch cannot run its slot-cleanup until
                // this block releases the lock, by which point the slot is published.
                _inflightPersonal[userId] = new PersonalFlight(flightCts, fetch);
                WirePersonalFlightCleanup(userId, fetch, flightCts);
            }
            else
            {
                fetch = existing.Task;
            }
        }

        return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Clears the flight's slot and disposes its cancellation source once the shared task settles, mirroring the
    // Seerr exclusion-build single-flight. The cleanup is a continuation (not an awaiting caller's finally) so a
    // waiter that cancels never strands the fetch or lets a later caller spawn a second live fetch. Everything
    // touching flightCts (this dispose and InvalidatePersonal's cancel) runs under _personalFlightLock, so a
    // cancel never hits a disposed source; the ReferenceEquals guard leaves a replacement slot untouched.
    private void WirePersonalFlightCleanup(Guid userId, Task<DiscoveryResult?> fetch, CancellationTokenSource flightCts)
    {
        _ = fetch.ContinueWith(
            _ =>
            {
                lock (_personalFlightLock)
                {
                    if (_inflightPersonal.TryGetValue(userId, out var current) && ReferenceEquals(current.Cts, flightCts))
                    {
                        _inflightPersonal.TryRemove(userId, out PersonalFlight? _);
                    }

                    flightCts.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Cache/backoff writes from a fetch go through these so a flight invalidated (switch-off or unlink on a
    // concurrent call) never persists a stale result or a backoff against a user who may since have re-linked.
    // The cancellation check and the write run under _personalFlightLock, the same lock InvalidatePersonal takes
    // to cancel the flight, so a cancel cannot land between the check and the write. The lock is reentrant, so a
    // synchronously-completing fetch that writes while GetPersonalAsync still holds the lock is fine. Logging is
    // intentionally not guarded: an outcome line is always recorded.
    private void CachePersonalIfValid(Guid userId, CancellationTokenSource flightCts, DiscoveryResult result)
    {
        lock (_personalFlightLock)
        {
            if (!flightCts.IsCancellationRequested)
            {
                _cache.SetPersonal(userId, result);
            }
        }
    }

    private void MarkPersonalFailedIfValid(Guid userId, CancellationTokenSource flightCts, TimeSpan? retryAfter)
    {
        lock (_personalFlightLock)
        {
            if (!flightCts.IsCancellationRequested)
            {
                _cache.MarkPersonalFailed(userId, retryAfter);
            }
        }
    }

    private void MarkPersonalEmptyIfValid(Guid userId, CancellationTokenSource flightCts)
    {
        lock (_personalFlightLock)
        {
            if (!flightCts.IsCancellationRequested)
            {
                _cache.MarkPersonalEmpty(userId);
            }
        }
    }

    // The live personal fetch and scoring, shared across concurrent callers for one user. Runs on a token that
    // links the flight's validity source with an overall timeout, so no single caller's cancellation aborts the
    // shared work, a hung Trakt socket cannot pin the slot past the timeout, and an invalidation cancels every
    // pending write. A non-fatal fault or the timeout degrades to null for every waiter (never a 500) and marks a
    // short failure backoff (unless the flight was cancelled, so a re-linked user is not suppressed).
    private async Task<DiscoveryResult?> FetchAndScorePersonalAsync(Guid userId, PluginConfiguration config, CancellationTokenSource flightCts)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(flightCts.Token);
        linked.CancelAfter(PersonalFetchTimeout);
        try
        {
            return await FetchAndScorePersonalCoreAsync(userId, config, flightCts, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Covers the overall-timeout cancellation and any non-fatal fault from resolve/fetch/scoring. Waiters
            // get null, not an exception; a short backoff absorbs the retry storm the failure would otherwise draw.
            // The guarded write skips the backoff if the flight was invalidated, so a re-linked user is not suppressed.
            MarkPersonalFailedIfValid(userId, flightCts, null);
            _pluginLog.LogWarning(LogSource, $"Personal fetch failed for {DescribeUser(userId)}.", ex, _logger);
            return null;
        }
    }

    private async Task<DiscoveryResult?> FetchAndScorePersonalCoreAsync(Guid userId, PluginConfiguration config, CancellationTokenSource flightCts, CancellationToken cancellationToken)
    {
        // Resolve this user's personal source from the official Trakt plugin's token (strictly read-only;
        // the sole Trakt source). Resolution is per user, not cached globally.
        var startedAt = Stopwatch.GetTimestamp();
        var source = await _personalSources.ResolveAsync(userId, config, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            // Linked at the gate but unresolvable now (a transient refresh failure): serve nothing, back off
            // briefly so the next request retries after the outage rather than hammering it.
            MarkPersonalFailedIfValid(userId, flightCts, null);
            LogPersonalOutcome(userId, startedAt, fetched: 0, final: 0, "source unresolvable at fetch time");
            return null;
        }

        var candidates = new List<ExternalDiscoveryCandidate>();
        var (movies, officialAuthFailed, retryAfter) = await FetchPersonalAsync("/recommendations/movies", MediaTypeMovie, source, TraktLimit, rankOffset: 0, userId, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(movies);

        // A revoked/stale official token fails the movies call and would fail the shows call identically (same
        // dead token, same app). Stop here instead of firing a second doomed request; only the official plugin
        // can re-link this user. A failure backoff is set below so the next request waits rather than re-hitting
        // the dead token on every tab load.
        if (officialAuthFailed)
        {
            var reason = retryAfter is { } wait
                ? $"official token auth failed, rate-limited for {(int)wait.TotalSeconds}s"
                : "official token auth failed (re-link required)";
            // A failed fetch backs off further live attempts (Retry-After when given, else the default) so a
            // dead token or a rate limit is not re-hit on every tab load.
            MarkPersonalFailedIfValid(userId, flightCts, retryAfter);
            LogPersonalOutcome(userId, startedAt, candidates.Count, final: 0, reason);
            return null;
        }

        // Re-resolve before the shows fetch so a token that went away between the two calls is caught.
        var showsSource = await _personalSources.ResolveAsync(userId, config, cancellationToken).ConfigureAwait(false);
        if (showsSource is null)
        {
            // No cache entry exists to drop here (a hit would have returned earlier), and this failing fetch
            // never writes one, so a plain failure backoff is enough.
            MarkPersonalFailedIfValid(userId, flightCts, null);
            LogPersonalOutcome(userId, startedAt, candidates.Count, final: 0, "source lost before shows fetch");
            return null;
        }

        // Continue the ranking after the movies so personal shows rank below personal movies, matching
        // the fetch order.
        var (shows, _, _) = await FetchPersonalAsync("/recommendations/shows", MediaTypeTv, showsSource, TraktLimit, candidates.Count, userId, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(shows);
        if (candidates.Count == 0)
        {
            // Trakt itself returned nothing: a stable empty result, so a short dampener absorbs duplicate tab
            // loads. The ambiguous scoring-null path below sets no backoff; only this definite emptiness does.
            MarkPersonalEmptyIfValid(userId, flightCts);
            LogPersonalOutcome(userId, startedAt, fetched: 0, final: 0, "Trakt returned no personal recommendations");
            return null;
        }

        var result = await _discoveryService.ScoreExternalCandidatesAsync(userId, candidates, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            CachePersonalIfValid(userId, flightCts, result);
            var filtered = _discoveryService.FilterConsumedItems(userId, result);
            LogPersonalOutcome(userId, startedAt, candidates.Count, filtered.Recommendations.Count, "ok");
            return filtered;
        }

        // Scoring returned nothing. This is ambiguous: it can mean every candidate was already owned, but also
        // that the user has no watch profile or Seerr is unconfigured yet. Those "not ready" states resolve on
        // their own, so this path sets no backoff. A backoff here would hide a genuine result the moment the
        // profile grows or Seerr is configured, which is exactly what broke "Trakt for you" for a fresh user.
        // The reason stays factual (no guessed cause); the Seerr Debug stage-count line carries the detail.
        LogPersonalOutcome(userId, startedAt, candidates.Count, final: 0, "scoring returned no result");
        return null;
    }

    /// <inheritdoc />
    public Task<bool> IsLinkedForAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktSourcingEnabled)
        {
            return Task.FromResult(false);
        }

        // Answer "is this user linked?" WITHOUT forcing a token refresh. A link-status check runs on every tab
        // load / status poll; refreshing just to answer a boolean would hit the network and rotate the
        // single-use refresh token. The source service reads link state cheaply (store + file read).
        return Task.FromResult(_personalSources.IsLinked(userId, config));
    }

    /// <inheritdoc />
    public void InvalidatePersonal(Guid userId)
    {
        // Cancel the in-flight fetch BEFORE dropping the cache. Cancelling first means a surviving fetch either
        // already wrote (then the cache drop below removes it) or sees the cancelled token and skips its write;
        // dropping first would leave a window where the fetch writes a stale entry after the drop but before the
        // cancel. Cancel under the lock so it never races the continuation's dispose of the same source.
        lock (_personalFlightLock)
        {
            if (_inflightPersonal.TryRemove(userId, out var flight))
            {
                flight.Cts.Cancel();
            }
        }

        _cache.InvalidatePersonal(userId);
    }

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetTrendingAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();

        // Trending is sourceable when the master switch is on AND the official Trakt plugin is present. Trending
        // needs only a client id (no bearer), so the official app id is used.
        if (config is null || !config.TraktSourcingEnabled || !_personalSources.IsAvailable(config))
        {
            return null;
        }

        // Serve each user's scored trending result from the cache; scoring replays the full
        // Seerr enrichment per candidate, so without this every tab load repeats it.
        var cached = _cache.GetTrending(userId, CacheTtl);
        if (cached is not null)
        {
            return _discoveryService.FilterConsumedItems(userId, cached);
        }

        var pool = _cache.GetTrendingPool(CacheTtl);
        if (pool is null)
        {
            var candidates = await FetchTrendingAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                _pluginLog.LogDebug(LogSource, "Trending fetch returned no candidates.", _logger);
                return null;
            }

            _cache.SetTrendingPool(candidates);
            pool = candidates;
        }

        // The raw pool is shared; the per-user scoring replays the full Seerr enrichment (~seconds), so a preload
        // and a near-simultaneous click would run it twice. Single-flight the scoring step per user so they share
        // one scoring pass. The shared pass runs on its own time-boxed token and never faults a waiter.
        var scored = await ScoreTrendingSingleFlightAsync(userId, pool, cancellationToken).ConfigureAwait(false);
        if (scored is null)
        {
            return null;
        }

        return _discoveryService.FilterConsumedItems(userId, scored);
    }

    // Single-flights the per-user trending scoring, mirroring the personal single-flight: publish/clear the slot
    // under a lock, reuse only an in-flight (not completed) task, and clear via a continuation (not a waiter's
    // finally) so a cancelling waiter cannot strand or re-spawn the shared scoring.
    private async Task<DiscoveryResult?> ScoreTrendingSingleFlightAsync(Guid userId, IReadOnlyList<ExternalDiscoveryCandidate> pool, CancellationToken cancellationToken)
    {
        Task<DiscoveryResult?> scoring;
        lock (_trendingFlightLock)
        {
            if (!_inflightTrending.TryGetValue(userId, out var existing) || existing.IsCompleted)
            {
                scoring = StartTrendingScoring(userId, pool);
                _inflightTrending[userId] = scoring;
            }
            else
            {
                scoring = existing;
            }
        }

        return await scoring.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<DiscoveryResult?> StartTrendingScoring(Guid userId, IReadOnlyList<ExternalDiscoveryCandidate> pool)
    {
        Task<DiscoveryResult?> scoring = ScoreAndCacheTrendingAsync(userId, pool);
        _ = scoring.ContinueWith(
            completed =>
            {
                lock (_trendingFlightLock)
                {
                    if (_inflightTrending.TryGetValue(userId, out var current) && ReferenceEquals(current, completed))
                    {
                        _inflightTrending.TryRemove(userId, out _);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return scoring;
    }

    // Scores the shared pool for one user and caches the ranking. Runs on its own time-boxed token so no single
    // caller's cancellation aborts the shared scoring, and a non-fatal fault or the timeout degrades to null for
    // every waiter rather than surfacing as a 500.
    private async Task<DiscoveryResult?> ScoreAndCacheTrendingAsync(Guid userId, IReadOnlyList<ExternalDiscoveryCandidate> pool)
    {
        using var timeout = new CancellationTokenSource(PersonalFetchTimeout);
        try
        {
            var scored = await _discoveryService.ScoreExternalCandidatesAsync(userId, pool, timeout.Token).ConfigureAwait(false);
            if (scored is null)
            {
                _pluginLog.LogDebug(LogSource, $"Trending scoring produced no result for {DescribeUser(userId)}.", _logger);
                return null;
            }

            // The scored ranking is cached per user and never served across users.
            _cache.SetTrending(userId, scored);
            return scored;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, $"Trending scoring failed for {DescribeUser(userId)}.", ex, _logger);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktSourcingEnabled)
        {
            // Master switch off: warm nothing. (The next live request also short-circuits, so no stale pool.)
            return;
        }

        // Run only when the official Trakt plugin is present (the sole Trakt source).
        if (!_personalSources.IsAvailable(config))
        {
            return;
        }

        // Drop the shared trending pool and every per-user trending result derived from it, so the
        // next request re-fetches a fresh pool and rescores; trending is not warmed per user here. Then
        // warm each linked user's personal cache, guarding every user so one failure never aborts the rest.
        // Dry run skips the eviction and warms personal caches only, so a later outage cannot
        // leave users worse off than before the task ran.
        if (config.RecommendationsTaskMode != TaskMode.DryRun)
        {
            _cache.InvalidateTrendingPool();
        }

        // Warm every user the official plugin holds a usable token for. The population comes from the
        // source service, which owns the foreign token store.
        var userIds = _personalSources.GetLinkedUserIds();

        var attempted = 0;
        var warmed = 0;
        foreach (var userId in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                var result = await GetPersonalAsync(userId, cancellationToken).ConfigureAwait(false);

                // Count a user as warmed only when a non-empty result was produced. A null or empty outcome
                // (backoff gate, auth failure, or genuinely empty) is not a warm, so the task summary below
                // reports honestly instead of implying every linked user now has personal picks.
                if (result?.Recommendations.Count > 0)
                {
                    warmed++;
                }
            }
            catch (Exception ex) when (!ex.IsFatal() && !cancellationToken.IsCancellationRequested)
            {
                // Any single user's failure (timeout, malformed payload, scoring error) must never abort
                // the remaining users; a requested cancellation still propagates to the caller.
                _pluginLog.LogWarning(LogSource, "Personal refresh failed for a user.", ex, _logger);
            }
        }

        // One honest summary line: how many linked users were tried and how many actually got personal picks.
        // The gap is the backoff/auth/empty cohort, so the admin sees the real state rather than a flat "done".
        _pluginLog.LogInfo(LogSource, $"Personal refresh warmed {warmed}/{attempted} linked users.", _logger);
    }

    private async Task<(List<ExternalDiscoveryCandidate> Items, bool OfficialAuthFailed, TimeSpan? RetryAfter)> FetchPersonalAsync(
        string relPath, string mediaType, TraktPersonalSource source, int limit, int rankOffset, Guid userId, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(relPath, source.ClientId, limit, source.AccessToken);
        var (items, status, retryAfter) = await SendAndReadAsync<List<TraktMediaItem>>(request, relPath, hasBearer: true, userId, cancellationToken).ConfigureAwait(false);

        // A revoked/stale official token answers 401 OR 403 ("unapproved app" when the token predates the current
        // official client id). We are read-only on it (the official plugin owns refresh/re-link), and the next
        // fetch would fail identically, so signal the caller to skip the remaining calls for this user.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ([], true, retryAfter);
        }

        var mapped = TraktMapper.MapMediaItems(items, mediaType, out var dropped, rankOffset);
        LogDropped(dropped, mediaType, "personal");
        return (mapped, false, retryAfter);
    }

    private async Task<List<ExternalDiscoveryCandidate>> FetchTrendingAsync(CancellationToken cancellationToken)
    {
        var all = new List<ExternalDiscoveryCandidate>();

        // Trending is client-id-only (no bearer). Trakt is sourced through the official plugin, so use its public
        // app id rather than an empty trakt-api-key (which Trakt rejects).
        var clientId = External.OfficialTraktPluginReader.OfficialTraktClientId;

        using (var movieReq = BuildRequest("/movies/trending", clientId, TraktLimit, accessToken: null))
        {
            var (movies, _, _) = await SendAndReadAsync<List<TraktTrendingItem>>(movieReq, "/movies/trending", hasBearer: false, userId: null, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(movies, MediaTypeMovie, out var droppedMovies));
            LogDropped(droppedMovies, MediaTypeMovie, "trending");
        }

        using (var showReq = BuildRequest("/shows/trending", clientId, TraktLimit, accessToken: null))
        {
            var (shows, _, _) = await SendAndReadAsync<List<TraktTrendingItem>>(showReq, "/shows/trending", hasBearer: false, userId: null, cancellationToken).ConfigureAwait(false);

            // Continue the ranking after the movies so trending shows rank below trending movies, matching
            // the fetch order the user sees.
            all.AddRange(TraktMapper.MapTrendingItems(shows, MediaTypeTv, out var droppedShows, all.Count));
            LogDropped(droppedShows, MediaTypeTv, "trending");
        }

        return all;
    }

    private async Task<(T? Data, HttpStatusCode Status, TimeSpan? RetryAfter)> SendAndReadAsync<T>(
        HttpRequestMessage request, string relPath, bool hasBearer, Guid? userId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient("Trakt")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = ExtractRetryAfter(response);
                LogFetchFailure(relPath, hasBearer, userId, response, retryAfter);
                return (default, response.StatusCode, retryAfter);
            }

            var json = await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<T>(json), response.StatusCode, null);
        }
        catch (Exception ex) when (!ex.IsFatal() && !cancellationToken.IsCancellationRequested)
        {
            // Network, timeout, size-limit, and malformed-payload failures degrade to an empty
            // fetch (ServiceUnavailable is never treated as Unauthorized, so the link survives).
            _pluginLog.LogWarning(LogSource, $"Trakt fetch failed ({relPath}).", ex, _logger);
            return (default, HttpStatusCode.ServiceUnavailable, null);
        }
    }

    // Reads a Retry-After header into a non-negative delay. Trakt sends a delta-seconds value on rate limits
    // (420 "enhance your calm" / 429); an HTTP-date form is resolved against the injected clock. A past date or
    // malformed value yields null so the caller falls back to its default backoff.
    private TimeSpan? ExtractRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        // RetryConditionHeaderValue always carries exactly one of Delta or Date; resolve a date against the
        // injected clock. Only a strictly positive remaining delay is a usable backoff hint.
        var delay = header?.Delta ?? (header?.Date is { } date ? date - UtcNow() : (TimeSpan?)null);
        return delay > TimeSpan.Zero ? delay : null;
    }

    // Summarizes one personal fetch outcome so an empty "Trakt for you" is explainable without enabling Debug.
    // A non-empty result is Debug (routine). An empty result logs Info only on the FIRST transition into empty
    // for a user; a repeated empty (the same user opening the tab again) stays at Debug so the empty cohort does
    // not spam the admin log on every load. A later non-empty result clears the flag so a recurrence is reported.
    private void LogPersonalOutcome(Guid userId, long startedAt, int fetched, int final, string reason)
    {
        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var message = $"Personal discovery for {DescribeUser(userId)}: {fetched} fetched → {final} final ({reason}) in {elapsedMs} ms.";

        if (final > 0)
        {
            _loggedEmptyUsers.TryRemove(userId, out _);
            _pluginLog.LogDebug(LogSource, message, _logger);
            return;
        }

        // First time this user falls into an empty outcome: surface it at Info. TryAdd returns false if the user
        // was already flagged empty, so subsequent empties for the same user stay at Debug.
        if (_loggedEmptyUsers.TryAdd(userId, 0))
        {
            _pluginLog.LogInfo(LogSource, message, _logger);
        }
        else
        {
            _pluginLog.LogDebug(LogSource, message, _logger);
        }
    }

    // Logs an HTTP failure with the endpoint, plus the VIP header for diagnosis. A 401/403 on a bearer (personal)
    // call means the official plugin's token is stale/revoked - expected, unactionable here (read-only; only the
    // official plugin can re-link), and noisy, so it is logged at DEBUG rather than as a repeated WARN burst.
    // The affected user is named so the admin knows WHO to re-link (the plugin log is admin-only). Trending (no
    // bearer) and all other failures stay WARN. No token value is ever logged.
    private void LogFetchFailure(string relPath, bool hasBearer, Guid? userId, HttpResponseMessage response, TimeSpan? retryAfter)
    {
        var code = (int)response.StatusCode;
        var authFailure = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        var detail = $"Trakt fetch failed: {code} ({relPath}).";
        if (response.Headers.TryGetValues("X-VIP-User", out var vip))
        {
            detail += $" X-VIP-User={string.Join(",", vip)}";
        }

        if (retryAfter is { } wait)
        {
            detail += $" Retry-After={(int)wait.TotalSeconds}s.";
        }

        if (authFailure && hasBearer)
        {
            var who = DescribeUser(userId);
            _pluginLog.LogDebug(LogSource, detail + $" The official Trakt plugin must re-link {who}.", _logger);
            return;
        }

        _pluginLog.LogWarning(LogSource, detail, logger: _logger);
    }

    // Resolves a user-facing identity for diagnostic logs: the username when it can be looked up, falling back to
    // the raw id (then "this user") so a missing/renamed account never throws or blanks the message.
    private string DescribeUser(Guid? userId)
    {
        if (userId is null || userId.Value.Equals(Guid.Empty))
        {
            return "this user";
        }

        try
        {
            var name = _userManager.GetUserById(userId.Value)?.Username;
            return string.IsNullOrWhiteSpace(name) ? $"user {userId.Value:N}" : $"user '{name}'";
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return $"user {userId.Value:N}";
        }
    }

    private static HttpRequestMessage BuildRequest(string relPath, string clientId, int limit, string? accessToken)
    {
        // extended=full is load-bearing: without it Trakt omits rating (mapped to 0 downstream), and the
        // minimum-vote filter would then empty the candidate pool.
        var uri = new Uri($"{TraktApi.BaseUrl}{relPath}?limit={limit}&extended=full", UriKind.Absolute);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private void LogDropped(int dropped, string mediaType, string list)
    {
        if (dropped > 0)
        {
            _pluginLog.LogDebug(LogSource, $"Dropped {dropped} {mediaType} {list} item(s) with no TMDb id.", _logger);
        }
    }

    private static PluginConfiguration? GetConfig() => Plugin.Instance?.Configuration;

    // A personal single-flight slot: the shared fetch task plus the cancellation source that represents its
    // validity. Invalidation (switch-off or unlink on a concurrent call) cancels the source, so the surviving
    // fetch sees cancellation and skips its stale cache/backoff write without depending on slot-publish timing.
    private sealed record PersonalFlight(CancellationTokenSource Cts, Task<DiscoveryResult?> Task);
}
