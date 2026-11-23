using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using HostPlaylist = MediaBrowser.Controller.Playlists.Playlist;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>Reads owner-scoped playlist IDs from the host database without viewer visibility or row-wide materialization.</summary>
internal sealed class HostedPlaylistInventory
{
    private const int MaximumOwnedIds = 4096;
    private readonly ILibraryManager _library;
    private readonly Func<Guid, Guid[]> _captureIds;

    internal HostedPlaylistInventory(ILibraryManager library, IDbContextFactory<JellyfinDbContext> database)
        : this(library, owner =>
        {
            using var context = database.CreateDbContext();
            return CaptureOwnedIds(context.BaseItems.AsNoTracking(), owner);
        })
    {
    }

    internal HostedPlaylistInventory(ILibraryManager library, Func<Guid, Guid[]> captureIds)
    {
        _library = library;
        _captureIds = captureIds;
    }

    internal IReadOnlyList<BaseItem> LoadOwned(Guid owner)
    {
        var ids = _captureIds(owner);
        if (ids.Length == 0)
        {
            return [];
        }

        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Playlist], ItemIds = ids,
            Recursive = true, GroupByPresentationUniqueKey = false
        }) ?? throw new IOException("Owned playlist rows are unavailable.");
        var validItems = items.OfType<BaseItem>().ToArray();
        var captured = ids.ToHashSet();
        if (!captured.IsSubsetOf(validItems.Select(item => item.Id)))
        {
            throw new IOException("The owner-scoped playlist inventory is incomplete.");
        }

        if (validItems.Any(item => captured.Contains(item.Id) && (item is not HostPlaylist playlist || playlist.OwnerUserId == Guid.Empty)))
        {
            throw new IOException("The owner-scoped playlist inventory contains unresolved ownership.");
        }

        return validItems.OfType<HostPlaylist>().Where(item => captured.Contains(item.Id) && item.OwnerUserId == owner).Cast<BaseItem>().ToArray();
    }

    internal static Guid[] CaptureOwnedIds(IQueryable<BaseItemEntity> items, Guid owner)
    {
        if (owner == Guid.Empty)
        {
            throw new ArgumentException("A playlist owner is required.", nameof(owner));
        }

        // Jellyfin 12.4 stores Playlist.OwnerUserId in its serialized Data, not
        // BaseItemEntity.OwnerId (which owns parts/extras). These predicates run
        // host-side before IDs are projected; no User/viewer policy is applied.
        var compact = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\"OwnerUserId\":\"{owner:N}\"");
        var legacy = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\"OwnerUserId\":\"{owner:D}\"");
        var type = typeof(HostPlaylist).FullName;
        var ids = items.Where(item => item.Type == type && item.Data != null && (item.Data.Contains(compact) || item.Data.Contains(legacy)))
            .Select(item => item.Id).Distinct().Take(MaximumOwnedIds + 1).ToArray();
        if (ids.Length > MaximumOwnedIds || ids.Contains(Guid.Empty))
        {
            throw new IOException("The owner playlist inventory exceeds its bounded ID limit.");
        }

        return ids;
    }
}
