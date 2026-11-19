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
            foreach (var title in reserved.Where(title => title.TmdbId % 2 == 0)) cache.Store(_config, Snapshot() with { TmdbId = title.TmdbId });
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

    private sealed class Clock : TimeProvider
    {
        internal DateTime Now { get; set; } = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
