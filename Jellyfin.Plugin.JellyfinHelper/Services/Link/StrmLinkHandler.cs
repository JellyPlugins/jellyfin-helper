using System;
using System.IO;
using System.IO.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Link handler for .strm files. A .strm file is a plain-text file whose content is the target media path (or URL).
/// </summary>
public class StrmLinkHandler : ILinkHandler
{
    // A valid .strm target (path or URL) is never more than a few hundred characters. Reading beyond 4 KB would only happen for accidentally misnamed files (e.g.
    private const int MaxStrmFileSizeBytes = 32 * 1024;

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
        ArgumentNullException.ThrowIfNull(filePath);

        return filePath.EndsWith(MediaExtensions.StrmExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string? ReadTarget(string filePath)
    {
        try
        {
            var fileInfo = _fileSystem.FileInfo.New(filePath);
            if (!fileInfo.Exists || fileInfo.Length > MaxStrmFileSizeBytes)
            {
                return null;
            }

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
        // Crash-safe write: stage the new content in a sibling temp file and atomically move it over the target.
        var tempPath = filePath + ".jfh-tmp";
        try
        {
            _fileSystem.File.WriteAllText(tempPath, targetPath);
            _fileSystem.File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (_fileSystem.File.Exists(tempPath))
                {
                    _fileSystem.File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of the temp file; the original .strm was never truncated.
            }

            throw;
        }
    }
}
