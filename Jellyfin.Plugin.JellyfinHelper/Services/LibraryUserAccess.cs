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

        var query = new InternalItemsQuery { User = user, Recursive = true };
        if (itemIds is not null)
        {
            query.ItemIds = itemIds.ToArray();
            return Query(library, user, query);
        }

        query.MediaTypes = [MediaType.Video];
        query.IsFolder = false;
        var allowed = Query(library, user, query);
        allowed.UnionWith(Query(library, user, new InternalItemsQuery { User = user, Recursive = true, IncludeItemTypes = [BaseItemKind.Series], IsFolder = true }));
        return allowed;
    }

    private static HashSet<Guid> Query(ILibraryManager library, User user, InternalItemsQuery query)
    {
        library.ConfigureUserAccess(query, user);
        var ids = library.GetItemIds(query) ?? throw new IOException("Jellyfin user-access query is unavailable.");
        return ids.ToHashSet();
    }
}
