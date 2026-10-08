using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests the Trakt discovery DTO: unlinked/linked envelopes and JSON round-trips of the response payload.
/// </summary>
public sealed class TraktDiscoveryDtoTests
{
    [Fact]
    public void DiscoveryResponse_DefaultsToUnlinkedWithoutResult()
    {
        var dto = new TraktDiscoveryResponse();

        Assert.False(dto.Linked);
        Assert.Null(dto.Result);
    }

    [Fact]
    public void DiscoveryResponse_LinkedCarriesResult()
    {
        var result = new DiscoveryResult { UserId = Guid.NewGuid() };
        var dto = new TraktDiscoveryResponse { Linked = true, Result = result };

        Assert.True(dto.Linked);
        Assert.Same(result, dto.Result);
    }

    [Fact]
    public void DiscoveryResponse_RoundTripsThroughJson()
    {
        var dto = new TraktDiscoveryResponse
        {
            Linked = true,
            Result = new DiscoveryResult
            {
                UserId = Guid.NewGuid(),
                Recommendations =
                [
                    new DiscoveryRecommendation { TmdbId = 11, MediaType = "movie", Title = "Alpha" },
                ],
            },
        };

        var parsed = JsonSerializer.Deserialize<TraktDiscoveryResponse>(JsonSerializer.Serialize(dto));

        Assert.NotNull(parsed);
        Assert.True(parsed!.Linked);
        Assert.NotNull(parsed.Result);
        Assert.Equal(dto.Result!.UserId, parsed.Result!.UserId);
        var rec = Assert.Single(parsed.Result.Recommendations);
        Assert.Equal(11, rec.TmdbId);
        Assert.Equal("Alpha", rec.Title);
    }
}
