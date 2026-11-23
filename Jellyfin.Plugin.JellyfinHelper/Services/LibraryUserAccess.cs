using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>Uses Jellyfin's authoritative user-access query configuration for library eligibility.</summary>
internal static class LibraryUserAccess
{
    /// <summary>Captures permitted IDs without recreating folder, tag or parental policies in plugin code.</summary>
    /// <param name="library">The host library manager.</param>
    /// <param name="user">The current enabled user.</param>
    /// <param name="itemIds">An optional bounded set of requested IDs; null captures video and series access.</param>
    /// <returns>Permitted identities. An unavailable query never becomes permission to use the input.</returns>
    internal static HashSet<Guid> GetAllowedIds(ILibraryManager library, User user, IReadOnlyCollection<Guid>? itemIds = null)
    {
        if (itemIds is { Count: 0 })
        {
            return [];
        }

        var query = CreateQuery(library, user);
        if (itemIds is not null)
        {
            query.ItemIds = itemIds.ToArray();
            return Query(library, query);
        }

        query.MediaTypes = [MediaType.Video];
        query.IsFolder = false;
        var allowed = Query(library, query);
        var series = CreateQuery(library, user);
        series.IncludeItemTypes = [BaseItemKind.Series];
        series.IsFolder = true;
        allowed.UnionWith(Query(library, series));
        return allowed;
    }

    /// <summary>Captures host tag and library restrictions before IDs or ancestors can bypass their population.</summary>
    /// <param name="library">The host library manager.</param>
    /// <param name="user">The current account and content policy.</param>
    /// <returns>A configured query with no ID or ancestor predicate yet.</returns>
    internal static InternalItemsQuery CreateQuery(ILibraryManager library, User user)
    {
        var query = new InternalItemsQuery(user) { Recursive = true, GroupByPresentationUniqueKey = false };
        library.ConfigureUserAccess(query, user);
        return query;
    }

    private static HashSet<Guid> Query(ILibraryManager library, InternalItemsQuery query)
    {
        var ids = library.GetItemIds(query) ?? throw new IOException("Jellyfin user-access query is unavailable.");
        return ids.ToHashSet();
    }
}
