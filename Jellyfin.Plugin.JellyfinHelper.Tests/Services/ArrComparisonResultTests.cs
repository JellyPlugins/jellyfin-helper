using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

public class ArrComparisonResultTests
{
    [Fact]
    public void ArrComparisonResult_DefaultCollections_AreEmpty()
    {
        var result = new ArrComparisonResult();

        Assert.NotNull(result.InBoth);
        Assert.NotNull(result.InArrOnly);
        Assert.NotNull(result.InArrOnlyMissing);
        Assert.NotNull(result.InJellyfinOnly);

        Assert.Empty(result.InBoth);
        Assert.Empty(result.InArrOnly);
        Assert.Empty(result.InArrOnlyMissing);
        Assert.Empty(result.InJellyfinOnly);
    }

    [Fact]
    public void ArrComparisonResult_CanAddItems()
    {
        var result = new ArrComparisonResult();

        result.InBoth.Add("The Matrix");
        result.InArrOnly.Add("Inception");
        result.InArrOnlyMissing.Add("Upcoming Movie");
        result.InJellyfinOnly.Add("Old Movie");

        Assert.Single(result.InBoth);
        Assert.Single(result.InArrOnly);
        Assert.Single(result.InArrOnlyMissing);
        Assert.Single(result.InJellyfinOnly);

        Assert.Contains("The Matrix", result.InBoth);
        Assert.Contains("Inception", result.InArrOnly);
        Assert.Contains("Upcoming Movie", result.InArrOnlyMissing);
        Assert.Contains("Old Movie", result.InJellyfinOnly);
    }

    [Fact]
    public void ArrComparisonResult_MultipleItems_MaintainsOrder()
    {
        var result = new ArrComparisonResult();

        result.InBoth.Add("Movie A");
        result.InBoth.Add("Movie B");
        result.InBoth.Add("Movie C");

        Assert.Equal(3, result.InBoth.Count);
        Assert.Equal("Movie A", result.InBoth[0]);
        Assert.Equal("Movie B", result.InBoth[1]);
        Assert.Equal("Movie C", result.InBoth[2]);
    }
}