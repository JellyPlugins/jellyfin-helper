using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Tests for <see cref="SsrfGuard" />.
///     Contract: well-known cloud instance-metadata hosts must be blocked; ordinary LAN/loopback/
///     public hosts must be allowed (Arr/Seerr commonly run on the LAN, so private ranges are not
///     blocked by design).
/// </summary>
public sealed class SsrfGuardTests
{
    [Theory]
    [InlineData("169.254.169.254")]     // AWS / Azure IMDS
    [InlineData("metadata.google.internal")] // GCP
    [InlineData("100.100.100.200")]     // Alibaba
    [InlineData("fd00:ec2::254")]       // AWS IPv6 (bare)
    [InlineData("[fd00:ec2::254]")]     // AWS IPv6 (bracketed, as Uri.Host returns it)
    [InlineData("METADATA.GOOGLE.INTERNAL")] // case-insensitive
    public void IsCloudMetadataHost_BlockedHosts_ReturnsTrue(string host)
        => Assert.True(SsrfGuard.IsCloudMetadataHost(host));

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.5")]
    [InlineData("radarr.example.com")]
    [InlineData("seerr.local")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCloudMetadataHost_AllowedHosts_ReturnsFalse(string? host)
        => Assert.False(SsrfGuard.IsCloudMetadataHost(host));

    [Fact]
    public void ThrowIfCloudMetadataHost_BlockedHost_Throws()
    {
        var ex = Assert.Throws<System.ArgumentException>(
            () => SsrfGuard.ThrowIfCloudMetadataHost("169.254.169.254", "baseUrl"));
        Assert.Equal("baseUrl", ex.ParamName);
    }

    [Fact]
    public void ThrowIfCloudMetadataHost_AllowedHost_DoesNotThrow()
        => SsrfGuard.ThrowIfCloudMetadataHost("192.168.1.10", "baseUrl");
}
