using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="UserDiscoveryController.SubmitMyRequest"/> and
///     <see cref="UserDiscoveryController.DismissItem"/> with the access gate ENABLED.
///     Covers the validation and permission logic that the disabled-gate suite cannot reach.
/// </summary>
[Collection("ConfigOverride")]
public sealed class UserDiscoveryControllerSubmitTests : IDisposable
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock;
    private readonly DiscoveryCacheService _cache;
    private readonly Mock<ILogger<UserDiscoveryController>> _loggerMock;
    private readonly Mock<IPluginConfigurationService> _configServiceMock;

    public UserDiscoveryControllerSubmitTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.DiscoveryUserAccessEnabled = true;

        _cache = new DiscoveryCacheService(
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<DiscoveryCacheService>>().Object);
        _discoveryMock = new Mock<ISeerrDiscoveryService>();
        _feedbackStoreMock = new Mock<IDiscoveryFeedbackStore>();
        _loggerMock = new Mock<ILogger<UserDiscoveryController>>();
        _configServiceMock = new Mock<IPluginConfigurationService>();
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration { DiscoveryUserAccessEnabled = true });
    }

    public void Dispose()
    {
        ControllerTestFactory.ResetPluginConfiguration();
        _cache.Dispose();
    }

    private UserDiscoveryController CreateController(Guid? userId = null)
    {
        var c = new UserDiscoveryController(_cache, _discoveryMock.Object, _feedbackStoreMock.Object, _configServiceMock.Object, _loggerMock.Object);
        var claims = new List<Claim>();
        if (userId.HasValue)
        {
            claims.Add(new Claim("Jellyfin-UserId", userId.Value.ToString()));
        }

        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return c;
    }

    // Validates a DiscoveryRequestDto using Data Annotations + IValidatableObject, mirroring what
    // [ApiController] does in the MVC pipeline before the action body is entered.
    private static IList<ValidationResult> ValidateDto(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    // --- DiscoveryRequestDto validation (enforced by [ApiController] in production) ---

    [Fact]
    public void RequestDto_TmdbId_Zero_FailsValidation()
    {
        var dto = new DiscoveryRequestDto { TmdbId = 0, MediaType = "movie" };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryRequestDto.TmdbId)));
    }

    [Fact]
    public void RequestDto_TmdbId_Negative_FailsValidation()
    {
        var dto = new DiscoveryRequestDto { TmdbId = -5, MediaType = "movie" };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryRequestDto.TmdbId)));
    }

    [Theory]
    [InlineData("music")]
    [InlineData("")]
    public void RequestDto_InvalidMediaType_FailsValidation(string mt)
    {
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = mt };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryRequestDto.MediaType)));
    }

    [Fact]
    public void RequestDto_RootFolderTooLong_FailsValidation()
    {
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", RootFolder = new string('a', 600) };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryRequestDto.RootFolder)));
    }

    [Fact]
    public void RequestDto_RootFolderPathTraversal_FailsValidation()
    {
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", RootFolder = "/media/../etc/passwd" };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryRequestDto.RootFolder)));
    }

    [Fact]
    public void RequestDto_RootFolderTilde_FailsValidation()
    {
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", RootFolder = "~/movies" };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryRequestDto.RootFolder)));
    }

    [Fact]
    public void RequestDto_RootFolderWithControlChars_FailsValidation()
    {
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", RootFolder = "/media/\0movies" };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryRequestDto.RootFolder)));
    }

    [Fact]
    public void RequestDto_ValidPayload_PassesValidation()
    {
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", RootFolder = "/media/movies" };
        var errors = ValidateDto(dto);
        Assert.Empty(errors);
    }

    // --- SubmitMyRequest controller logic ---

    [Fact]
    public async Task SubmitMyRequest_RootFolderWhitespaceOnly_TreatedAsNull()
    {
        // Whitespace-only RootFolder is coalesced to null inside the controller, so a matching
        // profile with no root folder constraint accepts the request and uses server defaults.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = new List<AllowedQualityProfile>
                {
                    new() { ServerId = 5, ProfileId = 20, RootFolder = string.Empty }
                }
            });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        _discoveryMock
            .Setup(d => d.SubmitRequestAsync(100, "movie", 42, 5, 20, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "queued"));

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", ServerId = 5, ProfileId = 20, RootFolder = "   " };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        var ok = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, ok.StatusCode);
        var body = Assert.IsType<RequestResult>(ok.Value);
        Assert.True(body.Success);
    }

    [Fact]
    public async Task SubmitMyRequest_ServerIdWithoutProfileId_Returns400()
    {
        // Partial overrides (ServerId without ProfileId) are semantically invalid: must reject
        // rather than send a nonsense request to Seerr.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = true });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", ServerId = 5 };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SubmitMyRequest_NoPermission_Returns403()
    {
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = false, IsTransient = false, DeniedReason = "Not authorized" });

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie" };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_TransientPermissionFailure_Returns503()
    {
        // Transient upstream failure (Seerr temporarily down) must return 503 so the client retries.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "tv", "sonarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = false, IsTransient = true, DeniedReason = "upstream" });

        var dto = new DiscoveryRequestDto { TmdbId = 200, MediaType = "tv" };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_SeerrUserIdCannotBeResolved_Returns502()
    {
        // After CanRequest=true, an unresolvable user ID is a transient upstream failure: 502 not 500.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = true });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie" };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_ProfileNotInAllowList_Returns403()
    {
        // Security: user requested a profile not in their allow-list → reject.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = new List<AllowedQualityProfile> { new() { ServerId = 1, ProfileId = 10 } }
            });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", ServerId = 1, ProfileId = 99 };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_RootFolderMismatch_Returns403()
    {
        // Security: profile has a specific RootFolder — client MUST match it exactly.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = new List<AllowedQualityProfile> { new() { ServerId = 1, ProfileId = 10, RootFolder = "/movies/hd" } }
            });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie", ServerId = 1, ProfileId = 10, RootFolder = "/movies/4k" };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_HappyPath_Returns201_AndRecordsFeedback()
    {
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = true });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        _discoveryMock
            .Setup(d => d.SubmitRequestAsync(100, "movie", 42, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "queued"));

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie" };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);

        var ok = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, ok.StatusCode);
        var body = Assert.IsType<RequestResult>(ok.Value);
        Assert.True(body.Success);
        _feedbackStoreMock.Verify(f => f.RecordRequested(userId, 100, "movie"), Times.Once);
    }

    [Fact]
    public async Task SubmitMyRequest_SeerrSubmissionFails_Returns502()
    {
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = true });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        _discoveryMock
            .Setup(d => d.SubmitRequestAsync(100, "movie", 42, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Seerr HTTP 500"));

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie" };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, status.StatusCode);
        _feedbackStoreMock.Verify(f => f.RecordRequested(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SubmitMyRequest_HappyPath_SwallowsFeedbackStoreException()
    {
        // Even if feedback bookkeeping throws, the 201 must still be returned.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = true });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        _discoveryMock
            .Setup(d => d.SubmitRequestAsync(100, "movie", 42, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "queued"));
        _feedbackStoreMock
            .Setup(f => f.RecordRequested(userId, 100, "movie"))
            .Throws(new InvalidOperationException("simulated IO failure"));

        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie" };
        var result = await CreateController(userId).SubmitMyRequest(dto, CancellationToken.None);

        var ok = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, ok.StatusCode);
        var body = Assert.IsType<RequestResult>(ok.Value);
        Assert.True(body.Success);
    }

    // --- DismissItem validation (enforced by [ApiController] in production) ---

    [Fact]
    public void DismissDto_TmdbId_Zero_FailsValidation()
    {
        var dto = new DiscoveryDismissDto { TmdbId = 0, MediaType = "movie" };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryDismissDto.TmdbId)));
    }

    [Fact]
    public void DismissDto_TmdbId_Negative_FailsValidation()
    {
        var dto = new DiscoveryDismissDto { TmdbId = -1, MediaType = "movie" };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryDismissDto.TmdbId)));
    }

    [Theory]
    [InlineData("music")]
    [InlineData("")]
    public void DismissDto_InvalidMediaType_FailsValidation(string mt)
    {
        var dto = new DiscoveryDismissDto { TmdbId = 100, MediaType = mt };
        var errors = ValidateDto(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(DiscoveryDismissDto.MediaType)));
    }

    // --- DismissItem controller logic ---

    [Fact]
    public void DismissItem_NoUserClaim_ReturnsUnauthorized()
    {
        var dto = new DiscoveryDismissDto { TmdbId = 100, MediaType = "movie" };
        var result = CreateController(null).DismissItem(dto);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public void DismissItem_HappyPath_RecordsAndReturnsSuccess()
    {
        var userId = Guid.NewGuid();
        var dto = new DiscoveryDismissDto { TmdbId = 100, MediaType = "movie" };

        var result = CreateController(userId).DismissItem(dto);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<RequestResult>(ok.Value);
        Assert.True(body.Success);
        _feedbackStoreMock.Verify(f => f.RecordDismissed(userId, 100, "movie"), Times.Once);
    }

    [Fact]
    public void DismissItem_FeedbackStoreThrows_StillReturnsSuccess()
    {
        var userId = Guid.NewGuid();
        _feedbackStoreMock
            .Setup(f => f.RecordDismissed(userId, 100, "movie"))
            .Throws(new InvalidOperationException("simulated IO failure"));

        var dto = new DiscoveryDismissDto { TmdbId = 100, MediaType = "movie" };
        var result = CreateController(userId).DismissItem(dto);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<RequestResult>(ok.Value);
        Assert.True(body.Success);
    }

    // --- GetExternalLinksConfig ---

    [Fact]
    public void GetExternalLinksConfig_NoUserClaim_ReturnsUnauthorized()
    {
        var result = CreateController(null).GetExternalLinksConfig();
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public void GetExternalLinksConfig_ReturnsSeerrUrl_TrimmedAndNormalised()
    {
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration
        {
            DiscoveryUserAccessEnabled = true,
            SeerrUrl = "  https://seerr.example.com/  "
        });
        var result = CreateController(Guid.NewGuid()).GetExternalLinksConfig();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        var seerrUrlProp = ok.Value!.GetType().GetProperty("SeerrUrl");
        Assert.NotNull(seerrUrlProp);
        Assert.Equal("https://seerr.example.com", seerrUrlProp!.GetValue(ok.Value) as string);
    }

    [Fact]
    public void GetExternalLinksConfig_NoSeerrUrlConfigured_ReturnsEmpty()
    {
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration
        {
            DiscoveryUserAccessEnabled = true,
            SeerrUrl = null!
        });
        var result = CreateController(Guid.NewGuid()).GetExternalLinksConfig();
        var ok = Assert.IsType<OkObjectResult>(result);
        var seerrUrlProp = ok.Value!.GetType().GetProperty("SeerrUrl");
        Assert.Equal(string.Empty, seerrUrlProp!.GetValue(ok.Value) as string);
    }

    // --- SubmitMyRequest rate limiting ---

    [Fact]
    public async Task SubmitMyRequest_SameUser_SecondCallWithinWindow_Returns429()
    {
        var userId = Guid.NewGuid();
        var dto = new DiscoveryRequestDto { TmdbId = 1, MediaType = "movie" };

        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = true });
        _discoveryMock
            .Setup(d => d.ResolveSeerrUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        _discoveryMock
            .Setup(d => d.SubmitRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));

        var controller = CreateController(userId);

        var first = await controller.SubmitMyRequest(dto, CancellationToken.None);
        var firstResult = Assert.IsType<ObjectResult>(first.Result);
        Assert.Equal(201, firstResult.StatusCode);

        var second = await controller.SubmitMyRequest(dto, CancellationToken.None);
        var tooMany = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(429, tooMany.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_DifferentUsers_BothSucceed()
    {
        var dto = new DiscoveryRequestDto { TmdbId = 2, MediaType = "movie" };
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        foreach (var uid in new[] { userA, userB })
        {
            _discoveryMock
                .Setup(d => d.GetUserRequestPermissionsAsync(uid, "movie", "radarr", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UserRequestPermissionResult { CanRequest = true });
            _discoveryMock
                .Setup(d => d.ResolveSeerrUserIdAsync(uid, It.IsAny<CancellationToken>()))
                .ReturnsAsync(uid == userA ? 10 : 20);
        }

        _discoveryMock
            .Setup(d => d.SubmitRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));

        var resultA = await CreateController(userA).SubmitMyRequest(dto, CancellationToken.None);
        var resultB = await CreateController(userB).SubmitMyRequest(dto, CancellationToken.None);

        var okA = Assert.IsType<ObjectResult>(resultA.Result);
        Assert.Equal(201, okA.StatusCode);
        var okB = Assert.IsType<ObjectResult>(resultB.Result);
        Assert.Equal(201, okB.StatusCode);
    }
}
