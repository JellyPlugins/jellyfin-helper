using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Link handler for symbolic links (symlinks).
///     Uses <see cref="ISymlinkHelper" /> for filesystem operations
///     to enable unit testing without real symlinks.
/// </summary>
public class SymlinkHandler : ILinkHandler
{
    private readonly ISymlinkHelper _symlinkHelper;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SymlinkHandler" /> class.
    /// </summary>
    /// <param name="symlinkHelper">The symlink helper for filesystem operations.</param>
    /// <param name="pluginLog">Plugin log service for warning on rollback failure.</param>
    public SymlinkHandler(ISymlinkHelper symlinkHelper, IPluginLogService pluginLog)
    {
        _symlinkHelper = symlinkHelper;
        _pluginLog = pluginLog;
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
        var deleted = false;
        try
        {
            _symlinkHelper.DeleteSymlink(filePath);
            deleted = true;
            _symlinkHelper.CreateSymlink(filePath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            if (!deleted || string.IsNullOrWhiteSpace(previousTarget))
            {
                throw;
            }

            try
            {
                _symlinkHelper.CreateSymlink(filePath, previousTarget);
            }
            catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // Rollback failed: the original symlink at filePath is permanently gone.
                // Log at Warning so operators can detect and remediate manually.
                _pluginLog.LogWarning(
                    "SymlinkHandler",
                    $"Rollback failed for '{filePath}': could not restore symlink to '{previousTarget}'. The link is permanently removed.",
                    rollbackEx);
            }

            throw;
        }
    }
}
