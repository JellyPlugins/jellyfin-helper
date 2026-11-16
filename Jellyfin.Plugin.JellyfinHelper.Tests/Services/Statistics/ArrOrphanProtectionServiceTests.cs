using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class ArrOrphanProtectionServiceTests
{
    private const string Url = "http://radarr.test";
    private readonly Mock<IArrIntegrationService> _arr = new();
    private readonly Jellyfin.Plugin.JellyfinHelper.Services.Security.ISecretProtector _protector = TestMockFactory.CreateSecretProtector();
    private readonly PluginConfiguration _config = new();
    private readonly List<VirtualFolderInfo> _libraries;
    private readonly ArrOrphanProtectionService _service;
    private readonly string _root = Path.GetFullPath(Path.Join("media", "movies"));

    public ArrOrphanProtectionServiceTests()
    {
        _libraries = [new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = [_root] }];
        _service = new ArrOrphanProtectionService(_arr.Object, _protector, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<ArrOrphanProtectionService>().Object, TestMockFactory.CreateCleanupConfigHelper(_config).Object);
        _config.RadarrInstances = [Instance()];
        SetMovies([new ArrMovie { Path = Path.Join(_root, "Wanted"), HasFile = false }]);
        _arr.Setup(a => a.GetRootFoldersAsync(It.IsAny<string>(), "key", It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync([_root]);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(Url, "")]
    [InlineData("", "key")]
    [InlineData(" ", "key")]
    public async Task IncompleteConfiguration_MakesNoRequests(string url, string key)
    {
        _config.RadarrInstances = [new ArrInstanceConfig { Url = url, ApiKey = key }];

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(_root));
        Assert.False(snapshot.IsUncertain(_root));
        Assert.Empty(_arr.Invocations);
    }

    [Fact]
    public async Task NoInstances_MakesNoRequests()
    {
        _config.RadarrInstances = [];

        Assert.Same(OrphanProtectionSnapshot.Empty, await SnapshotAsync());
        Assert.Empty(_arr.Invocations);
    }

    [Fact]
    public async Task RadarrOwnership_ProtectsWantedFolderAncestorsAndChildrenButNotSiblings()
    {
        var wanted = Path.Join(_root, "Wanted");

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(wanted));
        Assert.True(snapshot.ProtectsDirectory(Path.Join(wanted, "Extras")));
        Assert.True(snapshot.ProtectsDirectory(_root));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Orphan")));
        Assert.False(snapshot.IsUncertain(_root));
        _arr.Verify(a => a.GetRadarrMoviesAsync(Url, "key", false, It.IsAny<CancellationToken>()), Times.Once);
        _arr.Verify(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>()), Times.Once);
        _arr.Verify(a => a.GetSonarrSeriesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SonarrOwnership_ProtectsSeriesWithoutEpisodesUsingDecryptedKeyAndTlsSetting()
    {
        _config.RadarrInstances = [];
        _config.SonarrInstances = [new ArrInstanceConfig { Url = Url, ApiKey = _protector.Protect("key"), SkipCertificateValidation = true }];
        _libraries[0].CollectionType = CollectionTypeOptions.tvshows;
        _arr.Setup(a => a.GetSonarrSeriesAsync(Url, "key", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ArrSeries { Path = Path.Join(_root, "Wanted"), EpisodeFileCount = 0 }]);

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted", "Season 01")));
        _arr.Verify(a => a.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(" movies ", "Movies", CollectionTypeOptions.movies, "")]
    [InlineData("Missing", "Movies", CollectionTypeOptions.movies, "")]
    [InlineData("", "Movies", CollectionTypeOptions.music, "")]
    [InlineData("", "My Collection", CollectionTypeOptions.movies, "")]
    [InlineData("Movies", "Movies", CollectionTypeOptions.movies, "Movies")]
    public async Task IneligibleScopes_AreNotQueried(string excluded, string name, CollectionTypeOptions type, string assigned)
    {
        _config.ExcludedLibraries = excluded;
        _libraries[0].Name = name;
        _libraries[0].CollectionType = type;
        _config.RadarrInstances[0].Libraries = assigned;
        if (excluded == "Missing")
        {
            _config.RadarrInstances[0].Libraries = "Missing";
        }

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(_root));
        Assert.Empty(_arr.Invocations);
    }

    [Fact]
    public async Task CollectionRoot_IsNotQueried()
    {
        _libraries[0].Locations = [Path.Join(_root, "collections")];

        await SnapshotAsync();

        Assert.Empty(_arr.Invocations);
    }

    [Fact]
    public async Task ManualAssignment_CanScopeAMixedLibrary()
    {
        _libraries[0].CollectionType = null;
        _config.RadarrInstances[0].Libraries = " Movies ";

        Assert.True((await SnapshotAsync()).ProtectsDirectory(Path.Join(_root, "Wanted")));
    }

    [Fact]
    public async Task UniqueContainerRootRemap_PreservesRelativeHierarchy()
    {
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/arr/movies"]);
        SetMovies([new ArrMovie { Path = "/arr/movies/Group/Wanted" }]);

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(Path.Join(_root, "Group", "Wanted")));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Other", "Wanted")));
    }

    [Fact]
    public async Task NestedRemoteRoots_UseTheMostSpecificUnambiguousLocalMapping()
    {
        var children = Path.GetFullPath(Path.Join("other", "children"));
        _libraries[0].Locations = [_root, children];
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/arr/movies", "/arr/movies/children"]);
        SetMovies([new ArrMovie { Path = "/arr/movies/children/Wanted" }]);

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(Path.Join(children, "Wanted")));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "children", "Wanted")));
        Assert.False(snapshot.IsUncertain(_root));
        Assert.False(snapshot.IsUncertain(children));
    }

    [Fact]
    public async Task ExplicitSingleLibraryAssignment_CanMapADifferentlyNamedMount()
    {
        _config.RadarrInstances[0].Libraries = "Movies";
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/films", "/unrelated"]);
        SetMovies([new ArrMovie { Path = "/films/Group/Wanted" }]);

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(Path.Join(_root, "Group", "Wanted")));
        Assert.False(snapshot.IsUncertain(_root));
    }

    [Fact]
    public async Task ExplicitAssignment_WithMultipleActiveRemoteRoots_IsStillAmbiguous()
    {
        _config.RadarrInstances[0].Libraries = "Movies";
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/films", "/other"]);
        SetMovies([new ArrMovie { Path = "/films/Wanted" }, new ArrMovie { Path = "/other/Other" }]);

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(_root));
        Assert.True(snapshot.IsUncertain(_root));
    }

    [Fact]
    public async Task UniqueRootAlias_CanDifferInCaseWithoutChangingLocalFileSemantics()
    {
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/arr/MOVIES"]);
        SetMovies([new ArrMovie { Path = "/arr/MOVIES/Wanted" }]);

        Assert.True((await SnapshotAsync()).ProtectsDirectory(Path.Join(_root, "Wanted")));
    }

    [Theory]
    [InlineData("C:\\", "C:\\Wanted")]
    [InlineData("/", "/Wanted")]
    public async Task ExplicitRootMapping_PreservesWindowsDriveAndPosixRootSeparators(string remoteRoot, string remotePath)
    {
        _config.RadarrInstances[0].Libraries = "Movies";
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync([remoteRoot]);
        SetMovies([new ArrMovie { Path = remotePath }]);

        Assert.True((await SnapshotAsync()).ProtectsDirectory(Path.Join(_root, "Wanted")));
    }

    [Fact]
    public async Task SonarrFailure_DoesNotProveSeriesOwnershipIsAbsent()
    {
        _config.RadarrInstances = [];
        _config.SonarrInstances = [Instance()];
        _libraries[0].CollectionType = CollectionTypeOptions.tvshows;
        _arr.Setup(a => a.GetSonarrSeriesAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync((List<ArrSeries>?)null);

        Assert.True((await SnapshotAsync()).IsUncertain(_root));
    }

    [Fact]
    public async Task SuccessfulEmptySonarrInventory_HasNoExtraProtectionOrUncertainty()
    {
        _config.RadarrInstances = [];
        _config.SonarrInstances = [Instance()];
        _libraries[0].CollectionType = CollectionTypeOptions.tvshows;
        _arr.Setup(a => a.GetSonarrSeriesAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.IsUncertain(_root));
        Assert.False(snapshot.ProtectsDirectory(_root));
    }

    [Fact]
    public async Task AmbiguousRemoteRoots_DoNotGuessOwnership()
    {
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/one/movies", "/two/movies"]);
        SetMovies([new ArrMovie { Path = "/one/movies/Wanted" }]);

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted")));
        Assert.True(snapshot.IsUncertain(_root));
    }

    [Fact]
    public async Task AmbiguousLocalRoots_DoNotProtectSameNamedFolders()
    {
        var other = Path.GetFullPath(Path.Join("other", "movies"));
        _libraries[0].Locations = [_root, other];
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/arr/movies"]);
        SetMovies([new ArrMovie { Path = "/arr/movies/Wanted" }]);

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted")));
        Assert.True(snapshot.IsUncertain(_root));
        Assert.True(snapshot.IsUncertain(other));
    }

    [Fact]
    public async Task ExactRootMatch_DoesNotPullInSameNamedLibrary()
    {
        var other = Path.GetFullPath(Path.Join("other", "movies"));
        _libraries[0].Locations = [_root, other];

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted")));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(other, "Wanted")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartiallyMappedActiveRootsWithTheSameNameRemainUncertain(bool sonarr)
    {
        _config.RadarrInstances[0].Libraries = "Movies";
        var other = Path.GetFullPath(Path.Join("other", "unassigned"));
        _libraries.Add(new VirtualFolderInfo { Name = "Other", CollectionType = CollectionTypeOptions.movies, Locations = [other] });
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync([_root, "/downloads/movies"]);
        var owned = Path.Join(_root, "Owned");
        if (sonarr)
        {
            _config.RadarrInstances = [];
            _config.SonarrInstances = [new ArrInstanceConfig { Url = Url, ApiKey = "key", Libraries = "Movies" }];
            _libraries[0].CollectionType = CollectionTypeOptions.tvshows;
            _arr.Setup(a => a.GetSonarrSeriesAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(
                [new ArrSeries { Path = owned }, new ArrSeries { Path = "/downloads/movies/Wanted" }]);
        }
        else
        {
            SetMovies([new ArrMovie { Path = owned }, new ArrMovie { Path = "/downloads/movies/Wanted" }]);
        }

        var snapshot = await SnapshotAsync();
        Assert.True(snapshot.ProtectsDirectory(owned));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted")));
        Assert.True(snapshot.IsUncertain(_root));
        Assert.True(snapshot.IsUncertain(Path.Join(_root, "Wanted")));
        Assert.False(snapshot.IsUncertain(other));
        _arr.Verify(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnresolvedNestedActiveRootCannotFallThroughToAMappedAncestor()
    {
        var remote = _root.Replace('\\', '/');
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync([remote, remote + "/nested"]);
        SetMovies([new ArrMovie { Path = remote + "/nested/Wanted" }]);

        var snapshot = await SnapshotAsync();
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "nested", "Wanted")));
        Assert.True(snapshot.IsUncertain(_root));
    }

    [Theory]
    [InlineData("Z:\\ARR\\movies", "z:\\arr\\MOVIES\\Wanted")]
    [InlineData("\\\\SERVER\\Share\\movies", "\\\\server\\share\\MOVIES\\Wanted")]
    public async Task RemoteWindowsPathsAreCaseInsensitiveOnEveryHost(string remoteRoot, string remotePath)
    {
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync([remoteRoot]);
        SetMovies([new ArrMovie { Path = remotePath }]);
        var snapshot = await SnapshotAsync();
        Assert.True(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted")));
        Assert.False(snapshot.IsUncertain(_root));
    }

    [Fact]
    public async Task RemotePosixPathsStayCaseSensitiveOnWindowsHostsToo()
    {
        _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(["/arr/Movies"]);
        SetMovies([new ArrMovie { Path = "/arr/movies/Wanted" }]);
        var snapshot = await SnapshotAsync();
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted")));
        Assert.True(snapshot.IsUncertain(_root));
    }

    [Theory]
    [InlineData("movie.trickplay")]
    [InlineData(".jellyfin-trash")]
    [InlineData("collections")]
    [InlineData("Wanted [boxset]")]
    [InlineData("Wanted [collection]")]
    public async Task SkippedDirectoryEntries_CannotProtectAnAncestor(string name)
    {
        SetMovies([new ArrMovie { Path = Path.Join(_root, name, "Wanted") }]);

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(_root));
    }

    [Fact]
    public async Task EmptyTrashSetting_UsesTheExistingDefault()
    {
        _config.TrashFolderPath = " ";

        Assert.True((await SnapshotAsync()).ProtectsDirectory(Path.Join(_root, "Wanted")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/Wanted")]
    [InlineData("\\")]
    [InlineData("C:Wanted")]
    [InlineData("1:/Wanted")]
    [InlineData("/arr/movies/../Wanted")]
    [InlineData("/arr/movies/bad\u0000path")]
    public async Task InvalidInventoryPaths_AreUnprovenNotOwnership(string path)
    {
        SetMovies([new ArrMovie { Path = path }]);

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(_root));
        Assert.True(snapshot.IsUncertain(_root));
    }

    [Fact]
    public async Task MixedInventoryPaths_PreservePositiveOwnershipAndInvalidPathUncertaintyWithoutProtectingExcludedFolders()
    {
        var wanted = Path.Join(_root, "Group", "Wanted");
        var trash = Path.Join(_root, ".jellyfin-trash", "Discarded");
        var trickplay = Path.Join(_root, "Ignored", "movie.trickplay", "Frame");
        SetMovies([
            new ArrMovie { Path = wanted },
            new ArrMovie { Path = "relative/Invalid" },
            new ArrMovie { Path = trash },
            new ArrMovie { Path = trickplay },
            new ArrMovie { Path = _root + "-other/Outside" }
        ]);

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(wanted));
        Assert.True(snapshot.ProtectsDirectory(Path.Join(wanted, "Extras")));
        Assert.True(snapshot.IsUncertain(_root));
        Assert.True(snapshot.IsUncertain(Path.Join(_root, "Orphan")));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Orphan")));
        Assert.False(snapshot.ProtectsDirectory(trash));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(_root, "Ignored")));
        Assert.False(snapshot.ProtectsDirectory(_root + "-other"));
        Assert.False(snapshot.IsUncertain(_root + "-other"));
    }

    [Fact]
    public async Task PathsOutsideRootAndRootItself_DoNotProtectTheLibrary()
    {
        SetMovies([new ArrMovie { Path = _root }, new ArrMovie { Path = _root + "-other/Wanted" }]);

        Assert.False((await SnapshotAsync()).ProtectsDirectory(_root));
    }

    [Fact]
    public async Task SuccessfulEmptyInventory_IsDifferentFromFailedLookup()
    {
        SetMovies([]);

        var snapshot = await SnapshotAsync();

        Assert.False(snapshot.ProtectsDirectory(_root));
        Assert.False(snapshot.IsUncertain(_root));
        _arr.Verify(a => a.GetRootFoldersAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRequests_LeaveOnlyTheAssignedScopeUnproven(bool rootRequest)
    {
        var other = Path.GetFullPath(Path.Join("media", "other"));
        _libraries.Add(new VirtualFolderInfo { Name = "Other", CollectionType = CollectionTypeOptions.movies, Locations = [other] });
        _config.RadarrInstances[0].Libraries = "Movies";
        if (rootRequest)
        {
            _arr.Setup(a => a.GetRootFoldersAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync((List<string>?)null);
        }
        else
        {
            SetMovies(null);
        }

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.IsUncertain(_root));
        Assert.False(snapshot.IsUncertain(other));
        Assert.False(snapshot.ProtectsDirectory(_root));
    }

    [Fact]
    public async Task Timeout_DoesNotMasqueradeAsUserCancellation()
    {
        _arr.Setup(a => a.GetRadarrMoviesAsync(Url, "key", false, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        Assert.True((await SnapshotAsync()).IsUncertain(_root));
    }

    [Fact]
    public async Task UserCancellation_IsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        _arr.Setup(a => a.GetRadarrMoviesAsync(Url, "key", false, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetSnapshotAsync(_config, _libraries, cancellation.Token));
    }

    [Fact]
    public async Task AlreadyCancelled_MakesNoRequests()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetSnapshotAsync(_config, _libraries, cancellation.Token));
        Assert.Empty(_arr.Invocations);
    }

    [Fact]
    public async Task UndecryptableKey_MakesNoRequestsAndLeavesScopeUnproven()
    {
        var protector = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.Security.ISecretProtector>();
        protector.Setup(p => p.Unprotect(It.IsAny<string>())).Returns(string.Empty);
        var service = new ArrOrphanProtectionService(_arr.Object, protector.Object, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<ArrOrphanProtectionService>().Object, TestMockFactory.CreateCleanupConfigHelper(_config).Object);

        Assert.True((await service.GetSnapshotAsync(_config, _libraries, TestContext.Current.CancellationToken)).IsUncertain(_root));
        Assert.Empty(_arr.Invocations);
    }

    [Fact]
    public async Task OneFailedInstance_DoesNotDiscardAnotherInstancesPositiveProtection()
    {
        _config.RadarrInstances.Add(Instance("http://second.test"));
        _arr.Setup(a => a.GetRadarrMoviesAsync("http://second.test", "key", false, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());

        var snapshot = await SnapshotAsync();

        Assert.True(snapshot.ProtectsDirectory(Path.Join(_root, "Wanted")));
        Assert.True(snapshot.IsUncertain(_root));
    }

    [Fact]
    public async Task NullInputs_AreRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.GetSnapshotAsync(null!, _libraries, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.GetSnapshotAsync(_config, null!, TestContext.Current.CancellationToken));
    }

    private Task<OrphanProtectionSnapshot> SnapshotAsync() => _service.GetSnapshotAsync(_config, _libraries, TestContext.Current.CancellationToken);

    private void SetMovies(List<ArrMovie>? movies)
    {
        _arr.Setup(a => a.GetRadarrMoviesAsync(Url, "key", false, It.IsAny<CancellationToken>())).ReturnsAsync(movies);
    }

    private static ArrInstanceConfig Instance(string url = Url) => new() { Url = url, ApiKey = "key" };
}
