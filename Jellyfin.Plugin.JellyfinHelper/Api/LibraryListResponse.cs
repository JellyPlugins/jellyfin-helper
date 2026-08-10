using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for GET /JellyfinHelper/Configuration/Libraries.</summary>
public sealed class LibraryListResponse
{
    /// <summary>Gets or sets the list of available libraries.</summary>
    public IReadOnlyList<LibraryEntry> Libraries { get; set; } = [];
}
