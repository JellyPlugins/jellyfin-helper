using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Extended tests for <see cref="DiscoveryController"/> that fill in the gaps left by
///     <c>DiscoveryControllerTests</c>: happy paths for GetSeerrUsers / GetServiceInfo,
///     SubmitRequest success path (including MarkAsRequestedAsync integration), the
///     GetDiscoveryResults filter logic against dismissed/requested items, and behaviour
///     when the feedback store throws.
/// </summary>
public sealed class DiscoveryControllerExtendedTests : IDisposable
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock;
    private readonly DiscoveryCacheService _cache;

    // Storage isolation: DiscoveryCacheService derives its file path from
    // Plugin.Instance?.DataFolderPath, falling back to string.Empty which resolves
    // the cache filename against the current working directory. Any test that calls
    // Save persists a real JSON blob at "<cwd>/jellyfin-helper-discovery-results.json".
    // Without cleanup, cache state from one test class leaks into another that runs
    // afterwards on the same worker (xUnit executes tests in a class sequentially, but
    // different test classes may share process-wide state via files).
    //
    // We intentionally do NOT alter Directory.SetCurrentDirectory here because CWD is
    // a process-global setting and xUnit may run other test classes in parallel on
    // separate threads. Instead we snapshot / delete the exact cache file at both
    // fixture ends so this class starts and finishes with a clean slate, regardless
    // of what any concurrent class did.
    private const string CacheFileName = "jellyfin-helper-discovery-results.json";
    private readonly string _cacheFilePath;
    private readonly byte[]? _originalCacheContents;

    public DiscoveryControllerExtendedTests()
    {
        _cacheFilePath = Path.Combine(Directory.GetCurrentDirectory(), CacheFileName);
        _originalCacheContents = File.Exists(_cacheFilePath)
            ? File.ReadAllBytes(_cacheFilePath)
            : null;
        // Start every test with no pre-existing cache so seed order is deterministic.
        TryDeleteCacheFile();

        var pluginLog = new Mock<IPluginLogService>();
        var cacheLogger = new Mock<ILogger<DiscoveryCacheService>>();
        _cache = new DiscoveryCacheService(pluginLog.Object, cacheLogger.Object);
        _discoveryMock = new Mock<ISeerrDiscoveryService>();
        _feedbackStoreMock = new Mock<IDiscoveryFeedbackStore>();
    }

    public void Dispose()
    {
        _cache.Dispose();

        // Restore whatever cache state the process had before this fixture ran, so we
        // do not leave an artifact behind for downstream test classes to trip over.
        TryDeleteCacheFile();
        if (_originalCacheContents is not null)
        {
            try
            {
                File.WriteAllBytes(_cacheFilePath, _originalCacheContents);
            }
            catch (IOException)
            {
                // Best effort — a locking file would be surfaced by the next test's setup.
            }
            catch (UnauthorizedAccessException)
            {
                // Same rationale.
            }
        }

        GC.SuppressFinalize(this);
    }

    private void TryDeleteCacheFile()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private DiscoveryController CreateController() =>
        new(_cache, _discoveryMock.Object, _feedbackStoreMock.Object);

    [Fact]
    public async Task GetSeerrUsers_ReturnsOkWithUserList()
    {
        var users = new List<SeerrUser>
        {
            new() { Id = 1, DisplayName = "alice", JellyfinUserId = "aaa" },
            new() { Id = 2, DisplayName = "bob", JellyfinUserId = "bbb" }
        };
        _discoveryMock.Setup(d => d.GetSeerrUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);

        var controller = CreateController();
        var result = await controller.GetSeerrUsers(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IReadOnlyList<SeerrUser>>(ok.Value);
        Assert.Equal(2, returned.Count);
    }

    [Fact]
    public async Task GetSeerrUsers_EmptyList_StillReturnsOk()
    {
        _discoveryMock.Setup(d => d.GetSeerrUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var controller = CreateController();
        var result = await controller.GetSeerrUsers(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IReadOnlyList<SeerrUser>>(ok.Value);
        Assert.Empty(returned);
    }

    [Fact]
    public async Task GetServiceInfo_Radarr_ReturnsOkWithServices()
    {
        var services = new List<SeerrServiceInfo>
        {
            new() { Id = 5, Name = "Radarr", IsDefault = true }
        };
        _discoveryMock.Setup(d => d.GetServiceInfoAsync("radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(services);

        var controller = CreateController();
        var result = await controller.GetServiceInfo("radarr", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IReadOnlyList<SeerrServiceInfo>>(ok.Value);
        Assert.Single(returned);
        Assert.Equal(5, returned[0].Id);
    }

    [Fact]
    public async Task GetServiceInfo_Sonarr_ReturnsOkWithServices()
    {
        _discoveryMock.Setup(d => d.GetServiceInfoAsync("sonarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var controller = CreateController();
        var result = await controller.GetServiceInfo("sonarr", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task PostRequest_HappyPath_ReturnsOkAndMarksAsRequested()
    {
        _discoveryMock.Setup(d => d.SubmitRequestAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Request submitted successfully."));

        // Seed a matching recommendation so we can prove the persisted state transition,
        // not just the HTTP result. The controller's success path must also flip the
        // cached item's AlreadyRequested flag via _cache.MarkAsRequestedAsync — a
        // regression that drops that call would leave this flag false, and the item
        // would silently reappear on the next discovery-page refresh.
        var userId = Guid.NewGuid();
        _cache.Save(
        [
            new DiscoveryResult
            {
                UserId = userId,
                UserName = "u",
                Recommendations = new List<DiscoveryRecommendation>
                {
                    new() { TmdbId = 12345, MediaType = "movie", Title = "Target", AlreadyRequested = false },
                    new() { TmdbId = 99999, MediaType = "movie", Title = "Other",  AlreadyRequested = false }
                }
            }
        ]);

        var controller = CreateController();
        var dto = new DiscoveryRequestDto { TmdbId = 12345, MediaType = "movie" };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<RequestResult>(ok.Value);
        Assert.True(body.Success);
        Assert.Contains("submitted", body.Message, StringComparison.OrdinalIgnoreCase);

        // Post-condition: the matching cache item is flagged as requested; the other
        // one remains untouched (proves the mark is scoped by TmdbId, not blanket).
        var reload = _cache.Load();
        var user = Assert.Single(reload);
        var target = user.Recommendations.Single(r => r.TmdbId == 12345);
        var other = user.Recommendations.Single(r => r.TmdbId == 99999);
        Assert.True(target.AlreadyRequested, "matching item must be marked as requested");
        Assert.False(other.AlreadyRequested, "non-matching item must be untouched");
    }

    [Fact]
    public async Task PostRequest_RootFolderIsTrimmed_BeforeForwardingToService()
    {
        // BUG GUARD: leading/trailing whitespace in rootFolder must be stripped before
        // sending to Seerr — a Seerr backend that strictly matches its known root folders
        // would reject the request otherwise.
        string? capturedRootFolder = null;
        _discoveryMock.Setup(d => d.SubmitRequestAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, string, int?, int?, int?, string?, CancellationToken>(
                (_, _, _, _, _, rf, _) => capturedRootFolder = rf)
            .ReturnsAsync((true, "OK"));

        var controller = CreateController();
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "   /media/movies   "
        };

        await controller.SubmitRequest(dto, CancellationToken.None);

        Assert.Equal("/media/movies", capturedRootFolder);
    }

    [Fact]
    public async Task PostRequest_WhitespaceOnlyRootFolder_CoalescedToNull()
    {
        // Contract: whitespace-only rootFolder must be treated as "not specified" (null),
        // NOT sent as an empty string. This prevents ambiguous requests where Seerr can't
        // distinguish between "use server default" and "override with empty string".
        string? capturedRootFolder = "sentinel";
        _discoveryMock.Setup(d => d.SubmitRequestAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, string, int?, int?, int?, string?, CancellationToken>(
                (_, _, _, _, _, rf, _) => capturedRootFolder = rf)
            .ReturnsAsync((true, "OK"));

        var controller = CreateController();
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "   "
        };

        await controller.SubmitRequest(dto, CancellationToken.None);

        Assert.Null(capturedRootFolder);
    }

    [Fact]
    public async Task PostRequest_NullRootFolder_ForwardedAsNull()
    {
        string? capturedRootFolder = "sentinel";
        _discoveryMock.Setup(d => d.SubmitRequestAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, string, int?, int?, int?, string?, CancellationToken>(
                (_, _, _, _, _, rf, _) => capturedRootFolder = rf)
            .ReturnsAsync((true, "OK"));

        var controller = CreateController();
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", RootFolder = null };

        await controller.SubmitRequest(dto, CancellationToken.None);

        Assert.Null(capturedRootFolder);
    }

    [Fact]
    public async Task PostRequest_TildeAtStartAfterWhitespace_ReturnsBadRequest()
    {
        // Regression guard: rootFolder = "  ~/movies" (with leading whitespace) must still
        // be rejected. The controller trims first, then checks TrimStart().StartsWith('~'),
        // so this is a redundancy check. Locking the behaviour here prevents a future
        // refactor from dropping the TrimStart guard.
        var controller = CreateController();
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "\t~/malicious"
        };

        var result = await controller.SubmitRequest(dto, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void GetDiscoveryResults_FiltersDismissedItems()
    {
        var userId = Guid.NewGuid();
        var dismissedTmdbId = 999;
        _feedbackStoreMock.Setup(s => s.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)> { (dismissedTmdbId, "movie") });
        _feedbackStoreMock.Setup(s => s.GetRequestedItems(userId))
            .Returns(new HashSet<(int, string)>());

        var results = new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "u",
                Recommendations = new List<DiscoveryRecommendation>
                {
                    new() { TmdbId = dismissedTmdbId, MediaType = "movie", Title = "Dismissed" },
                    new() { TmdbId = 111, MediaType = "movie", Title = "Kept" }
                }
            }
        };
        _cache.Save(results);

        var controller = CreateController();
        var response = controller.GetDiscoveryResults();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var filtered = Assert.IsAssignableFrom<IReadOnlyList<DiscoveryResult>>(ok.Value);
        var user = Assert.Single(filtered);
        Assert.Single(user.Recommendations);
        Assert.Equal(111, user.Recommendations[0].TmdbId);
    }

    [Fact]
    public void GetDiscoveryResults_FiltersRequestedItems()
    {
        var userId = Guid.NewGuid();
        var requestedTmdbId = 555;
        _feedbackStoreMock.Setup(s => s.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)>());
        _feedbackStoreMock.Setup(s => s.GetRequestedItems(userId))
            .Returns(new HashSet<(int, string)> { (requestedTmdbId, "tv") });

        var results = new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "u",
                Recommendations = new List<DiscoveryRecommendation>
                {
                    new() { TmdbId = requestedTmdbId, MediaType = "tv", Title = "AlreadyRequested" },
                    new() { TmdbId = 222, MediaType = "movie", Title = "Kept" }
                }
            }
        };
        _cache.Save(results);

        var controller = CreateController();
        var response = controller.GetDiscoveryResults();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var filtered = Assert.IsAssignableFrom<IReadOnlyList<DiscoveryResult>>(ok.Value);
        var user = Assert.Single(filtered);
        Assert.Single(user.Recommendations);
        Assert.Equal(222, user.Recommendations[0].TmdbId);
    }

    [Fact]
    public void GetDiscoveryResults_FiltersAlreadyRequestedFlag()
    {
        var userId = Guid.NewGuid();
        _feedbackStoreMock.Setup(s => s.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)>());
        _feedbackStoreMock.Setup(s => s.GetRequestedItems(userId))
            .Returns(new HashSet<(int, string)>());

        var results = new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "u",
                Recommendations = new List<DiscoveryRecommendation>
                {
                    new() { TmdbId = 1, MediaType = "movie", Title = "AlreadyRequested", AlreadyRequested = true },
                    new() { TmdbId = 2, MediaType = "movie", Title = "Kept", AlreadyRequested = false }
                }
            }
        };
        _cache.Save(results);

        var controller = CreateController();
        var response = controller.GetDiscoveryResults();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var filtered = Assert.IsAssignableFrom<IReadOnlyList<DiscoveryResult>>(ok.Value);
        var user = Assert.Single(filtered);
        Assert.Single(user.Recommendations);
        Assert.Equal(2, user.Recommendations[0].TmdbId);
    }

    [Fact]
    public void GetDiscoveryResults_FeedbackStoreThrows_StillReturnsOk()
    {
        var userId = Guid.NewGuid();
        _feedbackStoreMock.Setup(s => s.GetDismissedItems(userId))
            .Throws(new InvalidOperationException("simulated failure"));

        var results = new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "u",
                Recommendations = new List<DiscoveryRecommendation>
                {
                    new() { TmdbId = 42, MediaType = "movie", Title = "keep" }
                }
            }
        };
        _cache.Save(results);

        var controller = CreateController();
        var response = controller.GetDiscoveryResults();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var filtered = Assert.IsAssignableFrom<IReadOnlyList<DiscoveryResult>>(ok.Value);
        var user = Assert.Single(filtered);
        Assert.Single(user.Recommendations);
    }

    [Fact]
    public void GetDiscoveryResults_TakesOnlyMaxVisiblePerUser()
    {
        var userId = Guid.NewGuid();
        _feedbackStoreMock.Setup(s => s.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)>());
        _feedbackStoreMock.Setup(s => s.GetRequestedItems(userId))
            .Returns(new HashSet<(int, string)>());

        var recs = new List<DiscoveryRecommendation>();
        for (var i = 0; i < SeerrDiscoveryService.MaxVisiblePerUser + 5; i++)
        {
            recs.Add(new DiscoveryRecommendation { TmdbId = i + 1, MediaType = "movie", Title = $"m{i}" });
        }

        _cache.Save([new DiscoveryResult { UserId = userId, UserName = "u", Recommendations = recs }]);

        var controller = CreateController();
        var response = controller.GetDiscoveryResults();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var filtered = Assert.IsAssignableFrom<IReadOnlyList<DiscoveryResult>>(ok.Value);
        var user = Assert.Single(filtered);
        Assert.Equal(SeerrDiscoveryService.MaxVisiblePerUser, user.Recommendations.Count);
    }
}
