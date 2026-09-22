using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class WatchedUserDetailTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var detail = new WatchedUserDetail();

        Assert.Equal(string.Empty, detail.Username);
        Assert.Equal(0, detail.PlayCount);
        Assert.Null(detail.LastPlayedDate);
        Assert.False(detail.Played);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var played = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var detail = new WatchedUserDetail
        {
            Username = "Alice",
            PlayCount = 3,
            LastPlayedDate = played,
            Played = true
        };

        Assert.Equal("Alice", detail.Username);
        Assert.Equal(3, detail.PlayCount);
        Assert.Equal(played, detail.LastPlayedDate);
        Assert.True(detail.Played);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        // WatchedDetails persist inside the statistics cache file.
        var detail = new WatchedUserDetail
        {
            Username = "Bob",
            PlayCount = 1,
            LastPlayedDate = new DateTime(2024, 5, 2, 8, 30, 0, DateTimeKind.Utc),
            Played = true
        };

        var loaded = JsonSerializer.Deserialize<WatchedUserDetail>(JsonSerializer.Serialize(detail));

        Assert.NotNull(loaded);
        Assert.Equal("Bob", loaded!.Username);
        Assert.Equal(1, loaded.PlayCount);
        Assert.Equal(detail.LastPlayedDate, loaded.LastPlayedDate);
        Assert.True(loaded.Played);
    }
}
