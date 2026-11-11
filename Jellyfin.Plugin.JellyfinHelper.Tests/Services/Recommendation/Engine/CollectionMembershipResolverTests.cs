using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Linked memberships, legacy merging, scope, duplicates and provider failure recovery.</summary>
public sealed class CollectionMembershipResolverTests
{
    [Fact]
    public void CollectionIds_UseAuthoritativeMembershipOnceAndRespectExplicitRemoval()
    {
        var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123", ["TmdbCollection"] = "123" } };
        var other = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123", ["TmdbCollection"] = "123" } };
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var calls = 0;
        var ids = CollectionMembershipResolver.CollectionIds([movie, other], [collection], _ => { calls++; return [movie.Id, other.Id]; });
        Assert.Equal(123, ids[movie.Id]);
        Assert.Equal(123, ids[other.Id]);
        Assert.Equal(1, calls);
        collection.LinkedChildren = [LinkedChild.Create(movie)];
        Assert.Equal(0, CollectionMembershipResolver.CollectionIds([movie], [collection], _ => [])[movie.Id]);
        Assert.Equal(123, CollectionMembershipResolver.CollectionIds([movie], [collection])[movie.Id]);
        Assert.Equal(0, CollectionMembershipResolver.CollectionIds([other], [collection], _ => throw new InvalidOperationException())[other.Id]);
    }

    [Fact]
    public void Merge_RecognizesLinkedMoviesWithoutReparentingThem()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var collection = new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(item)] };
        var lookup = new Dictionary<Guid, List<Guid>>();
        CollectionMembershipResolver.Merge(lookup, [item.Id], [collection]);
        Assert.Equal([collection.Id], lookup[item.Id]);
    }

    [Fact]
    public void Merge_UsesAuthoritativeIdsAndKeepsExistingMembershipsWithoutDuplicates()
    {
        var item = Guid.NewGuid();
        var collection = new BoxSet { Id = Guid.NewGuid() };
        var legacy = Guid.NewGuid();
        var lookup = new Dictionary<Guid, List<Guid>> { [item] = [legacy, collection.Id] };
        CollectionMembershipResolver.Merge(lookup, [item], [collection], _ => [item, item, Guid.Empty, Guid.NewGuid()]);
        Assert.Equal([legacy, collection.Id], lookup[item]);
        Assert.Single(lookup);
    }

    [Fact]
    public void Merge_FailedProviderUsesLinkedMetadataAndReportsFailure()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var collection = new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(item)] };
        var lookup = new Dictionary<Guid, List<Guid>>();
        var failures = 0;
        CollectionMembershipResolver.Merge(lookup, [item.Id], [collection], _ => throw new InvalidOperationException("offline"), _ => failures++);
        Assert.Equal(1, failures);
        Assert.Equal([collection.Id], lookup[item.Id]);
    }

    [Fact]
    public void Merge_AbsentIdsAndEmptyCollectionsDoNotLeakMemberships()
    {
        var collection = new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [new LinkedChild(), new LinkedChild { ItemId = Guid.NewGuid(), Type = LinkedChildType.Manual }] };
        var lookup = new Dictionary<Guid, List<Guid>>();
        CollectionMembershipResolver.Merge(lookup, [], [new BoxSet(), collection], _ => null);
        Assert.Empty(lookup);
        CollectionMembershipResolver.Merge(lookup, [], [collection], _ => throw new InvalidOperationException());
        Assert.Empty(lookup);
    }

    [Fact]
    public void ExternalCounts_UseRealProviderBackedCollectionsAndDistinctTitles()
    {
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var rows = new[]
        {
            new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 1, TmdbCollectionId = 123, Played = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 1, TmdbCollectionId = 123, Played = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbCollectionId = 123, IsFavorite = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbCollectionId = 123 },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbCollectionId = 999, Played = true }
        };
        collection.LinkedChildren = rows.Take(3).Select(item => new LinkedChild { ItemId = item.ItemId, Type = LinkedChildType.Manual }).ToArray();
        var counts = CollectionMembershipResolver.ExternalCounts(rows, [collection, new BoxSet()]);
        Assert.NotNull(counts);
        Assert.Equal(2, counts[123]);
        Assert.Single(counts);
        Assert.Empty(CollectionMembershipResolver.ExternalCounts(rows, [])!);
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.ExternalCounts(null!, []));
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.ExternalCounts([], null!));
    }

    [Fact]
    public void ExternalCounts_EmptyOrManuallyEditedBoxsetsDoNotInventProgress()
    {
        var first = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 1, TmdbCollectionId = 123, Played = true };
        var removed = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 2, TmdbCollectionId = 123, Played = true };
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        Assert.Empty(CollectionMembershipResolver.ExternalCounts([first, removed], [collection])!);
        collection.LinkedChildren = [new LinkedChild { ItemId = first.ItemId, Type = LinkedChildType.Manual }];
        Assert.Equal(1, CollectionMembershipResolver.ExternalCounts([first, removed], [collection])![123]);
        Assert.Empty(CollectionMembershipResolver.ExternalCounts([first, removed], [collection], _ => [])!);
    }

    [Fact]
    public void FailedMembershipQuery_IsUnknownRatherThanAConfirmedEmptyCollection()
    {
        var item = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 1, TmdbCollectionId = 123, Played = true };
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        Assert.Null(CollectionMembershipResolver.ExternalCounts([item], [collection], _ => throw new InvalidOperationException("offline")));
        Assert.Null(CollectionMembershipResolver.ExternalCounts([item], [collection], _ => null));
        Assert.Empty(CollectionMembershipResolver.ExternalCounts([item], [collection], _ => [])!);
    }

    [Fact]
    public void Merge_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.Merge(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.Merge([], null!, []));
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.Merge([], [], null!));
    }
}
