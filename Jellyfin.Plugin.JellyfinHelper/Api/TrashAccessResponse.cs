using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for POST /JellyfinHelper/Trash/CheckAccess.</summary>
public sealed class TrashAccessResponse
{
    /// <summary>Gets or sets a value indicating whether all checked paths have full access.</summary>
    public bool AllAccessible { get; set; }

    /// <summary>Gets or sets the per-path access results.</summary>
    public IReadOnlyList<TrashAccessEntry> Results { get; set; } = [];
}
