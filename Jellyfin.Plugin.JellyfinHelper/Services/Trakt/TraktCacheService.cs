using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Personal results per user, per-user trending results, plus one global trending candidate pool.
///     The pool stays raw (unscored, unfiltered) so every user scores the full set with their own
///     filters; a single shared ranking would shrink it to the first user's top-N for everyone else.
///     Best-effort and process-local; the task warms it, requests refresh it lazily.
/// </summary>
public sealed class TraktCacheService
{
    /// <summary>Default backoff for a failed personal fetch when the server gave no Retry-After.</summary>
    public static readonly TimeSpan DefaultFailureBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Short dampener for a stable empty personal result (Trakt itself returned nothing), enough to
    /// absorb duplicate tab loads without hiding a result that only appears once the user's data changes.</summary>
    public static readonly TimeSpan EmptyBackoff = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, CacheEntry<DiscoveryResult>> _personal = new();
    private readonly ConcurrentDictionary<Guid, CacheEntry<DiscoveryResult>> _trendingScored = new();

    // Negative cache: the UTC instant until which a user's personal fetch should not be retried. A failure
    // backs off (so a rate-limited or down Trakt is not hammered every tab load); a legitimately empty result
    // backs off only briefly (just to absorb duplicate loads) so a genuine first result appears fast.
    private readonly ConcurrentDictionary<Guid, DateTime> _personalBackoffUntil = new();

    // Per-user generation counter, bumped on every invalidation. A shared fetch captures the generation at start
    // and may only write its result if the generation is unchanged, so a fetch that was in flight when a mutation
    // invalidated the user never resurrects the pre-mutation result into the cache.
    private readonly ConcurrentDictionary<Guid, int> _personalGeneration = new();
    private readonly Func<DateTime> _utcNow;

