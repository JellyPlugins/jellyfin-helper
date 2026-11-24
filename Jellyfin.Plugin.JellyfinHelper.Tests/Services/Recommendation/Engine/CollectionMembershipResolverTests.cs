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
    public void CollectionIds_DuplicateLibraryIdsDoNotDiscardOtherTitlesOrConfirmedMemberships()
    {
        var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123", ["TmdbCollection"] = "123" } };
        var unrelated = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "456" } };
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var calls = 0;
        var ids = CollectionMembershipResolver.CollectionIds([movie, unrelated, unrelated, movie], [collection], _ => { calls++; return [movie.Id]; });
        Assert.Equal(2, ids.Count);
        Assert.Equal(123, ids[movie.Id]);
        Assert.Equal(0, ids[unrelated.Id]);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedSecondMembershipReadCannotMakeTheFirstCollectionLookUnambiguous(bool failSecond)
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var direct = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["TmdbCollection"] = "999" } };
        var first = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "123" } };
        var second = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "456" } };
        var calls = 0;
        var ids = CollectionMembershipResolver.CollectionIds([movie, direct], [first, second], id =>
        {
            calls++;
            if (id == second.Id && failSecond) throw new IOException("authoritative membership unavailable");
            return id == first.Id ? [movie.Id, direct.Id] : [];
        });
        Assert.Equal(failSecond ? 0 : 123, ids[movie.Id]);
        Assert.Equal(999, ids[direct.Id]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Merge_CancelledMembershipQueryPropagatesWithoutLegacyFallbackOrFailureLogging()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var collection = new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(movie)] };
        var lookup = new Dictionary<Guid, List<Guid>>();
        var cancellation = new OperationCanceledException("cancelled");
        var failures = 0;
        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() =>
            CollectionMembershipResolver.Merge(lookup, [movie.Id], [collection], _ => throw cancellation, _ => failures++)));
        Assert.Empty(lookup);
        Assert.Equal(0, failures);
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
    public void ExternalCounts_UnionDistinctTitlesAcrossBoxsetsWithTheSameProviderIdentity()
    {
        var first = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 1, Played = true };
        var duplicate = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 1, Played = true };
        var second = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 2, Played = true };
        var collectionA = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var collectionB = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var counts = CollectionMembershipResolver.ExternalCounts([first, second], [collectionA, collectionB], id => id == collectionA.Id ? [first.ItemId] : [second.ItemId]);
        Assert.Equal(2, Assert.Single(counts!).Value);
        Assert.Equal(0.5, EngineConstants.ComputeCollectionProgressionBoost(counts![123]), 12);
        var duplicateCopies = CollectionMembershipResolver.ExternalCounts([first, duplicate], [collectionA, collectionB], id => id == collectionA.Id ? [first.ItemId] : [duplicate.ItemId]);
        Assert.Equal(1, Assert.Single(duplicateCopies!).Value);
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
    public void ExternalCounts_UsesParentMembershipAndPrefersKnownSeriesIdentityAcrossMixedEpisodeRows()
    {
        var series = Guid.NewGuid();
        var duplicateSeries = Guid.NewGuid();
        var rows = new[]
        {
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = series, TmdbId = 801, Played = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = series, TmdbId = 802, SeriesTmdbId = 50, Played = true },
            new WatchedItemInfo { ItemId = duplicateSeries, ItemType = "Series", TmdbId = 50, IsFavorite = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = Guid.NewGuid(), SeriesTmdbId = 51 }
        };
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var allowed = CollectionMembershipResolver.EngagedItemIds(rows);
        Assert.True(allowed.SetEquals([series, duplicateSeries]));
        var snapshot = CollectionMembershipResolver.CaptureMemberships([collection], allowed, _ => [series, duplicateSeries, rows[3].SeriesId!.Value]);
        Assert.Equal(1, CollectionMembershipResolver.ExternalCounts(rows, snapshot)![123]);
        Assert.Equal(1, CollectionMembershipResolver.ExternalCounts(rows.Reverse(), snapshot)![123]);
        Assert.DoesNotContain(rows[0].ItemId, snapshot.Memberships.Keys);
        Assert.DoesNotContain(rows[3].SeriesId!.Value, snapshot.Memberships.Keys);
        // Episode membership alone is not parent-series membership.
        Assert.Empty(CollectionMembershipResolver.ExternalCounts(rows, [collection], _ => [rows[0].ItemId, rows[1].ItemId])!);
    }

    [Fact]
    public void CapturedMemberships_AreReadOnceButCountsRemainScopedToEachUsersHistory()
    {
        var first = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 1, Played = true };
        var second = new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 2, Played = true };
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var reads = 0;
        var snapshot = CollectionMembershipResolver.CaptureMemberships([collection], [first.ItemId, second.ItemId], _ => { reads++; return [first.ItemId, second.ItemId]; });
        Assert.Equal(1, CollectionMembershipResolver.ExternalCounts([first], snapshot)![123]);
        Assert.Equal(1, CollectionMembershipResolver.ExternalCounts([second], snapshot)![123]);
        Assert.Empty(CollectionMembershipResolver.ExternalCounts([], snapshot)!);
        Assert.Equal(1, reads);
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.ExternalCounts(null!, snapshot));
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.ExternalCounts([], (CollectionMembershipResolver.Snapshot)null!));
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.ExternalCounts([], (IEnumerable<BoxSet>)null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedMemberships_FailedReadRemainsUnknownEvenWhenLegacyLinksContainPositiveEvidence(bool throws)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" }, LinkedChildren = [LinkedChild.Create(item)] };
        var snapshot = CollectionMembershipResolver.CaptureMemberships([collection], [item.Id], _ => throws ? throw new InvalidOperationException("offline") : null);
        Assert.False(snapshot.Complete);
        Assert.Equal([collection.Id], snapshot.Memberships[item.Id]);
        Assert.Null(CollectionMembershipResolver.ExternalCounts([new WatchedItemInfo { ItemId = item.Id, Played = true }], snapshot));
    }

    [Fact]
    public void Merge_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.Merge(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.Merge([], null!, []));
        Assert.Throws<ArgumentNullException>(() => CollectionMembershipResolver.Merge([], [], null!));
    }

    [Fact]
    public void CanonicalCollectionCountsIntersectPhysicalCopiesBeforeDedupAndKeepMovieTvNamespaces()
    {
        var denied = Guid.NewGuid();
        var allowed = Guid.NewGuid();
        var series = Guid.NewGuid();
        var seriesCopy = Guid.NewGuid();
        var box = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "991" } };
        var rows = new[]
        {
            new WatchedItemInfo { ItemId = denied, ItemType = "Movie", TmdbId = 701, Played = true },
            new WatchedItemInfo { ItemId = allowed, ItemType = "Movie", TmdbId = 701, Played = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = series, TmdbId = 701, Played = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = series, SeriesTmdbId = 701, Played = true },
            new WatchedItemInfo { ItemId = series, ItemType = "Series", TmdbId = 701, IsFavorite = true },
            new WatchedItemInfo { ItemId = seriesCopy, ItemType = "Series", TmdbId = 701, Played = true },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", TmdbId = 702 }
        };
        var snapshot = CollectionMembershipResolver.CaptureMemberships([box], [allowed, series, seriesCopy], _ => [denied, allowed, series, seriesCopy]);
        var native = CollectionMembershipResolver.WatchedCounts(rows, snapshot.Memberships, allowedItems: new HashSet<Guid> { allowed, series, seriesCopy });
        Assert.Equal(2, Assert.Single(native).Value);
        Assert.Equal(2, CollectionMembershipResolver.ExternalCounts(rows, snapshot)![991]);
        Assert.Equal(2, CollectionMembershipResolver.WatchedCounts(rows.Reverse(), snapshot.Memberships, allowedItems: new HashSet<Guid> { allowed, series, seriesCopy })[box.Id]);
        Assert.DoesNotContain(denied, snapshot.Memberships.Keys);
        Assert.DoesNotContain(rows[2].ItemId, snapshot.Memberships.Keys);
    }

    [Fact]
    public void CanonicalCollectionFallbackDoesNotMergeNamesAndOnlyVerifiedProviderAliasesShareCounts()
    {
        var orphan = new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", TmdbId = 701, Played = true };
        Assert.Equal($"tv:item:{orphan.ItemId:N}", CollectionMembershipResolver.EngagedTitle(orphan).Key);
        var first = new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Name = "Same display name", Played = true };
        var second = new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Name = "Same display name", Played = true };
        var unlinked = new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true };
        var firstBox = Guid.NewGuid();
        var secondBox = Guid.NewGuid();
        var otherBox = Guid.NewGuid();
        var emptyAlias = Guid.NewGuid();
        var memberships = new Dictionary<Guid, IReadOnlyList<Guid>> { [first.ItemId] = [firstBox, firstBox, Guid.Empty], [second.ItemId] = [firstBox, otherBox] };
        var providers = new Dictionary<Guid, int> { [firstBox] = 991, [secondBox] = 991, [otherBox] = 992, [emptyAlias] = 993, [Guid.Empty] = 991 };
        var counts = CollectionMembershipResolver.WatchedCounts([first, second, unlinked], memberships, providers);
        Assert.Equal(2, counts[firstBox]);
        Assert.Equal(2, counts[secondBox]);
        Assert.Equal(1, counts[otherBox]);
        Assert.DoesNotContain(emptyAlias, counts.Keys);
        Assert.DoesNotContain(Guid.Empty, counts.Keys);
        Assert.Empty(CollectionMembershipResolver.WatchedCounts([first, second], memberships, providers, new HashSet<Guid>()));
        Assert.Equal(2, CollectionMembershipResolver.WatchedCounts([first, second], memberships)[firstBox]);
        Assert.Equal(first.ItemId, CollectionMembershipResolver.EngagedTitle(first).ItemId);
    }

    [Fact]
    public void CanonicalCollectionIncompleteRosterCannotCorroborateSelfIdsEvenWithLegacyPositiveLinks()
    {
        var item = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "701", ["TmdbCollection"] = "701" } };
        var box = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "701" }, LinkedChildren = [LinkedChild.Create(item)] };
        Assert.Equal(0, CollectionMembershipResolver.CollectionIds([item], [box], _ => null)[item.Id]);
        Assert.Equal(0, CollectionMembershipResolver.CollectionIds([item], [box], _ => throw new IOException("offline"))[item.Id]);
        Assert.Equal(701, CollectionMembershipResolver.CollectionIds([item], [box], _ => [item.Id])[item.Id]);
    }
}
