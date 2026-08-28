using System;
using System.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Fail-closed helpers for detecting reparse points (symlinks/junctions) and deleting them safely.
/// </summary>
internal static class ReparsePointGuard
{
    /// <summary>
    ///     Returns true if path exists as a DIRECTORY and carries the ReparsePoint flag (symlink or junction).
    /// </summary>
    /// <param name="path">Directory path to inspect.</param>
    /// <returns>
    ///     <see langword="true" /> when the path exists as a directory and is a reparse point;
    ///     <see langword="false" /> otherwise (including when it is a file or the path is absent).
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">The entry could not be stat'd (access denied).</exception>
    /// <exception cref="IOException">The entry could not be stat'd (I/O failure).</exception>
    internal static bool IsReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        return (attributes & FileAttributes.Directory) != 0
               && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>
    ///     Returns true if path carries the ReparsePoint flag, whether the filesystem surfaces the entry as a file or a directory.
    /// </summary>
    /// <param name="path">The path to inspect (file or directory entry).</param>
    /// <returns>
    ///     <see langword="true" /> when the entry exists and is a reparse point (symlink/junction),
    ///     <see langword="false" /> otherwise (including a missing path).
    /// </returns>
    internal static bool IsReparsePointAnyType(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Re-checks immediately before deletion and delegates the actual delete to . Throws when the node is missing or is no longer a reparse point, which means a concurrent replacement was detected and we fail closed.
    /// </summary>
    /// <param name="path">The reparse-point path to remove.</param>
    /// <param name="delete">
    ///     The delete action to invoke on the verified <see cref="DirectoryInfo" />.
    ///     Typically an overridable seam (e.g. <c>InvokeDirectoryDelete</c>).
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the node changed type between the caller's guard and this call. The entry is
    ///     left unchanged to avoid data loss.
    /// </exception>
    internal static void DeleteLinkNode(string path, Action<DirectoryInfo> delete)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            throw new InvalidOperationException(
                $"'{path}' is no longer a reparse point at deletion time; " +
                "aborting to avoid data loss (concurrent replacement detected).");
        }

        delete(info);
    }
}
