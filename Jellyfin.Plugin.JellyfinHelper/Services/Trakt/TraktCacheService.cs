using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     In-memory cache for Trakt discovery results: personal recommendations keyed per user and a single
///     global trending list shared across users. Entries carry a TTL so a request falls back to a live fetch
///     once stale; writes replace the entry. The cache is best-effort and process-local, mirroring how the
///     scheduled task warms it and request-time lazily refreshes it.
/// </summary>
public sealed class TraktCacheService
{
    private readonly ConcurrentDictionary<Guid, CacheEntry> _personal = new();
    private readonly Func<DateTime> _utcNow;
    private CacheEntry? _trending;

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
        _personal[userId] = new CacheEntry(result, _utcNow());
    }

    /// <summary>
    ///     Returns the global trending result when present and not past <paramref name="ttl"/>, else null.
    /// </summary>
    /// <param name="ttl">The maximum age a cached entry may have to be served.</param>
    /// <returns>The cached trending result, or null on miss or stale.</returns>
    public DiscoveryResult? GetTrending(TimeSpan ttl)
    {
        var entry = _trending;
        return entry is not null && !IsStale(entry, ttl) ? entry.Result : null;
    }

    /// <summary>
    ///     Stores (or replaces) the global trending result with the current timestamp.
    /// </summary>
    /// <param name="result">The result to cache.</param>
    public void SetTrending(DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _trending = new CacheEntry(result, _utcNow());
    }

    /// <summary>
    ///     Drops a user's cached personal entry (e.g. on disconnect or a mutation that invalidates it).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    public void InvalidatePersonal(Guid userId) => _personal.TryRemove(userId, out _);

    /// <summary>
    ///     Drops the cached global trending entry.
    /// </summary>
    public void InvalidateTrending() => _trending = null;

    private bool IsStale(CacheEntry entry, TimeSpan ttl) => _utcNow() - entry.StoredAtUtc > ttl;

    private sealed record CacheEntry(DiscoveryResult Result, DateTime StoredAtUtc);
}
