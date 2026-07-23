using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request DTO for relocating trash contents from an old path to a new path.
/// </summary>
public class TrashRelocateRequest
{
    /// <summary>
    ///     Gets the previous trash folder path (before the change).
    /// </summary>
    [StringLength(4096)]
    public string OldTrashPath { get; init; } = string.Empty;

    /// <summary>
    ///     Gets the new trash folder path (after the change).
    /// </summary>
    [StringLength(4096)]
    public string NewTrashPath { get; init; } = string.Empty;
}