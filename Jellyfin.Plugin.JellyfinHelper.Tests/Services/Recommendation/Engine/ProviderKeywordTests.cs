using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Keyword identity and immutable provider labels remain independent.</summary>
public sealed class ProviderKeywordTests
{
    [Fact]
    public void PairedIdentityRoundTripsWithoutInventingMissingLabels()
    {
        var keyword = new ProviderKeyword(42, null);
        Assert.Equal(keyword, JsonSerializer.Deserialize<ProviderKeyword>(JsonSerializer.Serialize(keyword)));
        Assert.Equal(42, keyword.Id);
        Assert.Null(keyword.Name);
    }

    [Fact]
    public void ChangingALabelCreatesANewValueWithoutChangingTheOriginalReference()
    {
        var original = new ProviderKeyword(42, "pirate");
        var translated = original with { Name = "pirata" };
        Assert.Equal("pirate", original.Name);
        Assert.Equal(original.Id, translated.Id);
        Assert.NotEqual(original, translated);
        Assert.Equal(original.GetHashCode(), new ProviderKeyword(42, "pirate").GetHashCode());
    }
}
