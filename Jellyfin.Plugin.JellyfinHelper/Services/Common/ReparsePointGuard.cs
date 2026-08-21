using System;
using System.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Shared fail-closed primitives for reparse-point (symlink / junction) detection and
///     safe deletion.  Centralising these removes the risk of one copy diverging from the
///     other and silently losing the data-loss guard.
/// </summary>
internal static class ReparsePointGuard
{
    /// <summary>
    ///     Returns <see langword="true" /> if <paramref name="path" /> exists as a DIRECTORY and
    ///     carries the <see cref="FileAttributes.ReparsePoint" /> flag (symlink or junction).
    ///     File link nodes always return <see langword="false" /> because the check uses
    ///     <see cref="DirectoryInfo" />; use <c>FileInfo.LinkTarget</c> for file entries.
    ///     All current callers pass directory paths.
    /// </summary>
    /// <param name="path">Directory path to inspect.</param>
    /// <returns>
    ///     <see langword="true" /> when the path exists as a directory and is a reparse point;
    ///     <see langword="false" /> otherwise (including when it is a file).
    /// </returns>
    internal static bool IsReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>
    ///     Returns <see langword="true" /> if <paramref name="path" /> carries the
    ///     <see cref="FileAttributes.ReparsePoint" /> flag, <em>regardless of whether the
    ///     underlying filesystem surfaces the entry as a file or a directory</em>.
    ///     <para>
    ///         This exists because <see cref="IsReparsePoint" /> uses <see cref="DirectoryInfo" />
    ///         and therefore only detects link nodes the OS classified as directories. On some
    ///         mounts — notably Docker Desktop for Windows bind mounts (9p/virtiofs) — a symlink
    ///         that points at a directory is enumerated as a <em>file</em>, so a directory-only
    ///         check misses it and the caller could dereference/delete a link into a foreign tree.
    ///         <see cref="File.GetAttributes(string)" /> reads the node's own attributes (it does
    ///         not follow the link) and reports the reparse-point flag for both classifications.
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
    ///     Re-checks <paramref name="path" /> immediately before deletion and delegates the
    ///     actual delete to <paramref name="delete" />.  Throws when the node is missing or
    ///     is no longer a reparse point (concurrent replacement detected — fail closed).
    /// </summary>
    /// <param name="path">The reparse-point path to remove.</param>
    /// <param name="delete">
    ///     The delete action to invoke on the verified <see cref="DirectoryInfo" />.
    ///     Typically an overridable seam (e.g. <c>InvokeDirectoryDelete</c>).
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the node changed type between the caller's guard and this call
    ///     (concurrent replacement detected — entry left unchanged to avoid data loss).
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
