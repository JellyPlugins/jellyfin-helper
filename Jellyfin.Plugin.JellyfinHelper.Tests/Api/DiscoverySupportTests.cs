using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="DiscoverySupport" />, the shared claim-resolution and excluded-item helpers
///     used by both discovery controllers.
/// </summary>
public class DiscoverySupportTests
{
    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public void GetCurrentUserId_PrefersJellyfinUserIdClaim()
    {
        var jellyfinId = Guid.NewGuid();
        var nameId = Guid.NewGuid();
        var principal = PrincipalWith(
            new Claim("Jellyfin-UserId", jellyfinId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, nameId.ToString()));

        Assert.Equal(jellyfinId, DiscoverySupport.GetCurrentUserId(principal));
    }

    [Fact]
    public void GetCurrentUserId_FallsBackToNameIdentifier()
    {
        var nameId = Guid.NewGuid();
        var principal = PrincipalWith(new Claim(ClaimTypes.NameIdentifier, nameId.ToString()));

        Assert.Equal(nameId, DiscoverySupport.GetCurrentUserId(principal));
    }

    [Fact]
    public void GetCurrentUserId_UnparseableClaim_ReturnsNull()
    {
        var principal = PrincipalWith(new Claim("Jellyfin-UserId", "not-a-guid"));

        Assert.Null(DiscoverySupport.GetCurrentUserId(principal));
    }

    [Fact]
    public void GetCurrentUserId_NoClaims_ReturnsNull()
    {
        Assert.Null(DiscoverySupport.GetCurrentUserId(PrincipalWith()));
    }

    [Fact]
    public void GetCurrentUserId_NullPrincipal_ReturnsNull()
    {
        Assert.Null(DiscoverySupport.GetCurrentUserId(null));
    }

    [Fact]
    public void BuildExcludedItemKeys_UnionsDismissedAndRequested()
    {
        var userId = Guid.NewGuid();
        var store = new Mock<IDiscoveryFeedbackStore>();
        store.Setup(s => s.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)> { (1, "movie"), (2, "tv") });
        store.Setup(s => s.GetRequestedItems(userId))
            .Returns(new HashSet<(int, string)> { (2, "tv"), (3, "movie") });

        var result = DiscoverySupport.BuildExcludedItemKeys(store.Object, userId, _ => { });

        Assert.Equal(3, result.Count);
        Assert.Contains((1, "movie"), result);
        Assert.Contains((2, "tv"), result);
        Assert.Contains((3, "movie"), result);
    }

    [Fact]
    public void BuildExcludedItemKeys_EmptyStores_ReturnsEmptySet()
    {
        var userId = Guid.NewGuid();
        var store = new Mock<IDiscoveryFeedbackStore>();
        store.Setup(s => s.GetDismissedItems(userId)).Returns(new HashSet<(int, string)>());
        store.Setup(s => s.GetRequestedItems(userId)).Returns(new HashSet<(int, string)>());

        var result = DiscoverySupport.BuildExcludedItemKeys(store.Object, userId, _ => { });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildExcludedItemKeys_NonFatalFailure_InvokesOnErrorAndReturnsPartial()
    {
        var userId = Guid.NewGuid();
        var store = new Mock<IDiscoveryFeedbackStore>();
        store.Setup(s => s.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)> { (1, "movie") });
        store.Setup(s => s.GetRequestedItems(userId))
            .Throws(new InvalidOperationException("store unavailable"));

        Exception? captured = null;
        var result = DiscoverySupport.BuildExcludedItemKeys(store.Object, userId, ex => captured = ex);

        Assert.NotNull(captured);
        Assert.IsType<InvalidOperationException>(captured);
        // The dismissed items gathered before the failure are retained.
        Assert.Contains((1, "movie"), result);
    }

    [Fact]
    public void BuildExcludedItemKeys_FatalException_Propagates()
    {
        var userId = Guid.NewGuid();
        var store = new Mock<IDiscoveryFeedbackStore>();
        store.Setup(s => s.GetDismissedItems(userId)).Throws(new OutOfMemoryException());

        Assert.Throws<OutOfMemoryException>(
            () => DiscoverySupport.BuildExcludedItemKeys(store.Object, userId, _ => { }));
    }
}
