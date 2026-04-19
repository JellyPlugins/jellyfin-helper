using System;
using System.IO;
using System.IO.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Link handler for .strm files.
///     A .strm file is a plain-text file whose content is the target media path (or URL).
///     This handler encapsulates all .strm-specific logic so it can be removed cleanly
///     if .strm support is deprecated in the future.
/// </summary>
public class StrmLinkHandler : ILinkHandler
{
    private readonly IFileSystem _fileSystem;

    /// <summary>
    ///     Initializes a new instance of the <see cref="StrmLinkHandler" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system abstraction.</param>
    public StrmLinkHandler(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public bool SupportsUrlTargets => true;

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        return filePath.EndsWith(MediaExtensions.StrmExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string? ReadTarget(string filePath)
    {
        try
        {
            var content = _fileSystem.File.ReadAllText(filePath).Trim();
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <exception cref="IOException">Thrown when the file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when write access to the file is denied.</exception>
    public void WriteTarget(string filePath, string targetPath)
    {
        _fileSystem.File.WriteAllText(filePath, targetPath);
    }
}