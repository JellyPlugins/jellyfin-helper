using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for GET /JellyfinHelper/Trash/Config.</summary>
public sealed class TrashConfigResponse
{
    /// <summary>Gets or sets a value indicating whether the trash feature is enabled.</summary>
    public bool UseTrash { get; set; }

    /// <summary>Gets or sets the number of days items are retained in trash before permanent deletion.</summary>
    public int RetentionDays { get; set; }

    /// <summary>Gets or sets the per-library trash information.</summary>
    public IReadOnlyList<TrashLibraryInfo> Libraries { get; set; } = [];
}
