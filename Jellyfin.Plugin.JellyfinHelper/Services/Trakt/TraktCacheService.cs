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
    private readonly ConcurrentDictionary<Guid, CacheEntry<DiscoveryResult>> _personal = new();
    private readonly ConcurrentDictionary<Guid, CacheEntry<DiscoveryResult>> _trendingScored = new();
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
    ///     Drops a user's cached personal entry (e.g. on disconnect or a mutation that invalidates it).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    public void InvalidatePersonal(Guid userId) => _personal.TryRemove(userId, out _);

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
