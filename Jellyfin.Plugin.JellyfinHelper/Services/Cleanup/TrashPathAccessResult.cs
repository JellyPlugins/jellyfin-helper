namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
///     Result of a filesystem access check for a trash path.
///     Used to proactively verify read/write permissions before performing
///     trash operations (relocate, delete).
/// </summary>
public sealed class TrashPathAccessResult
{
    /// <summary>
    ///     Gets a value indicating whether the path already exists on disk.
    /// </summary>
    public bool Exists { get; init; }

    /// <summary>
    ///     Gets a value indicating whether the Jellyfin process can read from the path.
    ///     When the path does not exist, this reflects readability of the nearest existing parent.
    /// </summary>
    public bool CanRead { get; init; }

    /// <summary>
    ///     Gets a value indicating whether the Jellyfin process can write to the path.
    ///     When the path does not exist, this reflects writability of the nearest existing parent
    ///     (i.e., whether the directory could be created).
    /// </summary>
    public bool CanWrite { get; init; }

    /// <summary>
    ///     Gets an optional human-readable error message explaining why access is denied.
    ///     Null when both <see cref="CanRead"/> and <see cref="CanWrite"/> are true.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Gets a value indicating whether full access (read + write) is available.
    /// </summary>
    public bool HasFullAccess => CanRead && CanWrite;
}