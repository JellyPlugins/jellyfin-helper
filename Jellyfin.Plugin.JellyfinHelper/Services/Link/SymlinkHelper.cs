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
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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

        File.Delete(linkPath);
    }
}