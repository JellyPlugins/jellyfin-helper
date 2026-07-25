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
        var tempPath = filePath + ".jfh-tmp";
        try
        {
            _symlinkHelper.CreateSymlink(tempPath, targetPath);
            _symlinkHelper.ReplaceSymlink(tempPath, filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            try
            {
                if (_symlinkHelper.IsSymlink(tempPath))
                {
                    _symlinkHelper.DeleteSymlink(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup of temp file; original link was never touched.
            }

            throw;
        }
    }
}
