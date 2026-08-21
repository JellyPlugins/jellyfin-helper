using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Production implementation of <see cref="ISymlinkHelper" /> using real filesystem operations.
/// </summary>
public class SymlinkHelper : ISymlinkHelper
{
    /// <inheritdoc />
    public bool IsSymlink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return IsSymlinkFromAttributes(path, attrs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or PathTooLongException)
        {
            // Non-existent paths / permission denied / invalid path characters → treat as "not a symlink". The
            // LinkRepairService will decide separately whether the *absence* of the
            // path is itself an actionable state.
            return false;
        }
    }

    /// <summary>
    ///     Determines whether a path is a symbolic link given an already-fetched
    ///     <see cref="FileAttributes" /> value, avoiding a second <c>GetAttributes</c> syscall
    ///     in callers that have already read the attributes (e.g. <see cref="DeleteSymlink" />).
    /// </summary>
    /// <remarks>
    ///     We must detect the LINK NODE itself, not follow it to the target. Using
    ///     <c>info.Exists</c> gates the check on the target being present, which:
    ///     <list type="bullet">
    ///       <item>On Windows, <c>FileInfo.Exists</c> follows the link at check time - so a
    ///         broken symlink is reported as NOT a symlink, silently hiding the very
    ///         class of link LinkRepairService is designed to fix.</item>
    ///       <item>On Linux/macOS, <c>FileInfo.Exists</c> reports the link node - the check
    ///         would work there, but relying on that is a portability hazard.</item>
    ///     </list>
    ///     <c>File.GetAttributes</c> inspects the entry itself without following it, and
    ///     the <c>ReparsePoint</c> bit is a necessary - but NOT sufficient - indicator of a
    ///     symbolic link. On Windows the <c>ReparsePoint</c> bit is also set on entries that
    ///     are NOT symlinks: OneDrive / cloud "files on-demand" placeholders and
    ///     Windows Data-Deduplication stubs both carry it. Treating those as symlinks
    ///     makes LinkRepairService flag healthy media files as broken links.
    ///     <para>
    ///       To distinguish a real (possibly broken) symlink from such a placeholder we
    ///       additionally require a non-null <c>LinkTarget</c>. <c>FileInfo.LinkTarget</c> reads
    ///       the stored target from the reparse data WITHOUT following it, so it is:
    ///       <list type="bullet">
    ///         <item>non-null for both valid and broken symlinks (the target string survives
    ///           even after the target file is deleted - see IsSymlink_BrokenSymlink test),</item>
    ///         <item>null for cloud/dedup reparse points, which .NET does not recognise as links.</item>
    ///       </list>
    ///     </para>
    /// </remarks>
    private bool IsSymlinkFromAttributes(string path, FileAttributes attrs) =>
        (attrs & FileAttributes.ReparsePoint) != 0 && GetLinkTarget(path) != null;

    /// <inheritdoc />
    public string? GetSymlinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void CreateSymlink(string linkPath, string targetPath)
    {
        // Precondition validation: File.CreateSymbolicLink throws a bare ArgumentException on
        // null/empty, and silently no-ops nothing useful if the link path already points at a
        // file/dir. Fail fast with clear errors so callers cannot create a broken/ambiguous link.
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            throw new ArgumentException("Link path must not be null or empty.", nameof(linkPath));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path must not be null or empty.", nameof(targetPath));
        }

        if (File.Exists(linkPath) || Directory.Exists(linkPath))
        {
            throw new IOException($"Cannot create symlink at '{linkPath}': a file or directory already exists there.");
        }

        File.CreateSymbolicLink(linkPath, targetPath);
    }

    /// <inheritdoc />
    public void ReplaceSymlink(string sourcePath, string destPath)
    {
        FileAttributes destAttrs;
        try
        {
            destAttrs = GetAttributes(destPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Destination vanished since the scan; safe to move into place (no overwrite needed).
            MoveFile(sourcePath, destPath);
            return;
        }

        if (!IsSymlinkFromAttributes(destPath, destAttrs))
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite '{destPath}': it is no longer a symbolic link "
                + "(likely replaced by a real file since the scan). Aborting repair to avoid data loss.");
        }

        try
        {
            // Non-overwriting move: atomically fails if destPath still exists (it does — a symlink).
            MoveFile(sourcePath, destPath);
            return;
        }
        catch (IOException)
        {
            // Only the "destination already exists" case is recoverable here. Any other
            // IOException (EXDEV cross-device move, read-only mount, media error) is not related
            // to a racing file and must propagate unchanged rather than trigger a pointless retry.
            if (!FileExists(destPath) && !DirectoryExists(destPath))
            {
                throw;
            }

            // destPath exists. Re-stat: only remove it if it is STILL a symlink. If a real file
            // raced into place we must not touch it.
            FileAttributes recheckAttrs;
            try
            {
                recheckAttrs = GetAttributes(destPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // destPath vanished between the failed move and the re-stat — retry cleanly.
                MoveFile(sourcePath, destPath);
                return;
            }

            if (!IsSymlinkFromAttributes(destPath, recheckAttrs))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite '{destPath}': it became a real file during the move operation. "
                    + "Aborting repair to avoid data loss. The downloaded media file is safe.");
            }

            // Still a symlink — use an overwriting move which calls rename(2) on Linux /
            // MoveFileExW(MOVEFILE_REPLACE_EXISTING) on Windows.  This replaces the destination
            // in a single kernel operation, removing the delete-then-move two-step gap.
            //
            // A narrow TOCTOU window remains: a concurrent process could swap the link node for a
            // real file between the re-check above and the rename syscall.  The .NET BCL exposes no
            // identity-pinned (no-follow) rename that would close this gap without P/Invoke.  The
            // two IsSymlinkFromAttributes checks above (the initial guard and the post-move
            // re-stat) already fail closed for real files that arrive before this point; accepting
            // the residual nanosecond-scale window is the safest option available in managed code.
            MoveFileOverwrite(sourcePath, destPath);
        }
    }

    /// <inheritdoc />
    public void DeleteSymlink(string linkPath)
    {
        FileAttributes attrs;
        try
        {
            attrs = GetAttributes(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = $"Cannot delete '{linkPath}': the entry could not be inspected ({ex.GetType().Name}). "
                + "Aborting to avoid deleting an unverified entry.";
            throw new InvalidOperationException(message, ex);
        }

        if (!IsSymlinkFromAttributes(linkPath, attrs))
        {
            throw new InvalidOperationException(
                $"Cannot delete '{linkPath}': not a symbolic link.");
        }

        if ((attrs & FileAttributes.Directory) != 0)
        {
            DeleteDirectory(linkPath);
        }
        else
        {
            DeleteFile(linkPath);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Filesystem seams (overridable for tests).
    //
    // ReplaceSymlink / DeleteSymlink guard against concurrent replacement of a link node between
    // the stat and the mutation (TOCTOU). Provoking those races against the real filesystem is
    // non-deterministic, and creating real symlinks needs elevated privileges unavailable in CI.
    // Routing every raw System.IO primitive through these thin virtual wrappers lets a test
    // subclass drive each defensive branch deterministically. Production always runs the real
    // System.IO implementations below.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Reads the attributes of the entry at <paramref name="path" /> without following links.</summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The entry's <see cref="FileAttributes" />.</returns>
    internal virtual FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    /// <summary>
    ///     Reads the stored link target of <paramref name="path" /> without following it
    ///     (<see cref="FileSystemInfo.LinkTarget" />). Non-null for valid and broken symlinks;
    ///     null for cloud/dedup reparse points and non-link entries.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The link target string, or <see langword="null" /> when the entry is not a link.</returns>
    internal virtual string? GetLinkTarget(string path) => new FileInfo(path).LinkTarget;

    /// <summary>Moves <paramref name="source" /> to <paramref name="dest" />, failing if the destination exists.</summary>
    /// <param name="source">The source path.</param>
    /// <param name="dest">The destination path.</param>
    internal virtual void MoveFile(string source, string dest) => File.Move(source, dest);

    /// <summary>Moves <paramref name="source" /> to <paramref name="dest" />, replacing an existing destination.</summary>
    /// <param name="source">The source path.</param>
    /// <param name="dest">The destination path.</param>
    internal virtual void MoveFileOverwrite(string source, string dest) => File.Move(source, dest, overwrite: true);

    /// <summary>Deletes the file link node at <paramref name="path" />.</summary>
    /// <param name="path">The file path to delete.</param>
    internal virtual void DeleteFile(string path) => File.Delete(path);

    /// <summary>Deletes the directory link node at <paramref name="path" />.</summary>
    /// <param name="path">The directory path to delete.</param>
    internal virtual void DeleteDirectory(string path) => Directory.Delete(path);

    /// <summary>Determines whether a file exists at <paramref name="path" />.</summary>
    /// <param name="path">The path to test.</param>
    /// <returns><see langword="true" /> if a file exists at the path.</returns>
    internal virtual bool FileExists(string path) => File.Exists(path);

    /// <summary>Determines whether a directory exists at <paramref name="path" />.</summary>
    /// <param name="path">The path to test.</param>
    /// <returns><see langword="true" /> if a directory exists at the path.</returns>
    internal virtual bool DirectoryExists(string path) => Directory.Exists(path);
}