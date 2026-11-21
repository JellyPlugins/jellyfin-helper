using System.Collections.Immutable;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Bounded cache, stale fallback, retry throttling, configuration isolation and optional persistence.</summary>
public sealed class TitleMetadataCacheTests
{
    private readonly Clock _clock = new();
    private readonly PluginConfiguration _config = new() { SeerrUrl = "https://seerr.example.test", SeerrApiKey = "encrypted-test-key" };

    [Fact]
    public void SeparateAliasesCannotReconcileContradictoryTitlesWithoutAnExactTmdbAnchor()
    {
        _config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-sonarr" }];
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "test-sonarr");
        var cache = new TitleMetadataCache(clock: _clock);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", AnimeHint = true, FetchedAtUtc = _clock.Now }, source, null);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 999, ImdbId = "tt123456", MediaType = "tv", Overview = "different title", FetchedAtUtc = _clock.Now }, source, null);
        Assert.Null(cache.Get(_config, 0, "tv", 456, "tt123456"));
        var anchored = Assert.IsType<TitleMetadataSnapshot>(cache.Get(_config, 123, "tv", 456, "tt123456"));
        Assert.Equal(123, anchored.TmdbId);
        Assert.True(anchored.AnimeHint);
        Assert.Null(anchored.Overview);
    }

    [Theory]
    [InlineData(456)]
    [InlineData(789)]
    public void SameScopeReconciliationRejectsConflictingAliasesButRetainsCompatibleFields(int aliasTvdb)
    {
        _config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-sonarr" }];
        var scope = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "test-sonarr");
        var cache = new TitleMetadataCache(clock: _clock);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot
        {
            TmdbId = 123, TvdbId = 456, MediaType = "tv", Overview = "exact title", FetchedAtUtc = _clock.Now.AddDays(-1)
        }, scope, null);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot
        {
            ImdbId = "tt123456", TvdbId = aliasTvdb, MediaType = "tv", AnimeHint = true, FetchedAtUtc = _clock.Now
        }, scope, null);
        var result = Assert.IsType<TitleMetadataSnapshot>(cache.Get(_config, 123, "tv", imdbId: "tt123456"));
        Assert.Equal(123, result.TmdbId);
        Assert.Equal(456, result.TvdbId);
        Assert.Equal("exact title", result.Overview);
        Assert.Equal(aliasTvdb == 456 ? true : null, result.AnimeHint);
    }

    [Fact]
    public void AliasOnlyLookupCannotResolveCrossProviderIdentityConflictsBySourcePriority()
    {
        _config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-sonarr" }];
        _config.TraktSourcingEnabled = true;
        var cache = new TitleMetadataCache(clock: _clock) { TraktMetadataAvailable = _ => true };
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", AnimeHint = true, FetchedAtUtc = _clock.Now },
            new("sonarr", "https://sonarr.example.test", "test-sonarr"), null);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 999, TvdbId = 456, MediaType = "tv", OriginalLanguage = "en", FetchedAtUtc = _clock.Now },
            new("trakt", "https://api.trakt.tv", "public-metadata"), null);
        Assert.Null(cache.Get(_config, 0, "tv", 456));
        var exact = Assert.IsType<TitleMetadataSnapshot>(cache.Get(_config, 123, "tv", 456));
        Assert.Equal(123, exact.TmdbId);
        Assert.True(exact.AnimeHint);
        Assert.Null(exact.OriginalLanguage);
        Assert.Null(cache.Get(_config, 123, "movie", 456));
    }

    [Fact]
    public async Task JoiningAnOlderWriterCannotReportSuccessForANewerMetadataGeneration()
    {
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        fs.SetupGet(value => value.File).Returns(file.Object);
        fs.SetupGet(value => value.Directory).Returns(new Mock<IDirectory>().Object);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        file.Setup(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback(() =>
        {
            started.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        var cache = new TitleMetadataCache(fileSystem: fs.Object, cachePath: Path.GetFullPath("metadata-generation.json"), clock: _clock);
        cache.Store(_config, Snapshot());
        var first = cache.SaveAsync(TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            cache.Store(_config, Snapshot() with { TmdbId = 999 });
            var second = cache.SaveAsync(TestContext.Current.CancellationToken);
            release.Set();
            Assert.True(await first);
            Assert.False(await second);
            Assert.True(await cache.SaveAsync(TestContext.Current.CancellationToken));
            file.Verify(value => value.WriteAllText(It.IsAny<string>(), It.Is<string>(json => json.Contains("999", StringComparison.Ordinal))), Times.Once);
            file.Verify(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        }
        finally
        {
            release.Set();
            await first;
        }
    }

    [Fact]
    public void TypedIdsDoNotCollideAndReadsDoNotFetchOrRefresh()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        cache.Store(_config, Snapshot());
        Assert.NotNull(cache.Get(_config, 123, "movie"));
        Assert.Null(cache.Get(_config, 123, "tv"));
        Assert.Null(cache.Get(_config, 0, "movie"));
        Assert.Null(cache.Get(_config, 123, "unknown"));
        Assert.False(cache.TryBeginRefresh(_config, 123, "movie"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(8, true)]
    [InlineData(29, true)]
    [InlineData(31, true)]
    public void StaleMetadataIsRefreshableWithBoundedOutageFallback(int age, bool refresh)
    {
        var cache = new TitleMetadataCache(clock: _clock);
        cache.Store(_config, Snapshot() with { FetchedAtUtc = _clock.Now.AddDays(-age) });
        Assert.Equal(age < 30, cache.Get(_config, 123, "movie") is not null);
        Assert.Equal(refresh, cache.TryBeginRefresh(_config, 123, "movie"));
    }

    [Fact]
    public void FailedRefreshRetriesAreThrottledAndCannotEraseLastGoodMetadata()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        cache.Store(_config, Snapshot() with { FetchedAtUtc = _clock.Now.AddDays(-8) });
        Assert.True(cache.TryBeginRefresh(_config, 123, "movie"));
        Assert.False(cache.TryBeginRefresh(_config, 123, "movie"));
        Assert.NotNull(cache.Get(_config, 123, "movie"));
        _clock.Now = _clock.Now.AddMinutes(16);
        Assert.True(cache.TryBeginRefresh(_config, 123, "movie"));
        Assert.False(cache.TryBeginRefresh(_config, -1, "movie"));
        Assert.False(cache.TryBeginRefresh(_config, 123, "invalid"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderUrlAndCredentialChangesCannotReuseAnotherContext(bool url)
    {
        var cache = new TitleMetadataCache(clock: _clock);
        cache.Store(_config, Snapshot());
        if (url) _config.SeerrUrl = "https://other.example.test";
        else _config.SeerrApiKey = "different-test-key";
        Assert.Null(cache.Get(_config, 123, "movie"));
        Assert.True(cache.TryBeginRefresh(_config, 123, "movie"));
    }

    [Fact]
    public void MetadataLanguageChangesInvalidateOnlyThatProviderContext()
    {
        var serverConfig = new ServerConfiguration { PreferredMetadataLanguage = "en" };
        var manager = new Mock<IServerConfigurationManager>();
        manager.SetupGet(value => value.Configuration).Returns(serverConfig);
        var cache = new TitleMetadataCache(manager.Object, clock: _clock);
        cache.Store(_config, Snapshot());
        serverConfig.PreferredMetadataLanguage = "de";
        Assert.Null(cache.Get(_config, 123, "movie"));
    }

    [Fact]
    public void PartialResponsesPreserveKnownFieldsButExplicitEmptyKeywordsClearThem()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        cache.Store(_config, Snapshot() with { OriginalLanguage = "ja", SeriesType = "Scripted", OriginCountries = ["JP"], ProductionCountries = ["JP"], Overview = "known", GenreIds = [16], KeywordIds = [42] });
        cache.Store(_config, new TitleMetadataSnapshot { TmdbId = 123, FetchedAtUtc = _clock.Now, Keywords = [] });
        var stored = cache.Get(_config, 123, "movie")!;
        Assert.Empty(stored.Keywords!.Value);
        Assert.Equal("ja", stored.OriginalLanguage);
        Assert.Equal("Scripted", stored.SeriesType);
        Assert.Equal("JP", Assert.Single(stored.OriginCountries!.Value));
        Assert.Equal("JP", Assert.Single(stored.ProductionCountries!.Value));
        Assert.Equal("known", stored.Overview);
        Assert.Equal(16, Assert.Single(stored.GenreIds!.Value));
    }

    [Fact]
    public void InvalidFutureAndOlderSnapshotsDoNotReplaceGoodMetadata()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        cache.Store(_config, Snapshot());
        cache.Store(_config, Snapshot() with { TmdbId = 0 });
        cache.Store(_config, Snapshot() with { MediaType = "invalid" });
        cache.Store(_config, Snapshot() with { FetchedAtUtc = _clock.Now.AddDays(1), Keywords = ["wrong"] });
        cache.Store(_config, Snapshot() with { FetchedAtUtc = _clock.Now.AddDays(-1), Keywords = ["old"] });
        Assert.Equal("anime", Assert.Single(cache.Get(_config, 123, "movie")!.Keywords!.Value));
    }

    [Fact]
    public void PersistenceRoundTripsWithoutSavingCredentialsAndCorruptionIsOptional()
    {
        var fs = new MockFileSystem();
        var path = Path.GetFullPath(Path.Join("metadata-cache-tests", "titles.json"));
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        cache.Store(_config, Snapshot());
        Assert.True(cache.Save());
        Assert.DoesNotContain(_config.SeerrApiKey, fs.File.ReadAllText(path));
        Assert.NotNull(new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock).Get(_config, 123, "movie"));
        fs.File.WriteAllText(path, "not JSON");
        Assert.Null(new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock).Get(_config, 123, "movie"));
        Assert.False(new TitleMetadataCache().Save());
        Assert.False(new TitleMetadataCache(fileSystem: fs, cachePath: "relative.json").Save());
    }

    [Fact]
    public void CacheAndRetryBookkeepingAreBounded()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        for (var i = 1; i <= 4097; i++)
        {
            cache.Store(_config, Snapshot() with { TmdbId = i, FetchedAtUtc = _clock.Now.AddSeconds(-4097 + i) });
            Assert.True(cache.TryBeginRefresh(_config, i, "tv"));
        }

        Assert.Null(cache.Get(_config, 1, "movie"));
        Assert.NotNull(cache.Get(_config, 4097, "movie"));
        Assert.True(cache.TryBeginRefresh(_config, 1, "tv"));
    }

    [Fact]
    public void PersistedFutureAndExpiredMetadataAreNotRestored()
    {
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("invalid-titles.json");
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        for (var id = 1; id <= 3; id++) cache.Store(_config, Snapshot() with { TmdbId = id });
        Assert.True(cache.Save());
        var records = JsonSerializer.Deserialize<Dictionary<string, TitleMetadataSnapshot>>(fs.File.ReadAllText(path), JsonDefaults.Options)!;
        var future = records.Single(pair => pair.Value.TmdbId == 1).Key;
        var expired = records.Single(pair => pair.Value.TmdbId == 2).Key;
        records[future] = records[future] with { FetchedAtUtc = _clock.Now.AddDays(1) };
        records[expired] = records[expired] with { FetchedAtUtc = _clock.Now.AddDays(-31) };
        records["missing-record"] = null!;
        records["invalid-title"] = Snapshot() with { TmdbId = 0 };
        fs.File.WriteAllText(path, JsonSerializer.Serialize(records));
        var restored = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        Assert.Null(restored.Get(_config, 1, "movie"));
        Assert.Null(restored.Get(_config, 2, "movie"));
        Assert.NotNull(restored.Get(_config, 3, "movie"));
        // Get also filters age; Save verifies Load itself did not retain the invalid records.
        Assert.True(restored.Save());
        Assert.Equal(3, Assert.Single(JsonSerializer.Deserialize<Dictionary<string, TitleMetadataSnapshot>>(fs.File.ReadAllText(path), JsonDefaults.Options)!).Value.TmdbId);
    }

    [Fact]
    public void PublicCacheOperationsRejectNullArguments()
    {
        var cache = new TitleMetadataCache();
        Assert.Throws<ArgumentNullException>(() => cache.Get(null!, 123, "movie"));
        Assert.Throws<ArgumentNullException>(() => cache.TryBeginRefresh(null!, 123, "movie"));
        Assert.Throws<ArgumentNullException>(() => cache.Store(null!, Snapshot()));
        Assert.Throws<ArgumentNullException>(() => cache.Store(_config, null!));
    }

    [Fact]
    public void PartialResponsesCannotRenewRetainedFieldsEvenAcrossPersistence()
    {
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("partial-titles.json");
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        var acquired = _clock.Now;
        cache.Store(_config, Snapshot() with { GenreIds = [16], KeywordIds = [42], KeywordReferences = [new(42, "anime")], OriginalLanguage = "ja", SeriesType = "Scripted", OriginCountries = ["JP"], ProductionCountries = ["JP"], Overview = "old synopsis" });
        foreach (var day in new[] { 8, 16, 24 })
        {
            _clock.Now = acquired.AddDays(day);
            cache.Store(_config, new TitleMetadataSnapshot { TmdbId = 123, GenreIds = [16], FetchedAtUtc = _clock.Now });
            Assert.Equal("anime", Assert.Single(cache.Get(_config, 123, "movie")!.Keywords!.Value));
            Assert.True(cache.Save());
            cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        }

        _clock.Now = acquired.AddDays(30);
        var expiredFields = cache.Get(_config, 123, "movie")!;
        Assert.Equal(16, Assert.Single(expiredFields.GenreIds!.Value));
        Assert.Null(expiredFields.Keywords);
        Assert.Null(expiredFields.KeywordIds);
        Assert.Null(expiredFields.KeywordReferences);
        Assert.Null(expiredFields.OriginalLanguage);
        Assert.Null(expiredFields.SeriesType);
        Assert.Null(expiredFields.OriginCountries);
        Assert.Null(expiredFields.ProductionCountries);
        Assert.Null(expiredFields.Overview);
        _clock.Now = acquired.AddDays(32);
        cache.Store(_config, new TitleMetadataSnapshot { TmdbId = 123, GenreIds = [16], FetchedAtUtc = _clock.Now });
        Assert.Null(cache.Get(_config, 123, "movie")!.Keywords);
        cache.Store(_config, Snapshot() with { Keywords = ["new evidence"] });
        Assert.Equal("new evidence", Assert.Single(cache.Get(_config, 123, "movie")!.Keywords!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredTvdbPrimaryFallsThroughToTheCurrentTmdbObservation(bool onlyIdentityExpired)
    {
        _config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-sonarr" }];
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "test-sonarr");
        var cache = new TitleMetadataCache(clock: _clock);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot
        {
            TvdbId = 456, MediaType = "tv", Overview = "expired", FetchedAtUtc = onlyIdentityExpired ? _clock.Now : _clock.Now.AddDays(-31),
            FieldFetchedAtUtc = onlyIdentityExpired ? ImmutableDictionary<string, DateTime>.Empty.Add(nameof(TitleMetadataSnapshot.TvdbId), _clock.Now.AddDays(-31)) : null
        }, source, null);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", Overview = "current", FetchedAtUtc = _clock.Now }, source, null);
        Assert.Equal("current", cache.Get(_config, 0, "tv", 456)!.Overview);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 999, TvdbId = 456, MediaType = "tv", Overview = "competing", FetchedAtUtc = _clock.Now }, source, null);
        Assert.Null(cache.Get(_config, 0, "tv", 456));
        Assert.Equal("current", cache.Get(_config, 123, "tv")!.Overview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredObservationsOrIdentityFieldsDoNotKeepACurrentAliasAmbiguous(bool onlyIdentityExpires)
    {
        _config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-sonarr" }];
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "test-sonarr");
        var cache = new TitleMetadataCache(clock: _clock);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", Overview = "older", FetchedAtUtc = _clock.Now.AddDays(-20) }, source, null);
        if (onlyIdentityExpires)
        {
            cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", Overview = "fresh metadata, old link", FetchedAtUtc = _clock.Now }, source, null);
        }

        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 999, TvdbId = 456, MediaType = "tv", Overview = "current", FetchedAtUtc = _clock.Now }, source, null);
        Assert.Null(cache.Get(_config, 0, "tv", 456));
        _clock.Now = _clock.Now.AddDays(11);
        Assert.Equal("current", cache.Get(_config, 0, "tv", 456)!.Overview);
        if (onlyIdentityExpires) Assert.Equal(0, cache.Get(_config, 123, "tv")!.TvdbId);
    }

    [Fact]
    public void ACurrentAliasPrimaryCannotHideCompetingClaimsButCompatibleObservationsCanComplementIt()
    {
        _config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-sonarr" }];
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "test-sonarr");
        var cache = new TitleMetadataCache(clock: _clock);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TvdbId = 456, MediaType = "tv", SourceGenres = ["Drama"], FetchedAtUtc = _clock.Now.AddDays(-1) }, source, null);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", Overview = "current", FetchedAtUtc = _clock.Now }, source, null);
        var linked = cache.Get(_config, 0, "tv", 456)!;
        Assert.Equal(123, linked.TmdbId);
        Assert.Equal("current", linked.Overview);
        Assert.Equal("Drama", Assert.Single(linked.SourceGenres!.Value));
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 999, TvdbId = 456, MediaType = "tv", Overview = "competing", FetchedAtUtc = _clock.Now }, source, null);
        Assert.Null(cache.Get(_config, 0, "tv", 456));
        Assert.Equal("current", cache.Get(_config, 123, "tv")!.Overview);
        Assert.Equal("competing", cache.Get(_config, 999, "tv")!.Overview);
    }

    [Fact]
    public void OversizedSaveRetainsThePreviousRestartReadableFileAndTheInMemoryCache()
    {
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("oversized-titles.json");
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        cache.Store(_config, Snapshot());
        Assert.True(cache.Save());
        var previous = fs.File.ReadAllText(path);
        var overview = new string('x', 4096);
        for (var id = 1; id <= 4096; id++) cache.Store(_config, Snapshot() with { TmdbId = id, Overview = overview });
        Assert.False(cache.Save());
        Assert.Equal(previous, fs.File.ReadAllText(path));
        Assert.False(fs.File.Exists(path + ".tmp"));
        Assert.Equal(overview, cache.Get(_config, 4096, "movie")!.Overview);
        Assert.NotNull(new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock).Get(_config, 123, "movie"));
    }

    [Fact]
    public void FreshAndExplicitEmptyFieldsRenewOnlyTheirOwnAcquisitionTimes()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        var acquired = _clock.Now;
        cache.Store(_config, Snapshot() with { OriginalLanguage = "ja", Overview = "old" });
        _clock.Now = acquired.AddDays(24);
        cache.Store(_config, new TitleMetadataSnapshot { TmdbId = 123, Keywords = [], Overview = "new", FetchedAtUtc = _clock.Now });
        _clock.Now = acquired.AddDays(31);
        var snapshot = cache.Get(_config, 123, "movie")!;
        Assert.Empty(snapshot.Keywords!.Value);
        Assert.Equal("new", snapshot.Overview);
        Assert.Null(snapshot.OriginalLanguage);
        Assert.Equal(acquired.AddDays(24), snapshot.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.Keywords)]);
    }

    [Fact]
    public void RefreshReservationProgressesBeyondTheFirstBudgetEvenWithPermanentFailures()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        var titles = Enumerable.Range(1, 300).Select(id => (id, "movie")).ToArray();
        var visited = new HashSet<int>();
        for (var pass = 0; pass < 3; pass++)
        {
            var reserved = cache.ReserveRefreshes(_config, titles, 128);
            Assert.Equal(128, reserved.Length);
            visited.UnionWith(reserved.Select(title => title.TmdbId));
            foreach (var title in reserved) Assert.True(cache.DispatchRefresh(_config, title.TmdbId, title.MediaType, null));
            foreach (var title in reserved.Where(title => title.TmdbId % 2 == 0)) cache.Store(_config, Snapshot() with { TmdbId = title.TmdbId });
            cache.ReleaseRefreshes(_config, reserved, null);
            _clock.Now = _clock.Now.AddDays(8);
        }

        Assert.Equal(300, visited.Count);
    }

    [Fact]
    public void RefreshRotationAlsoReachesTitlesBeyondTheBoundedCacheAndRetryCapacity()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        var titles = Enumerable.Range(1, 4608).Select(id => (id, "movie")).ToArray();
        var visited = new HashSet<int>();
        for (var pass = 0; pass < 36; pass++)
        {
            var reserved = cache.ReserveRefreshes(_config, titles, 128);
            Assert.Equal(128, reserved.Length);
            visited.UnionWith(reserved.Select(title => title.TmdbId));
            foreach (var title in reserved) Assert.True(cache.DispatchRefresh(_config, title.TmdbId, title.MediaType, null));
            cache.ReleaseRefreshes(_config, reserved, null);
            _clock.Now = _clock.Now.AddDays(8);
        }

        Assert.Equal(titles.Length, visited.Count);
        Assert.Empty(cache.ReserveRefreshes(_config, [], 128));
        Assert.Empty(cache.ReserveRefreshes(_config, titles, 0));
    }

    [Fact]
    public void CapturedRequestLanguageCannotBeStoredUnderTheLiveLanguage()
    {
        var serverConfig = new ServerConfiguration { PreferredMetadataLanguage = "de" };
        var manager = new Mock<IServerConfigurationManager>();
        manager.SetupGet(value => value.Configuration).Returns(serverConfig);
        var cache = new TitleMetadataCache(manager.Object, clock: _clock);
        cache.Store(_config, Snapshot(), "en");
        Assert.Null(cache.Get(_config, 123, "movie"));
        Assert.True(cache.TryBeginRefresh(_config, 123, "movie"));
        serverConfig.PreferredMetadataLanguage = "en";
        Assert.NotNull(cache.Get(_config, 123, "movie"));
    }

    [Fact]
    public void UndispatchedReservationsAreReleasedWithoutCooldownWhileDispatchedFailuresKeepTheirCooldown()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        var titles = Enumerable.Range(1, 256).Select(id => (id, "movie")).ToArray();
        var first = cache.ReserveRefreshes(_config, titles, 128, "en");
        Assert.Equal(128, first.Length);
        foreach (var title in first.Take(21)) Assert.True(cache.DispatchRefresh(_config, title.TmdbId, title.MediaType, "en"));
        cache.ReleaseRefreshes(_config, first, "en");
        var next = cache.ReserveRefreshes(_config, titles, 128, "en");
        Assert.All(next, title => Assert.DoesNotContain(title.TmdbId, first.Take(21).Select(started => started.TmdbId)));
        Assert.True(cache.DispatchRefresh(_config, next[0].TmdbId, next[0].MediaType, "en"));
        cache.ReleaseRefreshes(_config, next, "en");
        var remaining = cache.ReserveRefreshes(_config, titles, 128, "en");
        Assert.Contains(remaining, title => title.TmdbId == first[21].TmdbId);
        cache.ReleaseRefreshes(_config, remaining, "en");
    }

    [Fact]
    public void CapturedReservationLanguageControlsTheAttemptRegardlessOfLiveLanguageChanges()
    {
        var serverConfiguration = new ServerConfiguration { PreferredMetadataLanguage = "en" };
        var server = new Mock<IServerConfigurationManager>();
        server.SetupGet(value => value.Configuration).Returns(serverConfiguration);
        var cache = new TitleMetadataCache(server.Object, clock: _clock);
        var reserved = cache.ReserveRefreshes(_config, [(123, "movie")], 1, "en");
        serverConfiguration.PreferredMetadataLanguage = "de";
        Assert.True(cache.DispatchRefresh(_config, 123, "movie", "en"));
        cache.ReleaseRefreshes(_config, reserved, "en");
        Assert.True(cache.TryBeginRefresh(_config, 123, "movie"));
        serverConfiguration.PreferredMetadataLanguage = "en";
        Assert.False(cache.TryBeginRefresh(_config, 123, "movie"));
    }

    [Fact]
    public void OlderPublicationReturnsOnlyTheFreshFieldsOfTheStoredWinningSnapshot()
    {
        var cache = new TitleMetadataCache(clock: _clock);
        var original = _clock.Now;
        cache.Store(_config, Snapshot());
        _clock.Now = original.AddDays(24);
        cache.Store(_config, new TitleMetadataSnapshot { TmdbId = 123, GenreIds = [16], FetchedAtUtc = _clock.Now });
        _clock.Now = original.AddDays(31);
        var returned = cache.Store(_config, new TitleMetadataSnapshot { TmdbId = 123, FetchedAtUtc = original.AddDays(20) }, null)!;
        Assert.Null(returned.Keywords);
        Assert.Equal(new[] { 16 }, returned.GenreIds!.Value);
        Assert.Equal(original.AddDays(24), returned.FetchedAtUtc);
        Assert.Null(cache.Get(_config, 123, "movie")!.Keywords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledPersistenceHasABoundedCancelableWaitAndCannotAccumulateWriters(bool cancel)
    {
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        fs.SetupGet(value => value.File).Returns(file.Object);
        fs.SetupGet(value => value.Directory).Returns(new Mock<IDirectory>().Object);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        file.Setup(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback(() =>
        {
            started.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        var waitClock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var cache = new TitleMetadataCache(fileSystem: fs.Object, cachePath: Path.GetFullPath("cancelable-titles.json"), clock: _clock)
        {
            WaitTimeProvider = cancel ? waitClock : TimeProvider.System
        };
        cache.Store(_config, Snapshot());
        using var cancellation = new CancellationTokenSource();
        var save = cache.SaveAsync(cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.False(await save.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }

            var joining = cache.SaveAsync(TestContext.Current.CancellationToken);
            if (cancel) waitClock.ExpireDeadlines();
            Assert.False(await joining.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            file.Verify(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            file.Verify(value => value.Move(It.IsAny<string>(), It.IsAny<string>(), true), Times.Never);
            Assert.NotNull(cache.Get(_config, 123, "movie"));
        }
        finally
        {
            release.Set();
            Assert.True(await cache.SaveAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void AbsentOptionalServerConfigurationUsesTheUnlocalizedProviderScope()
    {
        var manager = new Mock<IServerConfigurationManager>();
        manager.SetupGet(value => value.Configuration).Returns((ServerConfiguration)null!);
        var cache = new TitleMetadataCache(manager.Object, clock: _clock);
        cache.Store(_config, Snapshot());
        Assert.NotNull(cache.Get(_config, 123, "movie"));
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(1)]
    public void ImportedFieldTimesCannotMakeExpiredOrFutureEvidenceUsable(int acquiredDay)
    {
        var cache = new TitleMetadataCache(clock: _clock);
        var metadata = Snapshot() with
        {
            GenreIds = [16],
            OriginalLanguage = "ja",
            FieldFetchedAtUtc = ImmutableDictionary<string, DateTime>.Empty.Add(nameof(TitleMetadataSnapshot.Keywords), _clock.Now.AddDays(acquiredDay))
        };
        cache.Store(_config, metadata);
        var snapshot = cache.Get(_config, 123, "movie")!;
        Assert.Null(snapshot.Keywords);
        Assert.Equal("ja", snapshot.OriginalLanguage);
        Assert.Equal(16, Assert.Single(snapshot.GenreIds!.Value));
    }

    [Fact]
    public void ASerializedNullCacheIsTreatedAsAbsentOptionalMetadata()
    {
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("null-titles.json");
        fs.AddFile(path, "null");
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        Assert.Null(cache.Get(_config, 123, "movie"));
        Assert.True(cache.Save());
        Assert.Empty(JsonSerializer.Deserialize<Dictionary<string, TitleMetadataSnapshot>>(fs.File.ReadAllText(path), JsonDefaults.Options)!);
    }

    [Fact]
    public void PersistenceFailureCannotRemoveTheInMemoryFallback()
    {
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        var directory = new Mock<IDirectory>();
        fs.SetupGet(value => value.File).Returns(file.Object);
        fs.SetupGet(value => value.Directory).Returns(directory.Object);
        file.Setup(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("read-only disk"));
        var cache = new TitleMetadataCache(fileSystem: fs.Object, cachePath: Path.GetFullPath("titles.json"), clock: _clock);
        cache.Store(_config, Snapshot());
        Assert.False(cache.Save());
        Assert.NotNull(cache.Get(_config, 123, "movie"));
    }

    [Fact]
    public async Task SlowPersistenceDoesNotHoldTheReadLockOrBlockRequests()
    {
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        fs.SetupGet(value => value.File).Returns(file.Object);
        fs.SetupGet(value => value.Directory).Returns(new Mock<IDirectory>().Object);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        file.Setup(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback(() =>
        {
            writing.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        var cache = new TitleMetadataCache(fileSystem: fs.Object, cachePath: Path.GetFullPath("titles.json"), clock: _clock);
        cache.Store(_config, Snapshot());
        var save = Task.Factory.StartNew(cache.Save, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var read = Task.Factory.StartNew(() => cache.Get(_config, 123, "movie"), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.NotNull(await read.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            await save.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    private TitleMetadataSnapshot Snapshot() => new() { TmdbId = 123, Keywords = ["anime"], FetchedAtUtc = _clock.Now };

    [Fact]
    public void ReplacingAnEntryRemovesOnlyItsAliasesAndPreservesOtherEntriesClaimingTheSameAlias()
    {
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("reverse-aliases.json");
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        cache.Store(_config, Snapshot() with { MediaType = "tv", TvdbId = 456, ImdbId = "tt123456", Overview = "first" });
        cache.Store(_config, Snapshot() with { TmdbId = 999, MediaType = "tv", TvdbId = 456, ImdbId = "tt123456", Overview = "second" });
        Assert.True(cache.Save());
        // The reverse index must also be complete after loading an existing file.
        cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        Assert.Null(cache.Get(_config, 0, "tv", 456));
        Assert.Null(cache.Get(_config, 0, "tv", imdbId: "tt123456"));
        _clock.Now = _clock.Now.AddMinutes(1);
        cache.Store(_config, Snapshot() with { MediaType = "tv", TvdbId = 789, ImdbId = "tt654321", Overview = "corrected" });
        Assert.Equal(999, cache.Get(_config, 0, "tv", 456)!.TmdbId);
        Assert.Equal(999, cache.Get(_config, 0, "tv", imdbId: "tt123456")!.TmdbId);
        Assert.Equal(123, cache.Get(_config, 0, "tv", 789)!.TmdbId);
        Assert.Equal("corrected", cache.Get(_config, 0, "tv", imdbId: "tt654321")!.Overview);
        Assert.True(cache.Save());
        Assert.Equal(999, new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock).Get(_config, 0, "tv", 456)!.TmdbId);
    }

    [Fact]
    public void ExpiredOversizedMetadataDoesNotPreventSavingOneRefreshedTitle()
    {
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("expired-oversized-titles.json");
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        var acquired = _clock.Now;
        var overview = new string('x', 4096);
        for (var id = 1; id <= 4096; id++) cache.Store(_config, Snapshot() with { TmdbId = id, Overview = overview });
        Assert.False(cache.Save());
        _clock.Now = acquired.AddDays(31);
        cache.Store(_config, Snapshot() with { TmdbId = 4096, Overview = "fresh" });
        Assert.True(cache.Save());
        var stored = Assert.Single(JsonSerializer.Deserialize<Dictionary<string, TitleMetadataSnapshot>>(fs.File.ReadAllText(path), JsonDefaults.Options)!);
        Assert.Equal(4096, stored.Value.TmdbId);
        Assert.Equal("fresh", stored.Value.Overview);
        Assert.Equal(_clock.Now, stored.Value.FetchedAtUtc);
        var restored = new TitleMetadataCache(fileSystem: fs, cachePath: path, clock: _clock);
        Assert.Null(restored.Get(_config, 1, "movie"));
        Assert.Equal("fresh", restored.Get(_config, 4096, "movie")!.Overview);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SourceReconciliationPreservesOrRenewsTheSelectedTvdbFieldsAcquisition(bool renewed, bool newerImdbObservation)
    {
        _config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-sonarr" }];
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "test-sonarr");
        var cache = new TitleMetadataCache(clock: _clock);
        var acquired = _clock.Now;
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", FetchedAtUtc = acquired }, source, null);
        _clock.Now = acquired.AddDays(20);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = renewed ? 456 : 0, MediaType = "tv", Overview = "partial", FetchedAtUtc = _clock.Now }, source, null);
        if (newerImdbObservation) _clock.Now = _clock.Now.AddDays(1);
        cache.StoreSupplemental(_config, new TitleMetadataSnapshot { ImdbId = "tt123456", MediaType = "tv", FetchedAtUtc = _clock.Now }, source, null);
        var result = cache.Get(_config, 123, "tv", imdbId: "tt123456")!;
        Assert.Equal(456, result.TvdbId);
        Assert.Equal(renewed ? acquired.AddDays(20) : acquired, result.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.TvdbId)]);
        _clock.Now = acquired.AddDays(31);
        Assert.Equal(renewed ? 456 : 0, cache.Get(_config, 123, "tv", imdbId: "tt123456")!.TvdbId);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTime Now { get; set; } = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
