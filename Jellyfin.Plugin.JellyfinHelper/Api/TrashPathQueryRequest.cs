namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request DTO for querying trash folders at a specific (possibly non-current) path.
///     Used to check whether trash content exists at the old path before a path change is applied.
/// </summary>
public class TrashPathQueryRequest
{
    /// <summary>
    ///     Gets the trash folder path to query for existing content.
    /// </summary>
    public string TrashFolderPath { get; init; } = string.Empty;
}