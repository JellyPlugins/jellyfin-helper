using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request DTO for querying trash folders at a specific (possibly non-current) path.
/// </summary>
public class TrashPathQueryRequest
{
    /// <summary>
    ///     Gets the trash folder path to query for existing content.
    /// </summary>
    [StringLength(4096)]
    public string TrashFolderPath { get; init; } = string.Empty;
}