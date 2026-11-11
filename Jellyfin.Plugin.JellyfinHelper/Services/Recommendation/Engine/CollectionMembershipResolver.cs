using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Resolves modern linked BoxSets without confusing collection membership with filesystem parents.</summary>
internal static class CollectionMembershipResolver
{
    /// <summary>Corroborates ambiguous collection IDs using the same membership source as progression, queried once per BoxSet.</summary>
    /// <param name="items">Items whose provider identities are being captured.</param>
    /// <param name="collections">Optional library collections.</param>
    /// <param name="linkedChildren">Optional authoritative Jellyfin membership query.</param>
    /// <returns>Confirmed collection IDs; zero means unknown or unsupported.</returns>
    internal static Dictionary<Guid, int> CollectionIds(IEnumerable<BaseItem> items, IEnumerable<BoxSet> collections, Func<Guid, IReadOnlyList<Guid>?>? linkedChildren = null)
    {
        var candidates = items.ToArray();
        var boxsets = collections.ToArray();
        var membership = new Dictionary<Guid, List<Guid>>();
        Merge(membership, candidates.Where(ContentAffinityResolver.NeedsCollectionCorroboration).Select(item => item.Id).ToHashSet(), boxsets, linkedChildren);
        return candidates.ToDictionary(item => item.Id, item => ContentAffinityResolver.ResolveTmdbCollectionId(item, boxsets, membership));
    }

    /// <summary>Counts engaged titles in provider-backed BoxSets, including external titles not installed yet.</summary>
    /// <param name="history">Only the current user's allowed watch history.</param>
    /// <param name="collections">Real library BoxSets, not inferred title prefixes.</param>
    /// <param name="linkedChildren">Optional authoritative membership query, shared with the library path.</param>
    /// <returns>TMDb collection IDs mapped to distinct engaged title counts.</returns>
    internal static Dictionary<int, int>? ExternalCounts(IEnumerable<WatchedItemInfo> history, IEnumerable<BoxSet> collections, Func<Guid, IReadOnlyList<Guid>?>? linkedChildren = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(collections);
        var engaged = history.Where(item => item.HasMeaningfulInteraction()).ToList();
        var boxsets = collections.ToList();
        var membership = new Dictionary<Guid, List<Guid>>();
        if (!Merge(membership, engaged.Select(item => item.ItemId).ToHashSet(), boxsets, linkedChildren))
        {
            return null;
        }

        var counts = new Dictionary<int, int>();
        foreach (var group in boxsets.GroupBy(ProviderCollectionId).Where(group => group.Key > 0))
        {
            var collectionIds = group.Select(collection => collection.Id).ToHashSet();
            var count = engaged.Where(item => membership.TryGetValue(item.ItemId, out var members) && members.Any(collectionIds.Contains))
                .Select(item => item.TmdbId > 0 ? $"tmdb:{item.TmdbId}" : $"item:{item.ItemId:N}").Distinct(StringComparer.Ordinal).Count();
            if (count > 0)
            {
                counts[group.Key] = count;
            }
        }

        return counts;
    }

    /// <summary>Merges linked collection memberships into the parent lookup, scoped to allowed candidates.</summary>
    /// <param name="lookup">The parent-hierarchy fallback, updated in place.</param>
    /// <param name="allowedItems">Only these item IDs may acquire collection memberships.</param>
    /// <param name="collections">The server's BoxSets.</param>
    /// <param name="linkedChildren">Optional authoritative Jellyfin linked-children query.</param>
    /// <param name="onFailure">Reports failed queries without losing other collections.</param>
    /// <returns>Whether all authoritative membership queries succeeded; false must not become an observed zero.</returns>
    internal static bool Merge(
        Dictionary<Guid, List<Guid>> lookup,
        HashSet<Guid> allowedItems,
        IEnumerable<BoxSet> collections,
        Func<Guid, IReadOnlyList<Guid>?>? linkedChildren = null,
        Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(allowedItems);
        ArgumentNullException.ThrowIfNull(collections);
        var complete = true;
        foreach (var collection in collections)
        {
            if (collection.Id == Guid.Empty)
            {
                continue;
            }

            var resolved = ReadChildren(collection, linkedChildren, onFailure);
            complete &= resolved.Complete;
            AddMemberships(lookup, allowedItems, collection.Id, resolved.Children);
        }

        return complete;
    }

    private static int ProviderCollectionId(BoxSet collection)
        => int.TryParse(collection.ProviderIds.GetValueOrDefault("Tmdb"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0;

    private static (IReadOnlyList<Guid> Children, bool Complete) ReadChildren(BoxSet collection, Func<Guid, IReadOnlyList<Guid>?>? linkedChildren, Action<Exception>? onFailure)
    {
        if (linkedChildren is null)
        {
            return (LegacyChildren(collection), true);
        }

        try
        {
            var children = linkedChildren(collection.Id);
            return (children ?? LegacyChildren(collection), children is not null);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            onFailure?.Invoke(ex);
            return (LegacyChildren(collection), false);
        }
    }

    private static Guid[] LegacyChildren(BoxSet collection)
        => collection.LinkedChildren.Where(child => child.Type == LinkedChildType.Manual && child.ItemId.HasValue)
            .Select(child => child.ItemId.GetValueOrDefault()).ToArray();

    private static void AddMemberships(Dictionary<Guid, List<Guid>> lookup, HashSet<Guid> allowedItems, Guid collectionId, IReadOnlyList<Guid> children)
    {
        foreach (var childId in children.Where(childId => childId != Guid.Empty && allowedItems.Contains(childId)))
        {
            if (!lookup.TryGetValue(childId, out var memberships))
            {
                memberships = [];
                lookup[childId] = memberships;
            }

            if (!memberships.Contains(collectionId))
            {
                memberships.Add(collectionId);
            }
        }
    }
}
