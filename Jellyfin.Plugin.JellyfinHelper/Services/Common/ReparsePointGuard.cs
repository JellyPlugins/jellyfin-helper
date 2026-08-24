using System;
using System.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Fail-closed helpers for detecting reparse points (symlinks/junctions) and deleting them
///     safely. Keeping these in one place avoids two copies drifting apart and losing the guard.
/// </summary>
internal static class ReparsePointGuard
{
    /// <summary>
    ///     Returns <see langword="true" /> if <paramref name="path" /> exists as a DIRECTORY and
    ///     carries the <see cref="FileAttributes.ReparsePoint" /> flag (symlink or junction).
    ///     File link nodes always return <see langword="false" /> because the check requires the
    ///     <see cref="FileAttributes.Directory" /> flag; use <c>FileInfo.LinkTarget</c> for file
    ///     entries. All current callers pass directory paths.
    ///     <para>
    ///         Attributes are read via <see cref="File.GetAttributes(string)" />, not
    ///         <see cref="DirectoryInfo.Exists" />, so the guard fails closed. The <c>Exists</c>
    ///         property swallows <see cref="UnauthorizedAccessException" /> and I/O failures and
    ///         returns <see langword="false" />, which would report an un-stat'able directory as
    ///         "not a reparse point" and let the caller delete a link into a foreign tree. Reading
    ///         attributes directly lets those access errors propagate to the callers'
    ///         <c>catch (IOException or UnauthorizedAccessException)</c> guards; only the genuine
    ///         "path is absent" exceptions map to <see langword="false" />.
    ///     </para>
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
    ///     Returns <see langword="true" /> if <paramref name="path" /> carries the
    ///     <see cref="FileAttributes.ReparsePoint" /> flag, whether the filesystem surfaces the
    ///     entry as a file or a directory.
    ///     <para>
    ///         This exists because <see cref="IsReparsePoint" /> requires the
    ///         <see cref="FileAttributes.Directory" /> flag and so only detects link nodes the OS
    ///         classified as directories. On some mounts, notably Docker Desktop for Windows bind
    ///         mounts (9p/virtiofs), a symlink pointing at a directory is enumerated as a file, so a
    ///         directory-only check misses it and the caller could dereference or delete a link into
    ///         a foreign tree. <see cref="File.GetAttributes(string)" /> reads the node's own
    ///         attributes (it does not follow the link) and reports the reparse-point flag for both
    ///         classifications.
    ///     </para>
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
    ///     Re-checks <paramref name="path" /> immediately before deletion and delegates the actual
    ///     delete to <paramref name="delete" />. Throws when the node is missing or is no longer a
    ///     reparse point, which means a concurrent replacement was detected and we fail closed.
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
