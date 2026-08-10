using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Per-library trash information entry for the trash contents response.</summary>
public sealed class TrashLibraryInfo
{
    /// <summary>Gets or sets the library root path.</summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the library display name.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the list of trash items for this library.</summary>
    public IReadOnlyList<TrashItemInfo> Items { get; set; } = [];
}
