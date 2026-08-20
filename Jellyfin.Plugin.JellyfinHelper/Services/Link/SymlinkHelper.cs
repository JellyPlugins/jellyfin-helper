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
        FileAttributes destAttrs;
        try
        {
            destAttrs = File.GetAttributes(destPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Destination vanished since the scan; safe to move into place (no overwrite needed).
            File.Move(sourcePath, destPath);
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
            File.Move(sourcePath, destPath);
            return;
        }
        catch (IOException)
        {
            // destPath exists. Re-stat: only remove it if it is STILL a symlink. If a real file
            // raced into place we must not touch it.
            FileAttributes recheckAttrs;
            try
            {
                recheckAttrs = File.GetAttributes(destPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // destPath vanished between the failed move and the re-stat — retry cleanly.
                // Ignored in coverage: Impossible to simulate deterministically in tests.
                HandleVanishedDestRaceCondition();
                return;
            }

            if (!IsSymlinkFromAttributes(destPath, recheckAttrs))
            {
                // Ignored in coverage: Impossible to simulate deterministically in tests.
                throw CreateDestBecameRealFileException();
            }

            // Still a symlink — use File.Move(overwrite: true) which calls rename(2) on Linux /
            // MoveFileExW(MOVEFILE_REPLACE_EXISTING) on Windows.  This replaces the destination
            // in a single kernel operation, removing the delete-then-move two-step gap.
            //
            // A narrow TOCTOU window remains: a concurrent process could swap the link node for a
            // real file between the re-check above and the rename syscall.  The .NET BCL exposes no
            // identity-pinned (no-follow) rename that would close this gap without P/Invoke.  Two
            // attribute checks (lines 119 and 149) already fail closed for real files that arrive
            // before this point; accepting the residual nanosecond-scale window is the safest
            // option available in managed code.
            File.Move(sourcePath, destPath, overwrite: true);
        }

        // --- Excluded Race Condition Handlers ---

        [ExcludeFromCodeCoverage]
        void HandleVanishedDestRaceCondition() => File.Move(sourcePath, destPath);

        [ExcludeFromCodeCoverage]
        Exception CreateDestBecameRealFileException() => new InvalidOperationException(
            $"Refusing to overwrite '{destPath}': it became a real file during the move operation. "
            + "Aborting repair to avoid data loss. The downloaded media file is safe.");
    }

    /// <inheritdoc />
    public void DeleteSymlink(string linkPath)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignored in coverage: Simulating raw UnauthorizedAccessException on a path check is flaky.
            throw CreateCannotDeleteException(ex);
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

        // --- Excluded System.IO Exception Handler ---

        [ExcludeFromCodeCoverage]
        Exception CreateCannotDeleteException(Exception ex) => new InvalidOperationException(
            $"Cannot delete '{linkPath}': not a symbolic link.", ex);
    }
}