using System;
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
    private static bool IsSymlinkFromAttributes(string path, FileAttributes attrs) =>
        (attrs & FileAttributes.ReparsePoint) != 0 && new FileInfo(path).LinkTarget != null;

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
        // TOCTOU guard (data-loss prevention): between LinkRepairService reading the broken
        // target and this move, an import (Sonarr/Radarr) can replace the placeholder symlink
        // at destPath with the REAL downloaded media file. Moving our temp symlink over it with
        // overwrite:true would destroy the real bytes with no trash/backup. Re-stat destPath and
        // refuse unless it is still a symbolic link (reparse point WITH a non-null LinkTarget).
        // If destPath no longer exists we allow the move (nothing to lose).
        FileAttributes destAttrs;
        try
        {
            destAttrs = File.GetAttributes(destPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Destination vanished since the scan; safe to move into place.
            File.Move(sourcePath, destPath, overwrite: true);
            return;
        }

        if (!IsSymlinkFromAttributes(destPath, destAttrs))
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite '{destPath}': it is no longer a symbolic link "
                + "(likely replaced by a real file since the scan). Aborting repair to avoid data loss.");
        }

        // TOCTOU: between the IsSymlinkFromAttributes check and File.Move, an import tool
        // (Sonarr/Radarr) could replace the symlink with the real downloaded media file.
        // We narrow the window and detect the race post-hoc via a re-stat on IOException.
        try
        {
            File.Move(sourcePath, destPath, overwrite: true);
        }
        catch (IOException)
        {
            // Re-stat destPath: if it is now a real file the race occurred — do not overwrite.
            try
            {
                var recheckAttrs = File.GetAttributes(destPath);
                if (!IsSymlinkFromAttributes(destPath, recheckAttrs))
                {
                    throw new InvalidOperationException(
                        $"Refusing to overwrite '{destPath}': it became a real file during the move operation. "
                        + "Aborting repair to avoid data loss. The downloaded media file is safe.");
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // destPath vanished between the IOException and the re-stat — retry without overwrite.
                File.Move(sourcePath, destPath);
                return;
            }

            throw; // Re-throw original IOException: destPath still looks like a symlink (other I/O failure).
        }
    }

    /// <inheritdoc />
    public void DeleteSymlink(string linkPath)
    {
        // Read attributes once and reuse for both the symlink check and the
        // file-vs-directory branch, avoiding a redundant GetAttributes syscall.
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot delete '{linkPath}': not a symbolic link.", ex);
        }

        if (!IsSymlinkFromAttributes(linkPath, attrs))
        {
            throw new InvalidOperationException(
                $"Cannot delete '{linkPath}': not a symbolic link.");
        }

        if ((attrs & FileAttributes.Directory) != 0)
        {
            Directory.Delete(linkPath);
        }
        else
        {
            File.Delete(linkPath);
        }
    }
}