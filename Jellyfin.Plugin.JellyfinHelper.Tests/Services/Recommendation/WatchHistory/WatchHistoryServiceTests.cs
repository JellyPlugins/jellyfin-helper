using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

public sealed class WatchHistoryServiceTests
{
    private static readonly string[] ParentSeriesGenres = ["Sci-Fi", "Drama"];
    private static readonly string[] OwnEpisodeGenres = ["Comedy"];

    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IPluginConfigurationService> _mockConfigService;
    private readonly Mock<IUserManager> _mockUserManager;
    private readonly Mock<IUserDataManager> _mockUserDataManager;
    private readonly Mock<IPluginLogService> _mockPluginLog;
    private readonly Mock<ILogger<WatchHistoryService>> _mockLogger;
    private readonly WatchHistoryService _service;

    public WatchHistoryServiceTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockConfigService = new Mock<IPluginConfigurationService>();
        _mockConfigService.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration());
        _mockUserManager = new Mock<IUserManager>();
        _mockUserDataManager = new Mock<IUserDataManager>();
        _mockPluginLog = new Mock<IPluginLogService>();
        _mockLogger = new Mock<ILogger<WatchHistoryService>>();
        _service = new WatchHistoryService(
            _mockLibraryManager.Object,
            _mockConfigService.Object,
            _mockUserManager.Object,
            _mockUserDataManager.Object,
            _mockPluginLog.Object,
            _mockLogger.Object);
    }

    [Fact]
    public void GetUserWatchProfile_UserNotFound_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _mockUserManager.Setup(m => m.GetUserById(userId)).Returns((Jellyfin.Database.Implementations.Entities.User?)null);
        Assert.Null(_service.GetUserWatchProfile(userId));
    }

    [Fact]
    public void GetAllUserWatchProfiles_NoUsers_ReturnsEmptyCollection()
    {
        _mockUserManager.Setup(m => m.GetUsers()).Returns(Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>());
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());
        var result = _service.GetAllUserWatchProfiles();
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllUserWatchProfiles_ExceptionInBuildProfile_SkipsUserAndContinues()
    {
        var user1 = CreateTestUser("alice");
        var user2 = CreateTestUser("bob-throws");
        var user3 = CreateTestUser("charlie");
        _mockUserManager.Setup(m => m.GetUsers()).Returns(new[] { user1, user2, user3 }.AsQueryable());
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Test Movie" };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });
        _mockUserDataManager
            .Setup(m => m.GetUserData(It.Is<Jellyfin.Database.Implementations.Entities.User>(u => u.Username == "bob-throws"), It.IsAny<BaseItem>()))
            .Throws(new InvalidOperationException("Simulated failure for bob-throws"));
        _mockUserDataManager
            .Setup(m => m.GetUserData(It.Is<Jellyfin.Database.Implementations.Entities.User>(u => u.Username != "bob-throws"), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);
        var result = _service.GetAllUserWatchProfiles();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.UserName == "alice");
        Assert.Contains(result, p => p.UserName == "charlie");
        Assert.DoesNotContain(result, p => p.UserName == "bob-throws");
    }

    [Fact]
    public void GetAllUserWatchProfiles_ReturnsProfilesForAllValidUsers()
    {
        var user1 = CreateTestUser("alice");
        var user2 = CreateTestUser("bob");
        _mockUserManager.Setup(m => m.GetUsers()).Returns(new[] { user1, user2 }.AsQueryable());
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());
        _mockUserDataManager.Setup(m => m.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>())).Returns((UserItemData?)null);
        var result = _service.GetAllUserWatchProfiles();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.UserName == "alice");
        Assert.Contains(result, p => p.UserName == "bob");
    }

    [Fact]
    public void LoadAllVideoItems_DelegatesWithVideoMediaType()
    {
        InternalItemsQuery? capturedQuery = null;
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());
        _service.LoadAllVideoItems();
        Assert.NotNull(capturedQuery);
        Assert.Contains(MediaType.Video, capturedQuery!.MediaTypes);
        Assert.Equal(false, capturedQuery.IsFolder);
    }

    [Fact]
    public void BuildProfile_MoviePlayed_IncrementsMovieCount()
    {
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Test Movie",
            Genres = new[] { "Action", "Thriller" },
            CommunityRating = 7.5f,
            ProductionYear = 2023,
            RunTimeTicks = TimeSpan.FromMinutes(120).Ticks
        };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "movie-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });
        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.WatchedMovieCount);
        Assert.Equal(0, profile.WatchedEpisodeCount);
        Assert.Equal(0, profile.WatchedSeriesCount);
        Assert.Single(profile.WatchedItems);
        Assert.Equal("Test Movie", profile.WatchedItems[0].Name);
        Assert.True(profile.TotalWatchTimeTicks > 0);
    }

    [Fact]
    public void BuildProfile_EpisodesFromSameSeries_CountsSeriesOnce()
    {
        var user = CreateTestUser("bob");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var seriesId = Guid.NewGuid();
        var ep1 = new Episode { Id = Guid.NewGuid(), Name = "Episode 1", SeriesId = seriesId, Genres = new[] { "Drama" }, RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };
        var ep2 = new Episode { Id = Guid.NewGuid(), Name = "Episode 2", SeriesId = seriesId, Genres = new[] { "Drama" }, RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { ep1, ep2 });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "episode-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });
        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Equal(2, profile!.WatchedEpisodeCount);
        Assert.Equal(1, profile.WatchedSeriesCount);
        Assert.Equal(0, profile.WatchedMovieCount);
    }

    [Fact]
    public void BuildProfile_EpisodeWithoutGenres_InheritsParentSeriesGenres()
    {
        // Episodes usually carry no genres of their own; the watched item must inherit the parent
        // series' genres so episode-based genre signals are not empty. Exercises the inheritance branch.
        var user = CreateTestUser("erin");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Genreless Episode", SeriesId = seriesId, Genres = System.Array.Empty<string>(), RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };
        var series = new Series { Id = seriesId, Name = "Parent Show", Genres = ParentSeriesGenres };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode })
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "ep-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        var watched = Assert.Single(profile!.WatchedItems, w => w.ItemId == episode.Id);
        Assert.Equal(ParentSeriesGenres, watched.Genres);
    }

    [Fact]
    public void BuildProfile_EpisodeWithOwnGenres_DoesNotInheritSeriesGenres()
    {
        // The inheritance branch is guarded by genres.Count == 0: an episode that has its own genres
        // must keep them and ignore the parent series' genres.
        var user = CreateTestUser("frank");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Own Genre Episode", SeriesId = seriesId, Genres = OwnEpisodeGenres, RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };
        var series = new Series { Id = seriesId, Name = "Parent Show", Genres = ParentSeriesGenres };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode })
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "ep-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        var watched = Assert.Single(profile!.WatchedItems, w => w.ItemId == episode.Id);
        Assert.Equal(OwnEpisodeGenres, watched.Genres);
    }

    [Fact]
    public void BuildProfile_WatchedEpisode_ResolvesParentSeriesTmdbId()
    {
        // A watched episode must carry its parent series' TMDb id (episodes rarely have a useful id of
        // their own), so recommendations can exclude a duplicate series entry of a partially watched show.
        var user = CreateTestUser("grace");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), Name = "S01E01", SeriesId = seriesId, Genres = OwnEpisodeGenres, RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };
        var series = new Series { Id = seriesId, Name = "Parent Show", Genres = ParentSeriesGenres };
        series.ProviderIds["Tmdb"] = "1399";
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode })
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "ep-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        var watched = Assert.Single(profile!.WatchedItems, w => w.ItemId == episode.Id);
        Assert.Equal(1399, watched.SeriesTmdbId);
    }

    [Fact]
    public void BuildProfile_WatchedEpisode_ParentSeriesWithoutTmdb_LeavesSeriesTmdbIdZero()
    {
        // When the parent series carries no TMDb id, SeriesTmdbId stays 0 so no (0, "tv") key is built.
        var user = CreateTestUser("heidi");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), Name = "S01E01", SeriesId = seriesId, Genres = OwnEpisodeGenres, RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };
        var series = new Series { Id = seriesId, Name = "Untagged Show", Genres = ParentSeriesGenres };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode })
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "ep-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        var watched = Assert.Single(profile!.WatchedItems, w => w.ItemId == episode.Id);
        Assert.Equal(0, watched.SeriesTmdbId);
    }

    [Fact]
    public void BuildProfile_WatchedMovie_LeavesSeriesTmdbIdZero()
    {
        // A movie is not an episode, so it never resolves a parent-series TMDb id.
        var user = CreateTestUser("ida");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Solo Film", Genres = OwnEpisodeGenres, RunTimeTicks = TimeSpan.FromMinutes(100).Ticks };
        movie.ProviderIds["Tmdb"] = "550";
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie })
            .Returns(new List<BaseItem>());
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "mv-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        var watched = Assert.Single(profile!.WatchedItems, w => w.ItemId == movie.Id);
        Assert.Equal(0, watched.SeriesTmdbId);
        Assert.Equal(550, watched.TmdbId);
    }

    [Fact]
    public void BuildProfile_GenreDistribution_CountsCorrectly()
    {
        var user = CreateTestUser("charlie");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie1 = new Movie { Id = Guid.NewGuid(), Name = "Action Movie", Genres = new[] { "Action", "Thriller" } };
        var movie2 = new Movie { Id = Guid.NewGuid(), Name = "Action Comedy", Genres = new[] { "Action", "Comedy" } };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie1, movie2 });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "genre-key", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow });
        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Equal(2, profile!.GenreDistribution["Action"]);
        Assert.Equal(1, profile.GenreDistribution["Thriller"]);
        Assert.Equal(1, profile.GenreDistribution["Comedy"]);
    }

    [Fact]
    public void BuildProfile_UnplayedItems_AreExcluded()
    {
        var user = CreateTestUser("dave");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Unwatched Movie", Genres = new[] { "Horror" } };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "unplayed-key", Played = false, PlayCount = 0, PlaybackPositionTicks = 0, IsFavorite = false });
        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Empty(profile!.WatchedItems);
        Assert.Equal(0, profile.WatchedMovieCount);
    }

    // The three tests below explicitly stub GetUserDataBatch so LookupUserData exercises the "valid batch present" branch (lookup is not null) rather than falling back to per-item GetUserData via Moq's implicit null default.

    [Fact]
    public void BuildProfile_FavoriteSeries_AddsSyntheticWatchedItemAndFavoriteId()
    {
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Fav Show",
            Genres = new[] { "Sci-Fi", "Drama" },
            ProductionYear = 2020,
            CommunityRating = 8.5f
        };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>())
            .Returns(new List<BaseItem> { series });
        var seriesUserData = new UserItemData { Key = "series-fav-key", Played = false, PlayCount = 0, IsFavorite = true };

        // Populate the batch dict with the series' UserData under its Id key so the
        // "valid batch" branch of LookupUserData is exercised.
        _mockUserDataManager
            .Setup(m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns((IReadOnlyList<BaseItem> items, Jellyfin.Database.Implementations.Entities.User _) =>
            {
                var dict = new Dictionary<Guid, UserItemData>();
                if (items.Any(i => i.Id == series.Id))
                {
                    dict[series.Id] = seriesUserData;
                }
                return dict;
            });
        // Defence-in-depth: if the batch path ever regresses the fallback still returns real data.
        _mockUserDataManager.Setup(m => m.GetUserData(user, series)).Returns(seriesUserData);

        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Contains(series.Id, profile!.FavoriteSeriesIds);
        Assert.Equal(1, profile.FavoriteCount);
        Assert.Contains(profile.WatchedItems, w => w.ItemId == series.Id && w.IsFavorite);
        Assert.Equal(1, profile.GenreDistribution["Sci-Fi"]);
        Assert.Equal(1, profile.GenreDistribution["Drama"]);
        _mockUserDataManager.Verify(
            m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user),
            Times.AtLeastOnce);
    }

    [Fact]
    public void BuildProfile_FavoriteSeries_SeedsSeriesTmdbIdOnSyntheticItem()
    {
        // A favorited-but-unwatched series must carry its own TMDb id so a duplicate library entry
        // of the same show (a second copy with a different Jellyfin Guid) is excluded by the
        // provider-id fallback, the same way a watched series contributes its key.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        const int seriesTmdbId = 1396;
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Fav Show",
            Genres = new[] { "Sci-Fi", "Drama" },
            ProductionYear = 2020,
            CommunityRating = 8.5f,
            ProviderIds = new Dictionary<string, string>
            {
                ["Tmdb"] = seriesTmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>())
            .Returns(new List<BaseItem> { series });
        var seriesUserData = new UserItemData { Key = "series-fav-key", Played = false, PlayCount = 0, IsFavorite = true };
        _mockUserDataManager
            .Setup(m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns((IReadOnlyList<BaseItem> items, Jellyfin.Database.Implementations.Entities.User _) =>
            {
                var dict = new Dictionary<Guid, UserItemData>();
                if (items.Any(i => i.Id == series.Id))
                {
                    dict[series.Id] = seriesUserData;
                }
                return dict;
            });
        _mockUserDataManager.Setup(m => m.GetUserData(user, series)).Returns(seriesUserData);

        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        var synthetic = Assert.Single(profile!.WatchedItems, w => w.ItemId == series.Id);
        Assert.Equal(seriesTmdbId, synthetic.TmdbId);
        Assert.Equal(seriesTmdbId, synthetic.SeriesTmdbId);
    }

    [Fact]
    public void BuildProfile_NonFavoriteSeries_IsIgnored()
    {
        var user = CreateTestUser("bob");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var series = new Series { Id = Guid.NewGuid(), Name = "Just A Show", Genres = new[] { "Comedy" } };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>())
            .Returns(new List<BaseItem> { series });
        var seriesUserData = new UserItemData { Key = "series-key", Played = false, PlayCount = 0, IsFavorite = false };

        _mockUserDataManager
            .Setup(m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns((IReadOnlyList<BaseItem> items, Jellyfin.Database.Implementations.Entities.User _) =>
            {
                var dict = new Dictionary<Guid, UserItemData>();
                if (items.Any(i => i.Id == series.Id))
                {
                    dict[series.Id] = seriesUserData;
                }
                return dict;
            });
        _mockUserDataManager.Setup(m => m.GetUserData(user, series)).Returns(seriesUserData);

        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Empty(profile!.FavoriteSeriesIds);
        Assert.Equal(0, profile.FavoriteCount);
        Assert.DoesNotContain(profile.WatchedItems, w => w.ItemId == series.Id);
        _mockUserDataManager.Verify(
            m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user),
            Times.AtLeastOnce);
    }

    [Fact]
    public void BuildProfile_NullSeriesUserData_IsIgnored()
    {
        // Contract for batch refactor: a series that has no entry in the batch dictionary (batch itself is non-null, key is simply missing) must produce the exact same skip-behavior as the pre-batch GetUserData returning null.
        var user = CreateTestUser("charlie");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var series = new Series { Id = Guid.NewGuid(), Name = "Ghost Show", Genres = new[] { "Horror" } };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>())
            .Returns(new List<BaseItem> { series });

        // Batch returns an empty dictionary - the "missing key" case, distinct from a null batch.
        _mockUserDataManager
            .Setup(m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns(new Dictionary<Guid, UserItemData>());
        _mockUserDataManager.Setup(m => m.GetUserData(user, series)).Returns((UserItemData?)null);

        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Empty(profile!.FavoriteSeriesIds);
        Assert.Equal(0, profile.FavoriteCount);
        _mockUserDataManager.Verify(
            m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user),
            Times.AtLeastOnce);
    }

    // Locks in the same contract that SimilarityComputerTests enforces for GetPeopleNamesByItems: non-cancellation failures degrade gracefully to per-item GetUserData, but OperationCanceledException must propagate.

    [Fact]
    public void GetUserWatchProfile_BatchApiThrows_FallsBackToPerItemGetUserData()
    {
        // If GetUserDataBatch throws a non-cancellation exception (e.g. an obscure Jellyfin runtime error), the profile build must fall back to per-item GetUserData so it never regresses below the pre-batch baseline.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "BatchThrowsMovie",
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
            Genres = ["Drama"]
        };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie })
            .Returns(new List<BaseItem>());
        _mockUserDataManager
            .Setup(m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Throws(new InvalidOperationException("batch API unavailable"));
        _mockUserDataManager
            .Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData
            {
                Key = Guid.NewGuid().ToString("N"),
                Played = true,
                PlayCount = 1,
                LastPlayedDate = DateTime.UtcNow
            });

        var profile = _service.GetUserWatchProfile(user.Id);
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.WatchedMovieCount);
        // Fallback path was exercised: per-item GetUserData was called.
        _mockUserDataManager.Verify(m => m.GetUserData(user, movie), Times.AtLeastOnce);
    }

    [Fact]
    public void GetUserWatchProfile_BatchApiCancelled_PropagatesWithoutFallback()
    {
        // OperationCanceledException from GetUserDataBatch must propagate to the caller. Per-item fallback must NOT be invoked once cancellation was requested.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "CancelledMovie" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager
            .Setup(m => m.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Throws(new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => _service.GetUserWatchProfile(user.Id));

        // Per-item fallback must NOT have been invoked once cancellation was signalled.
        _mockUserDataManager.Verify(
            m => m.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()),
            Times.Never);
    }

    // BuildPeopleProfile / AggregatePeopleFromItem - actor/director aggregation

    [Fact]
    public void BuildProfile_MovieWithActorsAndDirectors_PopulatesPeopleProfile()
    {
        // Verifies the happy path of AggregatePeopleFromItem: actors under the top-billed
        // cap (5) plus directors under the max cap (5) are all counted.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Inception",
            RunTimeTicks = TimeSpan.FromMinutes(148).Ticks
        };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });
        _mockLibraryManager.Setup(m => m.GetPeople(movie)).Returns(new List<PersonInfo>
        {
            new() { Name = "Leonardo DiCaprio", Type = PersonKind.Actor },
            new() { Name = "Joseph Gordon-Levitt", Type = PersonKind.Actor },
            new() { Name = "Christopher Nolan", Type = PersonKind.Director }
        });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(1, profile!.PeopleProfile["Leonardo DiCaprio"]);
        Assert.Equal(1, profile.PeopleProfile["Joseph Gordon-Levitt"]);
        Assert.Equal(1, profile.PeopleProfile["Christopher Nolan"]);
    }

    [Fact]
    public void BuildProfile_ActorsExceedTopBilledCap_OnlyFirstFiveAreCounted()
    {
        // BUG SURFACE: without the top-billed cap, a movie with 30 credited extras would flood PeopleProfile and dilute genuine actor-preference signals.
        var user = CreateTestUser("bob");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "BigCast", RunTimeTicks = 1 };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });
        _mockLibraryManager.Setup(m => m.GetPeople(movie)).Returns(new List<PersonInfo>
        {
            new() { Name = "Actor1", Type = PersonKind.Actor },
            new() { Name = "Actor2", Type = PersonKind.Actor },
            new() { Name = "Actor3", Type = PersonKind.Actor },
            new() { Name = "Actor4", Type = PersonKind.Actor },
            new() { Name = "Actor5", Type = PersonKind.Actor },
            new() { Name = "Actor6-Extra", Type = PersonKind.Actor }, // should be excluded
            new() { Name = "Actor7-Extra", Type = PersonKind.Actor }  // should be excluded
        });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(5, profile!.PeopleProfile.Count);
        Assert.DoesNotContain("Actor6-Extra", profile.PeopleProfile.Keys);
        Assert.DoesNotContain("Actor7-Extra", profile.PeopleProfile.Keys);
    }

    [Fact]
    public void BuildProfile_PeopleWithBlankOrDuplicateNames_AreDeduplicated()
    {
        // BUG SURFACE: a person credited twice under the same name (e.g. director AND producer with only one Director role) should count exactly once.
        var user = CreateTestUser("charlie");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "DupeCast", RunTimeTicks = 1 };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });
        _mockLibraryManager.Setup(m => m.GetPeople(movie)).Returns(new List<PersonInfo>
        {
            new() { Name = "Jane Smith", Type = PersonKind.Actor },
            new() { Name = "Jane Smith", Type = PersonKind.Director }, // duplicate name
            new() { Name = "   ", Type = PersonKind.Actor },           // whitespace
            new() { Name = null!, Type = PersonKind.Director }         // null
        });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Single(profile!.PeopleProfile);
        Assert.Equal(1, profile.PeopleProfile["Jane Smith"]);
    }

    [Fact]
    public void BuildProfile_WriterAndProducer_ExcludedFromPeopleProfile()
    {
        // Only Actor + Director count in the recommendation signal. Writers, producers,
        // composers etc. must be silently dropped.
        var user = CreateTestUser("dave");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Crew", RunTimeTicks = 1 };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });
        _mockLibraryManager.Setup(m => m.GetPeople(movie)).Returns(new List<PersonInfo>
        {
            new() { Name = "The Actor", Type = PersonKind.Actor },
            new() { Name = "The Writer", Type = PersonKind.Writer },
            new() { Name = "The Producer", Type = PersonKind.Producer },
            new() { Name = "The Composer", Type = PersonKind.Composer }
        });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Single(profile!.PeopleProfile);
        Assert.Contains("The Actor", profile.PeopleProfile.Keys);
        Assert.DoesNotContain("The Writer", profile.PeopleProfile.Keys);
        Assert.DoesNotContain("The Producer", profile.PeopleProfile.Keys);
    }

    [Fact]
    public void BuildProfile_GetPeopleThrows_SkipsItemGracefully()
    {
        // BUG SURFACE: a corrupted library entry that makes GetPeople throw must NOT
        // abort profile building - it should skip only the affected item.
        var user = CreateTestUser("eve");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var goodMovie = new Movie { Id = Guid.NewGuid(), Name = "Good", RunTimeTicks = 1 };
        var badMovie = new Movie { Id = Guid.NewGuid(), Name = "Corrupt", RunTimeTicks = 1 };

        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { goodMovie, badMovie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        _mockLibraryManager.Setup(m => m.GetPeople(goodMovie)).Returns(new List<PersonInfo>
        {
            new() { Name = "Working Actor", Type = PersonKind.Actor }
        });
        _mockLibraryManager.Setup(m => m.GetPeople(badMovie))
            .Throws(new InvalidOperationException("corrupted metadata"));

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        // Good movie's actor is present; bad movie's failure did not abort the scan.
        Assert.Contains("Working Actor", profile!.PeopleProfile.Keys);
        Assert.Equal(2, profile.WatchedItems.Count);
    }

    [Fact]
    public void BuildProfile_GetPeopleCancelled_PropagatesOperationCanceled()
    {
        // OperationCanceledException must propagate - parity contract with
        // SimilarityComputer.BuildCandidatePeopleLookup.
        var user = CreateTestUser("frank");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Cancelled", RunTimeTicks = 1 };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });
        _mockLibraryManager.Setup(m => m.GetPeople(movie))
            .Throws(new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => _service.GetUserWatchProfile(user.Id));
    }

    [Fact]
    public void BuildProfile_EpisodeWatched_UsesSeriesLevelPeopleAggregation()
    {
        // For episodes, people are aggregated at the series level to avoid over-counting guest actors who appear in every episode.
        var user = CreateTestUser("gina");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var seriesId = Guid.NewGuid();
        var series = new Series { Id = seriesId, Name = "Breaking Bad" };
        var ep1 = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Pilot",
            SeriesId = seriesId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        var ep2 = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Episode 2",
            SeriesId = seriesId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { ep1, ep2 })
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager.Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        _mockLibraryManager.Setup(m => m.GetPeople(series)).Returns(new List<PersonInfo>
        {
            new() { Name = "Bryan Cranston", Type = PersonKind.Actor },
            new() { Name = "Aaron Paul", Type = PersonKind.Actor }
        });
        // Episode-level GetPeople should NOT be called for people aggregation.
        _mockLibraryManager.Setup(m => m.GetPeople(It.Is<BaseItem>(i => i is Episode)))
            .Returns(new List<PersonInfo>());

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        // The series' people appear ONCE despite two episodes being watched.
        Assert.Equal(1, profile!.PeopleProfile["Bryan Cranston"]);
        Assert.Equal(1, profile.PeopleProfile["Aaron Paul"]);
        _mockLibraryManager.Verify(m => m.GetPeople(series), Times.Once);
        // Strengthened contract: GetPeople must NEVER be queried for an Episode. Returning an empty list from the episode setup above would let a regression pass silently (queried but happens to return nothing = same outward result).
        _mockLibraryManager.Verify(
            m => m.GetPeople(It.Is<BaseItem>(i => i is Episode)),
            Times.Never);
    }

    [Fact]
    public void BuildProfile_PartiallyWatchedItem_BelowThreshold_ExcludedFromPeople()
    {
        // Items with < 15% playback progress are considered "abandoned" and must NOT
        // contribute to the PeopleProfile - a bounced-off show should not shape preferences.
        var user = CreateTestUser("hank");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "AbandonedFilm",
            RunTimeTicks = TimeSpan.FromMinutes(120).Ticks
        };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData
            {
                Key = "k",
                Played = false,
                IsFavorite = false,
                // 5 minutes / 120 = 4% - well below the 15% "significant progress" threshold
                PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks
            });
        _mockLibraryManager.Setup(m => m.GetPeople(movie)).Returns(new List<PersonInfo>
        {
            new() { Name = "Should Not Appear", Type = PersonKind.Actor }
        });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.DoesNotContain("Should Not Appear", profile!.PeopleProfile.Keys);
    }

    [Fact]
    public void BuildProfile_PartiallyWatchedItem_AboveThreshold_IncludedInPeople()
    {
        // Items with >= 15% playback progress are treated as meaningful exposure and
        // DO contribute to PeopleProfile even though `Played` is false.
        var user = CreateTestUser("ivy");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "InProgress",
            RunTimeTicks = TimeSpan.FromMinutes(100).Ticks
        };
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData
            {
                Key = "k",
                Played = false,
                IsFavorite = false,
                // 30 minutes / 100 = 30% - well above the 15% threshold
                PlaybackPositionTicks = TimeSpan.FromMinutes(30).Ticks
            });
        _mockLibraryManager.Setup(m => m.GetPeople(movie)).Returns(new List<PersonInfo>
        {
            new() { Name = "Meaningful Actor", Type = PersonKind.Actor }
        });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Contains("Meaningful Actor", profile!.PeopleProfile.Keys);
    }

    [Fact]
    public void BuildPeopleProfile_MissingSeriesMetadata_SkipsEpisodeFallback()
    {
        // The original bug: if we had fallen back to episode-level data, we would either (a) count only limited guest cast instead of the full main cast, or (b) double-count people when a synthetic favourite-series row (ItemId == seriesId, SeriesId == null) is processed later and the.
        var user = CreateTestUser("missingSeriesUser");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);

        var episodeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var episode = new Episode
        {
            Id = episodeId,
            Name = "Pilot",
            SeriesId = seriesId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        // First GetItemList call -> video items (the episode)
        // Second GetItemList call -> series items (empty - series NOT in lookup)
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode })
            .Returns(new List<BaseItem>()); // <-- series lookup is empty

        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        // Episode-level people that must NOT appear in the profile when series is missing.
        _mockLibraryManager.Setup(m => m.GetPeople(It.Is<BaseItem>(i => i.Id == episodeId)))
            .Returns(new List<PersonInfo>
            {
                new() { Name = "Guest Actor Only", Type = PersonKind.Actor },
                new() { Name = "Guest Director Only", Type = PersonKind.Director }
            });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);

        // The episode was played, so it appears in WatchedItems.
        Assert.Contains(profile!.WatchedItems, w => w.ItemId == episodeId && w.Played);

        // But because the series is absent from the series lookup, BuildPeopleProfile skips
        // entirely rather than falling back to episode-level cast data.
        Assert.DoesNotContain("Guest Actor Only", profile.PeopleProfile.Keys);
        Assert.DoesNotContain("Guest Director Only", profile.PeopleProfile.Keys);
        Assert.Empty(profile.PeopleProfile);

        // Confirm GetPeople was never called for the episode (skip path was taken).
        _mockLibraryManager.Verify(
            m => m.GetPeople(It.Is<BaseItem>(i => i.Id == episodeId)),
            Times.Never);
    }

    [Fact]
    public void GetSeriesEpisodeCounts_CountsPlayableEpisodesPerSeries_SkippingPathlessAndOrphans()
    {
        // Two series, mixed playability.
        var seriesA = Guid.NewGuid();
        var seriesB = Guid.NewGuid();

        var episodes = new List<BaseItem>
        {
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesA, Path = "/media/a/s01e01.mkv" },
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesA, Path = "/media/a/s01e02.mkv" },
            // No Path (Arr placeholder before download) - must NOT be counted.
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesA, Path = null },
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesB, Path = "/media/b/s01e01.mkv" },
            // Orphan episode with no SeriesId - must NOT be counted.
            new Episode { Id = Guid.NewGuid(), SeriesId = Guid.Empty, Path = "/media/orphan.mkv" }
        };

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(episodes);

        var counts = _service.GetSeriesEpisodeCounts();

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[seriesA]);
        Assert.Equal(1, counts[seriesB]);
        Assert.False(counts.ContainsKey(Guid.Empty));
    }

    [Fact]
    public void BuildProfile_FavoritedPlayedMovieInMainLoop_IncrementsFavoriteCount()
    {
        // A movie favorited directly (not a series-level favorite) must bump FavoriteCount
        // from the main watched-items loop, independent of the series-favorite pass.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Fav Movie", RunTimeTicks = 1 };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie })
            .Returns(new List<BaseItem>());
        _mockUserDataManager.Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1, IsFavorite = true });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(1, profile!.FavoriteCount);
        Assert.Contains(profile.WatchedItems, w => w.ItemId == movie.Id && w.IsFavorite);
    }

    [Fact]
    public void BuildProfile_FavoriteSeriesGetPeopleThrows_StillAddsSyntheticItem()
    {
        // A non-fatal GetPeople failure on a favorited series must degrade people resolution
        // to empty rather than aborting the profile: the synthetic favorite row still appears.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var series = new Series { Id = Guid.NewGuid(), Name = "Fav Show", Genres = new[] { "Drama" } };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>())
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager.Setup(m => m.GetUserData(user, series))
            .Returns(new UserItemData { Key = "k", Played = false, IsFavorite = true });
        _mockLibraryManager.Setup(m => m.GetPeople(series))
            .Throws(new InvalidOperationException("corrupted people metadata"));

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Contains(series.Id, profile!.FavoriteSeriesIds);
        var synthetic = Assert.Single(profile.WatchedItems, w => w.ItemId == series.Id && w.IsFavorite);
        Assert.Empty(synthetic.PeopleNames);
    }

    [Fact]
    public void BuildProfile_FavoriteSeriesGetPeopleCancelled_PropagatesOperationCanceled()
    {
        // Cancellation during the favorite-series people resolution must propagate,
        // matching the main-loop and AggregatePeopleFromItem contracts.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var series = new Series { Id = Guid.NewGuid(), Name = "Fav Show" };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>())
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager.Setup(m => m.GetUserData(user, series))
            .Returns(new UserItemData { Key = "k", Played = false, IsFavorite = true });
        _mockLibraryManager.Setup(m => m.GetPeople(series))
            .Throws(new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => _service.GetUserWatchProfile(user.Id));
    }

    [Fact]
    public void BuildProfile_SeriesLevelAggregationGetPeopleCancelled_Propagates()
    {
        // A played episode whose parent series is present but NOT favorited reaches
        // AggregatePeopleFromItem(series); a cancellation there must propagate too.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var seriesId = Guid.NewGuid();
        var series = new Series { Id = seriesId, Name = "Show" };
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Pilot",
            SeriesId = seriesId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode })
            .Returns(new List<BaseItem> { series });
        _mockUserDataManager.Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1, IsFavorite = false });
        _mockLibraryManager.Setup(m => m.GetPeople(series))
            .Throws(new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => _service.GetUserWatchProfile(user.Id));
        _mockLibraryManager.Verify(m => m.GetPeople(series), Times.Once);
    }

    private static Jellyfin.Database.Implementations.Entities.User CreateTestUser(string username)
    {
        return new Jellyfin.Database.Implementations.Entities.User(username, "default", "default") { Id = Guid.NewGuid() };
    }
}
