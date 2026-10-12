using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="ReasonAnchorContext"/>: default and explicit construction of the reason anchors.
/// </summary>
public class ReasonAnchorContextTests
{
    [Fact]
    public void Default_HasNoAnchors()
    {
        var anchors = default(ReasonAnchorContext);

        Assert.Null(anchors.CollectionName);
        Assert.Equal(0, anchors.WatchedSiblingCount);
        Assert.Null(anchors.MatchedCountry);
        Assert.Null(anchors.MatchedWriter);
        Assert.Null(anchors.MatchedTag);
        Assert.Null(anchors.MatchedLanguage);
        Assert.Null(anchors.ReleaseEra);
    }

    [Fact]
    public void Construction_AssignsAllAnchors()
    {
        var anchors = new ReasonAnchorContext(
            CollectionName: "Spider-Man Collection",
            WatchedSiblingCount: 4,
            MatchedCountry: "France",
            MatchedWriter: "Christopher Nolan",
            MatchedTag: "Heist",
            MatchedLanguage: "Japanese",
            ReleaseEra: "2010s");

        Assert.Equal("Spider-Man Collection", anchors.CollectionName);
        Assert.Equal(4, anchors.WatchedSiblingCount);
        Assert.Equal("France", anchors.MatchedCountry);
        Assert.Equal("Christopher Nolan", anchors.MatchedWriter);
        Assert.Equal("Heist", anchors.MatchedTag);
        Assert.Equal("Japanese", anchors.MatchedLanguage);
        Assert.Equal("2010s", anchors.ReleaseEra);
    }
}
