using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

public sealed class SeerrTitleStatusTests
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(4, false, true)]
    [InlineData(5, false, true)]
    [InlineData(6, false, false)]
    [InlineData(7, false, false)]
    public void MediaStatus_DistinguishesRequestsFromOwnership(int status, bool requested, bool available)
    {
        var result = SeerrTitleStatus.Parse($$"""{"id":123,"mediaInfo":{"tmdbId":123,"mediaType":"movie","status":{{status}} } }""", 123, "movie");
        Assert.True(result.Verified);
        Assert.Equal(requested, result.AlreadyRequested);
        Assert.Equal(available, result.AlreadyAvailable);
    }

    [Theory]
    [InlineData("{\"id\":123}")]
    [InlineData("{\"id\":123,\"mediaInfo\":null}")]
    public void UntrackedTitle_IsVerifiedButNeitherRequestedNorOwned(string json)
        => Assert.Equal(new SeerrTitleStatus(true, false, false), SeerrTitleStatus.Parse(json, 123, "movie"));

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(5, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    public void RequestEvidence_OnlyAcceptsActiveStandardRequests(int status, bool fourK, bool expected)
    {
        var result = SeerrTitleStatus.Parse($$"""{"id":123,"mediaInfo":{"status":1,"requests":[{"status":{{status}},"is4k":{{fourK.ToString().ToLowerInvariant()}} }] } }""", 123, "movie");
        Assert.True(result.Verified);
        Assert.Equal(expected, result.AlreadyRequested);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void DeletedMedia_OnlyConfirmsIntentForAnActiveStandardRequest(int requestStatus, bool requested)
    {
        var result = SeerrTitleStatus.Parse($$"""{"id":123,"mediaInfo":{"status":7,"requests":[{"status":{{requestStatus}},"is4k":false}] } }""", 123, "movie");
        Assert.True(result.Verified);
        Assert.False(result.AlreadyAvailable);
        Assert.Equal(requested, result.AlreadyRequested);
    }

    [Fact]
    public void BlocklistedMedia_DoesNotConfirmIntentEvenWithAnOlderActiveRequest()
    {
        var result = SeerrTitleStatus.Parse("""{"id":123,"mediaInfo":{"status":6,"requests":[{"status":2,"is4k":false}]}}""", 123, "movie");
        Assert.True(result.Verified);
        Assert.False(result.AlreadyAvailable);
        Assert.False(result.AlreadyRequested);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"id\":124,\"mediaInfo\":{\"status\":2}}")]
    [InlineData("{\"id\":123,\"mediaType\":\"tv\",\"mediaInfo\":{\"status\":2}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"tmdbId\":124,\"status\":2}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"mediaType\":\"tv\",\"status\":2}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"status\":\"2\"}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"status\":99}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"status\":8}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"status\":2,\"requests\":{}}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"status\":2,\"requests\":[{}]}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"status\":1,\"requests\":[{\"status\":99}]}}")]
    [InlineData("{\"id\":123,\"mediaInfo\":{\"status\":1,\"requests\":[{\"status\":2,\"is4k\":\"true\"}]}}")]
    public void UntrustedOrMismatchedPayloads_NeverConfirmDuplicates(string json)
    {
        var result = SeerrTitleStatus.Parse(json, 123, "movie");
        Assert.False(result.Verified);
        Assert.False(result.AlreadyRequested);
    }
}