    // volatile so a set/invalidate on one thread is observed by a concurrent reader without a lock. The
    // reference assignment is already atomic; volatile only forbids a stale cached read, which is all this
    // best-effort pool needs - a full lock would be overkill for process-local cache state.
    private volatile CacheEntry<IReadOnlyList<ExternalDiscoveryCandidate>>? _trending;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktCacheService"/> class.
    /// </summary>
    /// <param name="utcNow">Clock seam for deterministic TTL handling in tests; defaults to UTC now.</param>
    public TraktCacheService(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    ///     Returns a user's cached personal result when present and not past <paramref name="ttl"/>, else null.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ttl">The maximum age a cached entry may have to be served.</param>
    /// <returns>The cached result, or null on miss or stale.</returns>
    public DiscoveryResult? GetPersonal(Guid userId, TimeSpan ttl)
    {
        if (_personal.TryGetValue(userId, out var entry) && !IsStale(entry, ttl))
        {
            return entry.Result;
        }

        return null;
    }

    /// <summary>
    ///     Stores (or replaces) a user's personal result with the current timestamp.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="result">The result to cache.</param>
    public void SetPersonal(Guid userId, DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _personal[userId] = new CacheEntry<DiscoveryResult>(result, _utcNow());

        // A fresh positive result clears any backoff so the user is served immediately rather than waiting it out.
        _personalBackoffUntil.TryRemove(userId, out _);
    }

    /// <summary>
    ///     Returns the current personal generation for a user, to be captured before a shared fetch starts and
    ///     passed back to <see cref="SetPersonal(Guid, DiscoveryResult, int)"/> so a fetch that raced an
    ///     invalidation does not write a stale result.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The current generation value.</returns>
    public int PersonalGeneration(Guid userId) => _personalGeneration.TryGetValue(userId, out var g) ? g : 0;

    /// <summary>
    ///     Stores a user's personal result only if the generation captured at fetch start still matches the
    ///     current generation. A fetch that was in flight across an invalidation is dropped instead of caching a
    ///     stale pre-invalidation result.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="result">The result to cache.</param>
    /// <param name="generation">The generation captured before the fetch started.</param>
    /// <returns><see langword="true"/> if the result was cached; otherwise <see langword="false"/>.</returns>
    public bool SetPersonal(Guid userId, DiscoveryResult result, int generation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (PersonalGeneration(userId) != generation)
        {
            return false;
        }

        SetPersonal(userId, result);
        return true;
    }

    /// <summary>
    ///     Suppresses personal fetches for this user until the backoff elapses, after a failed fetch. The delay is
    ///     the server's Retry-After when supplied, otherwise <see cref="DefaultFailureBackoff"/>.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="retryAfter">The server-requested backoff, if any.</param>
    public void MarkPersonalFailed(Guid userId, TimeSpan? retryAfter)
    {
        var delay = retryAfter is { } wait && wait > TimeSpan.Zero ? wait : DefaultFailureBackoff;
        _personalBackoffUntil[userId] = _utcNow() + delay;
    }

    /// <summary>
    ///     Applies a short backoff after a legitimately empty personal result, long enough to absorb duplicate tab
    ///     loads but short enough that a genuine first result is not hidden for long.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    public void MarkPersonalEmpty(Guid userId) => _personalBackoffUntil[userId] = _utcNow() + EmptyBackoff;

    /// <summary>
    ///     Returns whether a personal fetch for this user is currently suppressed by an active backoff.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns><see langword="true"/> while the backoff is active; otherwise <see langword="false"/>.</returns>
    public bool IsPersonalBackoffActive(Guid userId)
    {
        if (_personalBackoffUntil.TryGetValue(userId, out var until))
        {
            if (_utcNow() < until)
            {
                return true;
            }

            // Expired: drop it so the dictionary does not accumulate stale per-user entries.
            _personalBackoffUntil.TryRemove(userId, out _);
        }

        return false;
    }

    /// <summary>
    ///     Returns the global raw trending candidate pool when present and not past <paramref name="ttl"/>,
    ///     else null. Callers always score the pool for the requesting user; never serve it as a result.
    /// </summary>
    /// <param name="ttl">The maximum age a cached entry may have to be served.</param>
    /// <returns>The cached candidate pool, or null on miss or stale.</returns>
    public IReadOnlyList<ExternalDiscoveryCandidate>? GetTrendingPool(TimeSpan ttl)
    {
        var entry = _trending;
        return entry is not null && !IsStale(entry, ttl) ? entry.Result : null;
    }

    /// <summary>
    ///     Stores (or replaces) the global raw trending candidate pool with the current timestamp.
    /// </summary>
    /// <param name="pool">The unscored candidate pool to cache.</param>
    public void SetTrendingPool(IReadOnlyList<ExternalDiscoveryCandidate> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _trending = new CacheEntry<IReadOnlyList<ExternalDiscoveryCandidate>>(pool, _utcNow());
    }

    /// <summary>
    ///     Returns a user's cached trending result when present and not past <paramref name="ttl"/>, else null.
    ///     Callers filter the served copy for consumed items; the stored entry stays unfiltered.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ttl">The maximum age a cached entry may have to be served.</param>
    /// <returns>The cached result, or null on miss or stale.</returns>
    public DiscoveryResult? GetTrending(Guid userId, TimeSpan ttl)
    {
        if (_trendingScored.TryGetValue(userId, out var entry) && !IsStale(entry, ttl))
        {
            return entry.Result;
        }

        return null;
    }

    /// <summary>
    ///     Stores (or replaces) a user's trending result with the current timestamp.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="result">The result to cache.</param>
    public void SetTrending(Guid userId, DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _trendingScored[userId] = new CacheEntry<DiscoveryResult>(result, _utcNow());
    }

    /// <summary>
    ///     Drops a user's cached personal entry and backoff, and bumps the generation so any in-flight fetch
    ///     that started before this call cannot write its now-stale result (e.g. on disconnect or a mutation).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    public void InvalidatePersonal(Guid userId)
    {
        _personal.TryRemove(userId, out _);

        // Clear the backoff so a request/dismiss mutation does not leave the user served nothing for up to the
        // failure window, and bump the generation so a racing in-flight fetch is barred from caching a result.
        _personalBackoffUntil.TryRemove(userId, out _);
        _personalGeneration.AddOrUpdate(userId, 1, (_, current) => current + 1);
    }

    /// <summary>
    ///     Drops the cached global trending pool along with every per-user trending result derived from it.
    /// </summary>
    public void InvalidateTrendingPool()
    {
        _trending = null;
        _trendingScored.Clear();
    }

    private bool IsStale<T>(CacheEntry<T> entry, TimeSpan ttl) => _utcNow() - entry.StoredAtUtc > ttl;

    private sealed record CacheEntry<T>(T Result, DateTime StoredAtUtc);
}
