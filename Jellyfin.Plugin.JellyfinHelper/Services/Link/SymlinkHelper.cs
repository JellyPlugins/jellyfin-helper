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
            // We must detect the LINK NODE itself, not follow it to the target. Using
            // `info.Exists` gates the check on the target being present, which:
            //   • On Windows, `FileInfo.Exists` follows the link at check time — so a
            //     broken symlink is reported as NOT a symlink, silently hiding the very
            //     class of link LinkRepairService is designed to fix.
            //   • On Linux/macOS, `FileInfo.Exists` reports the link node — the check
            //     would work there, but relying on that is a portability hazard.
            //
            // `File.GetAttributes` inspects the entry itself without following it, and
            // the ReparsePoint bit is a necessary — but NOT sufficient — indicator of a
            // symbolic link. On Windows the ReparsePoint bit is also set on entries that
            // are NOT symlinks: OneDrive / cloud "files on-demand" placeholders and
            // Windows Data-Deduplication stubs both carry it. Treating those as symlinks
            // makes LinkRepairService flag healthy media files as broken links.
            //
            // To distinguish a real (possibly broken) symlink from such a placeholder we
            // additionally require a non-null `LinkTarget`. `FileInfo.LinkTarget` reads the
            // stored target from the reparse data WITHOUT following it, so it is:
            //   • non-null for both valid and broken symlinks (the target string survives
            //     even after the target file is deleted — see IsSymlink_BrokenSymlink test),
            //   • null for cloud/dedup reparse points, which .NET does not recognise as links.
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            return new FileInfo(path).LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-existent paths / permission denied → treat as "not a symlink". The
            // LinkRepairService will decide separately whether the *absence* of the
            // path is itself an actionable state.
            return false;
        }
    }

    /// <inheritdoc />
    public string? GetSymlinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void CreateSymlink(string linkPath, string targetPath)
    {
        File.CreateSymbolicLink(linkPath, targetPath);
    }

    /// <inheritdoc />
    public void DeleteSymlink(string linkPath)
    {
        if (!IsSymlink(linkPath))
        {
            throw new InvalidOperationException(
                $"Cannot delete '{linkPath}': not a symbolic link.");
        }

        var attrs = File.GetAttributes(linkPath);
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