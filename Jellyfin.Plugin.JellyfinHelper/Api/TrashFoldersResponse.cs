using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for trash folder listing endpoints.</summary>
public sealed class TrashFoldersResponse
{
    /// <summary>Gets or sets the resolved trash folder paths.</summary>
    public IReadOnlyList<string> Paths { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the paths are absolute.</summary>
    public bool IsAbsolute { get; set; }
}
