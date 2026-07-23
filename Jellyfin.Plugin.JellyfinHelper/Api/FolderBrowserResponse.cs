using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for GET /JellyfinHelper/Configuration/LibraryPaths.</summary>
public sealed class FolderBrowserResponse
{
    /// <summary>Gets or sets the list of library path entries.</summary>
    public IReadOnlyList<LibraryPathEntry> LibraryPaths { get; set; } = [];
}
