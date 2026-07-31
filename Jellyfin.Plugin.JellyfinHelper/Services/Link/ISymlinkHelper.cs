namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Abstraction for symbolic link operations to enable testing
///     without requiring real filesystem symlinks.
/// </summary>
public interface ISymlinkHelper
{
    /// <summary>
    ///     Determines whether the given path is a symbolic link.
    /// </summary>
    /// <param name="path">The file path to check.</param>
    /// <returns>True if the path is a symbolic link; otherwise, false.</returns>
    bool IsSymlink(string path);

    /// <summary>
    ///     Gets the target path of a symbolic link.
    /// </summary>
    /// <param name="path">The symbolic link path.</param>
    /// <returns>The target path, or null if not a symlink or the target cannot be read.</returns>
    string? GetSymlinkTarget(string path);

    /// <summary>
    ///     Creates a symbolic link at the specified path pointing to the given target.
    /// </summary>
    /// <param name="linkPath">The path where the symlink should be created.</param>
    /// <param name="targetPath">The target path the symlink should point to.</param>
    void CreateSymlink(string linkPath, string targetPath);

    /// <summary>
    ///     Deletes a symbolic link (without following it to the target).
    /// </summary>
    /// <param name="linkPath">The symlink path to delete.</param>
    void DeleteSymlink(string linkPath);

    /// <summary>
    ///     Atomically replaces <paramref name="destPath"/> with the symlink at
    ///     <paramref name="sourcePath"/>. On Linux this maps to <c>rename(2)</c>;
    ///     on Windows it uses <see cref="System.IO.File.Move(string,string,bool)"/> with overwrite.
    /// </summary>
    /// <remarks>
    ///     Implementations MUST re-verify that <paramref name="destPath"/> is still a symbolic link
    ///     immediately before overwriting and throw <see cref="System.InvalidOperationException"/> if
    ///     it is not — a real file may have taken its place since the scan, and overwriting it would
    ///     be irreversible data loss. If <paramref name="destPath"/> no longer exists, the move proceeds.
    /// </remarks>
    /// <param name="sourcePath">The source symlink to move into place.</param>
    /// <param name="destPath">The destination path to overwrite atomically.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when destPath exists but is no longer a symbolic link.</exception>
    void ReplaceSymlink(string sourcePath, string destPath);
}