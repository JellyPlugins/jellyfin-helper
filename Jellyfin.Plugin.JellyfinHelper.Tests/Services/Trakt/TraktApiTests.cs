using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the Trakt API base-url resolver. In the unit-test process no override env var is set, so it
///     must resolve to the real Trakt endpoint; the end-to-end stack sets the override to point at the mock.
///     The trimming/fallback rules live in a pure overload so they are testable without process env.
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

    [Fact]
    public void ResolveBaseUrl_TrailingSlashTrimmed()
    {
        Assert.Equal("http://localhost:9100", TraktApi.ResolveBaseUrl("http://localhost:9100/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://host/x")]
    public void ResolveBaseUrl_BlankOrMalformed_FallsBackToDefault(string? fromEnv)
    {
        Assert.Equal("https://api.trakt.tv", TraktApi.ResolveBaseUrl(fromEnv));
    }

    [Theory]
    [InlineData("http://mock-trakt:8080")]
    [InlineData("https://trakt.example.com/api")]
    public void ResolveBaseUrl_HttpOverride_AcceptedAsIs(string fromEnv)
    {
        Assert.Equal(fromEnv, TraktApi.ResolveBaseUrl(fromEnv));
    }
}
