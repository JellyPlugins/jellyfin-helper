using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Security-relevant tests for <see cref="SeerrPermissionExtensions" />. Because this is the
///     authorization gate for Discovery requests, every branch and every "admin bypass" contract
///     must be locked down against accidental changes:
///     <list type="bullet">
///         <item>Zero-flag guard - <c>HasFlag(None)</c> must NEVER pass.</item>
///         <item>Admin bypass - an admin user must satisfy every check.</item>
///         <item>Granular per-media-type flags must be respected only when general flag is missing.</item>
///         <item>Unknown media types must be rejected even for admins (defense in depth).</item>
///         <item>Null user must throw - no silent authorization.</item>
///     </list>
/// </summary>
public class SeerrPermissionExtensionsTests
{
    private static SeerrUser Make(long perms) => new() { Id = 1, DisplayName = "test", Permissions = perms };

    // === HasPermission ===

    [Fact]
    public void HasPermission_NullUser_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SeerrPermissionExtensions.HasPermission(null!, SeerrPermissions.Request));
    }

    [Fact]
    public void HasPermission_NoneFlag_ReturnsFalse_EvenForAdmin()
    {
        // Critical guard: HasFlag(0) would return true otherwise, which is a security hole.
        var admin = Make((long)SeerrPermissions.Admin);
        Assert.False(admin.HasPermission(SeerrPermissions.None));
    }

    [Fact]
    public void HasPermission_NoneFlag_ReturnsFalse_ForRegularUser()
    {
        var user = Make((long)SeerrPermissions.Request);
        Assert.False(user.HasPermission(SeerrPermissions.None));
    }

    [Fact]
    public void HasPermission_Admin_ReturnsTrue_ForAnyFlag()
    {
        var admin = Make((long)SeerrPermissions.Admin);

        // Admin should have every permission, without needing to set each flag
        Assert.True(admin.HasPermission(SeerrPermissions.Request));
        Assert.True(admin.HasPermission(SeerrPermissions.RequestMovie));
        Assert.True(admin.HasPermission(SeerrPermissions.RequestTv));
        Assert.True(admin.HasPermission(SeerrPermissions.Request4K));
        Assert.True(admin.HasPermission(SeerrPermissions.ManageUsers));
        Assert.True(admin.HasPermission(SeerrPermissions.ManageRequests));
        Assert.True(admin.HasPermission(SeerrPermissions.RequestAdvanced));
    }

    [Fact]
    public void HasPermission_UserWithExactFlag_ReturnsTrue()
    {
        var user = Make((long)SeerrPermissions.Request);
        Assert.True(user.HasPermission(SeerrPermissions.Request));
    }

    [Fact]
    public void HasPermission_UserWithoutFlag_ReturnsFalse()
    {
        var user = Make((long)SeerrPermissions.RequestMovie);
        Assert.False(user.HasPermission(SeerrPermissions.RequestTv));
    }

    [Fact]
    public void HasPermission_CombinedFlags_MatchesEach()
    {
        // A user with Request | Vote should have both, not one or the other
        var combined = (long)(SeerrPermissions.Request | SeerrPermissions.Vote);
        var user = Make(combined);
        Assert.True(user.HasPermission(SeerrPermissions.Request));
        Assert.True(user.HasPermission(SeerrPermissions.Vote));
        // But NOT unrelated flags
        Assert.False(user.HasPermission(SeerrPermissions.ManageRequests));
    }

    [Fact]
    public void HasPermission_UserWithZeroPermissions_ReturnsFalseForEverything()
    {
        var user = Make(0);
        Assert.False(user.HasPermission(SeerrPermissions.Request));
        Assert.False(user.HasPermission(SeerrPermissions.RequestMovie));
        Assert.False(user.HasPermission(SeerrPermissions.Admin));
    }

    // === CanRequest ===

    [Fact]
    public void CanRequest_NullUser_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SeerrPermissionExtensions.CanRequest(null!, "movie"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("music")]
    [InlineData("book")]
    [InlineData("garbage")]
    [InlineData(null)]
    public void CanRequest_UnknownMediaType_ReturnsFalse_EvenForAdmin(string? mediaType)
    {
        // Defense in depth: unknown media types must be rejected regardless of permissions.
        // This prevents attackers from smuggling arbitrary types through the request path.
        var admin = Make((long)SeerrPermissions.Admin);
        Assert.False(admin.CanRequest(mediaType!));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("MOVIE")]
    [InlineData("Movie")]
    [InlineData("tv")]
    [InlineData("TV")]
    [InlineData("Tv")]
    public void CanRequest_Admin_AllowsKnownMediaTypes(string mediaType)
    {
        var admin = Make((long)SeerrPermissions.Admin);
        Assert.True(admin.CanRequest(mediaType));
    }

    [Fact]
    public void CanRequest_GeneralRequestFlag_CoversMovieAndTv()
    {
        var user = Make((long)SeerrPermissions.Request);
        Assert.True(user.CanRequest("movie"));
        Assert.True(user.CanRequest("tv"));
    }

    [Fact]
    public void CanRequest_OnlyRequestMovieFlag_DeniesTv()
    {
        var user = Make((long)SeerrPermissions.RequestMovie);
        Assert.True(user.CanRequest("movie"));
        Assert.False(user.CanRequest("tv"));
    }

    [Fact]
    public void CanRequest_OnlyRequestTvFlag_DeniesMovie()
    {
        var user = Make((long)SeerrPermissions.RequestTv);
        Assert.False(user.CanRequest("movie"));
        Assert.True(user.CanRequest("tv"));
    }

    [Fact]
    public void CanRequest_UserWithNoPermissions_DeniesEverything()
    {
        var user = Make(0);
        Assert.False(user.CanRequest("movie"));
        Assert.False(user.CanRequest("tv"));
    }

    [Fact]
    public void CanRequest_MediaTypeCaseInsensitive_ButStillMustBeKnown()
    {
        var user = Make((long)SeerrPermissions.Request);
        Assert.True(user.CanRequest("Movie"));
        Assert.True(user.CanRequest("TV"));
        Assert.False(user.CanRequest("MoViE_"));   // typo -> unknown -> denied
        Assert.False(user.CanRequest(" movie"));   // leading space -> not equal -> denied
    }

    [Fact]
    public void CanRequest_UserWithBothGranularFlags_CoversBothTypes()
    {
        var combined = (long)(SeerrPermissions.RequestMovie | SeerrPermissions.RequestTv);
        var user = Make(combined);
        Assert.True(user.CanRequest("movie"));
        Assert.True(user.CanRequest("tv"));
    }

    // === CanSelectQualityProfile ===

    [Fact]
    public void CanSelectQualityProfile_NullUser_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SeerrPermissionExtensions.CanSelectQualityProfile(null!));
    }

    [Fact]
    public void CanSelectQualityProfile_Admin_ReturnsTrue()
    {
        var admin = Make((long)SeerrPermissions.Admin);
        Assert.True(admin.CanSelectQualityProfile());
    }

    [Fact]
    public void CanSelectQualityProfile_ManageRequests_ReturnsTrue()
    {
        var user = Make((long)SeerrPermissions.ManageRequests);
        Assert.True(user.CanSelectQualityProfile());
    }

    [Fact]
    public void CanSelectQualityProfile_RequestAdvanced_ReturnsTrue()
    {
        var user = Make((long)SeerrPermissions.RequestAdvanced);
        Assert.True(user.CanSelectQualityProfile());
    }

    [Fact]
    public void CanSelectQualityProfile_OnlyBasicRequest_ReturnsFalse()
    {
        // A user with only Request must NOT be able to pick a quality profile
        // (advanced request flow is gated).
        var user = Make((long)SeerrPermissions.Request);
        Assert.False(user.CanSelectQualityProfile());
    }

    [Fact]
    public void CanSelectQualityProfile_NoPermissions_ReturnsFalse()
    {
        var user = Make(0);
        Assert.False(user.CanSelectQualityProfile());
    }

    // === Bit-flag combinations: real-world scenarios ===

    [Fact]
    public void RealWorldScenario_TypicalUser_CanRequestButNotChooseProfile()
    {
        // Most users: Request + Watchlist + WatchlistView
        var typical = (long)(SeerrPermissions.Request
                             | SeerrPermissions.Watchlist
                             | SeerrPermissions.WatchlistView);
        var user = Make(typical);

        Assert.True(user.CanRequest("movie"));
        Assert.True(user.CanRequest("tv"));
        Assert.False(user.CanSelectQualityProfile());
    }

    [Fact]
    public void RealWorldScenario_PowerUser_CanRequestAndChooseProfile()
    {
        // Power users: Request + RequestAdvanced
        var power = (long)(SeerrPermissions.Request | SeerrPermissions.RequestAdvanced);
        var user = Make(power);

        Assert.True(user.CanRequest("movie"));
        Assert.True(user.CanSelectQualityProfile());
    }

    [Fact]
    public void RealWorldScenario_RestrictedUser_OnlyTvNoProfile()
    {
        var user = Make((long)SeerrPermissions.RequestTv);

        Assert.False(user.CanRequest("movie"));
        Assert.True(user.CanRequest("tv"));
        Assert.False(user.CanSelectQualityProfile());
    }
}