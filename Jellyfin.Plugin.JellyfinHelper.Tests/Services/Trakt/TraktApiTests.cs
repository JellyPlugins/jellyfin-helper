using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the Trakt API base-url resolver. In the unit-test process no override env var is set, so it
///     must resolve to the real Trakt endpoint; the end-to-end stack sets the override to point at the mock.
/// </summary>
public sealed class TraktApiTests
{
    [Fact]
    public void BaseUrl_DefaultsToTraktProductionEndpoint()
    {
        Assert.Equal("https://api.trakt.tv", TraktApi.BaseUrl);
    }

    [Fact]
    public void BaseUrl_HasNoTrailingSlash()
    {
        Assert.False(TraktApi.BaseUrl.EndsWith('/'));
    }
}
