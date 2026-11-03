using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Link handler for symbolic links (symlinks). Uses ISymlinkHelper for filesystem operations to enable unit testing without real symlinks.
/// </summary>
public class SymlinkHandler : ILinkHandler
{
    private readonly ISymlinkHelper _symlinkHelper;
    private readonly IPluginLogService? _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SymlinkHandler" /> class.
    /// </summary>
    /// <param name="symlinkHelper">The symlink helper for filesystem operations.</param>
    public SymlinkHandler(ISymlinkHelper symlinkHelper)
        : this(symlinkHelper, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SymlinkHandler"/> class with plugin-specific cleanup diagnostics.</summary>
    /// <param name="symlinkHelper">The filesystem symlink operations.</param>
    /// <param name="pluginLog">The plugin log service, or null for standalone use.</param>
    public SymlinkHandler(ISymlinkHelper symlinkHelper, IPluginLogService? pluginLog)
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
            catch (Exception cleanupException) when (!cleanupException.IsFatal())
            {
                var message = $"Failed to remove temporary symlink '{tempPath}'.";
                if (_pluginLog is not null)
                {
                    _pluginLog.LogWarning("LinkRepair", message, cleanupException);
                }
                else
                {
                    System.Diagnostics.Trace.TraceWarning("{0} {1}", message, cleanupException);
                }
            }

            throw;
        }
    }
}
