using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the Trakt cache: per-user personal and global trending get/set, TTL expiry, invalidation, and
///     per-user isolation, all driven by a deterministic clock.
/// </summary>
public sealed class TraktCacheServiceTests
{
    private readonly TimeSpan _ttl = TimeSpan.FromHours(1);
    private DateTime _now = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private TraktCacheService Create() => new(() => _now);

    private static DiscoveryResult Result(Guid userId) => new() { UserId = userId };

    private static IReadOnlyList<ExternalDiscoveryCandidate> Pool() =>
    [
        new ExternalDiscoveryCandidate { TmdbId = 11, MediaType = "movie", Title = "Alpha" },
    ];

    [Fact]
    public void Personal_SetThenGet_ReturnsEntryWhenFresh()
    {
        var cache = Create();
        var userId = Guid.NewGuid();
        var result = Result(userId);

        cache.SetPersonal(userId, result);

        Assert.Same(result, cache.GetPersonal(userId, _ttl));
    }

    [Fact]
    public void Personal_ReturnsNull_WhenStale()
    {
        var cache = Create();
        var userId = Guid.NewGuid();
        cache.SetPersonal(userId, Result(userId));

        _now = _now.Add(_ttl).AddSeconds(1);

        Assert.Null(cache.GetPersonal(userId, _ttl));
    }

    [Fact]
    public void Personal_Miss_ReturnsNull()
    {
        Assert.Null(Create().GetPersonal(Guid.NewGuid(), _ttl));
    }

    [Fact]
    public void Personal_IsIsolatedPerUser()
    {
        var cache = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cache.SetPersonal(a, Result(a));

        Assert.NotNull(cache.GetPersonal(a, _ttl));
        Assert.Null(cache.GetPersonal(b, _ttl));
    }

    [Fact]
    public void Personal_Invalidate_DropsEntry()
    {
        var cache = Create();
        var userId = Guid.NewGuid();
        cache.SetPersonal(userId, Result(userId));

        cache.InvalidatePersonal(userId);

        Assert.Null(cache.GetPersonal(userId, _ttl));
    }

    [Fact]
    public void Trending_SetThenGet_ReturnsRawPoolWhenFresh()
    {
        var cache = Create();
        var pool = Pool();
        cache.SetTrendingPool(pool);
        Assert.Same(pool, cache.GetTrendingPool(_ttl));
    }

    [Fact]
    public void Trending_ReturnsNull_WhenStale()
    {
        var cache = Create();
        cache.SetTrendingPool(Pool());
        _now = _now.Add(_ttl).AddSeconds(1);
        Assert.Null(cache.GetTrendingPool(_ttl));
    }

    [Fact]
    public void Trending_Invalidate_DropsEntry()
    {
        var cache = Create();
        cache.SetTrendingPool(Pool());
        cache.InvalidateTrendingPool();
        Assert.Null(cache.GetTrendingPool(_ttl));
    }
}
