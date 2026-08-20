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
    ///     Returns <see langword="true" /> if <paramref name="path" /> exists and carries the
    ///     <see cref="FileAttributes.ReparsePoint" /> flag (symlink or junction).
    /// </summary>
    /// <param name="path">Filesystem path to inspect.</param>
    /// <returns>
    ///     <see langword="true" /> when the path exists and is a reparse point;
    ///     <see langword="false" /> otherwise.
    /// </returns>
    internal static bool IsReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
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
