using System;
using System.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Link handler for symbolic links (symlinks).
///     Uses <see cref="ISymlinkHelper" /> for filesystem operations
///     to enable unit testing without real symlinks.
/// </summary>
public class SymlinkHandler : ILinkHandler
{
    private readonly ISymlinkHelper _symlinkHelper;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SymlinkHandler" /> class.
    /// </summary>
    /// <param name="symlinkHelper">The symlink helper for filesystem operations.</param>
    public SymlinkHandler(ISymlinkHelper symlinkHelper)
    {
        _symlinkHelper = symlinkHelper;
    }

    /// <inheritdoc />
    public bool SupportsUrlTargets => false;

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        return _symlinkHelper.IsSymlink(filePath);
    }

    /// <inheritdoc />
    public string? ReadTarget(string filePath)
    {
        return _symlinkHelper.GetSymlinkTarget(filePath);
    }

    /// <inheritdoc />
    public void WriteTarget(string filePath, string targetPath)
    {
        var previousTarget = _symlinkHelper.GetSymlinkTarget(filePath);
        try
        {
            _symlinkHelper.DeleteSymlink(filePath);
            _symlinkHelper.CreateSymlink(filePath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            if (string.IsNullOrWhiteSpace(previousTarget))
            {
                throw;
            }

            try
            {
                _symlinkHelper.CreateSymlink(filePath, previousTarget);
            }
            catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // best-effort rollback — ignore errors restoring the original symlink
            }

            throw;
        }
    }
}