using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Generic negative contracts: unqualified broadcaster labels are not production-company evidence.</summary>
public sealed class ProducerProvenanceBaselineReproTests
{
    [Fact]
    public void NativeNetworkNameAloneMustNotEstablishProducerPreference()
    {
        var id = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [new WatchedItemInfo { ItemId = id, ItemType = "Series", Played = true, Studios = ["Same acronym"] }] };
        var native = new Series { Id = id, Studios = ["Same acronym"] };

        Assert.Empty(PreferenceBuilder.BuildStudioPreferenceSet(profile, new Dictionary<Guid, BaseItem> { [id] = native }));
    }

    [Fact]
    public void LegacyCachedStudioLabelsCannotLaunderProductionIdentity()
    {
        var id = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [new WatchedItemInfo { ItemId = id, ItemType = "Series", Played = true, Studios = ["producer:tmdb:production-company:123"] }] };

        Assert.Empty(TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, new Dictionary<Guid, IReadOnlyList<string>> { [id] = profile.WatchedItems[0].Studios }));
    }
}
