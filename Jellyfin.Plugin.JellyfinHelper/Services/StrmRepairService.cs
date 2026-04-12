using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Service for finding and repairing broken .strm file references.
/// </summary>
public class StrmRepairService
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<StrmRepairService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmRepairService"/> class.
    /// </summary>
    /// <param name="fileSystem">The file system abstraction.</param>
    /// <param name="logger">The logger instance.</param>
    public StrmRepairService(IFileSystem fileSystem, ILogger<StrmRepairService> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Scans the given library paths for .strm files, validates their target paths,
    /// and repairs broken references by searching the parent directory for a media file.
    /// </summary>
    /// <param name="libraryPaths">The library paths to scan for .strm files.</param>
    /// <param name="dryRun">If true, no files will be modified.</param>
    /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
    /// <returns>The result of the repair operation.</returns>
    public StrmRepairResult RepairStrmFiles(IEnumerable<string> libraryPaths, bool dryRun, CancellationToken cancellationToken = default)
    {
        var result = new StrmRepairResult();
        var strmFiles = FindStrmFiles(libraryPaths);

        PluginLogService.LogInfo("StrmRepair", $"Found {strmFiles.Count} .strm files to check", _logger);

        foreach (var strmFile in strmFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileResult = ProcessStrmFile(strmFile, dryRun);
            result.FileResults.Add(fileResult);
        }

        PluginLogService.LogInfo("StrmRepair", $"STRM repair complete: {result.ValidCount} valid, {result.RepairedCount} repaired, {result.BrokenCount} broken, {result.AmbiguousCount} ambiguous, {result.InvalidContentCount} invalid content", _logger);

        return result;
    }

    /// <summary>
    /// Finds all .strm files in the given library paths using recursive search.
    /// </summary>
    /// <param name="libraryPaths">The library paths to search.</param>
    /// <returns>A list of .strm file paths.</returns>
    internal List<string> FindStrmFiles(IEnumerable<string> libraryPaths)
    {
        var strmFiles = new List<string>();

        foreach (var libraryPath in libraryPaths)
        {
            if (!_fileSystem.Directory.Exists(libraryPath))
            {
                PluginLogService.LogWarning("StrmRepair", $"Library path does not exist: {libraryPath}", logger: _logger);
                continue;
            }

            var files = FindFilesRecursive(libraryPath, MediaExtensions.StrmExtension);
            strmFiles.AddRange(files);
        }

        return strmFiles;
    }

    /// <summary>
    /// Recursively finds files with the given extension in the specified directory.
    /// </summary>
    /// <param name="directory">The directory to search.</param>
    /// <param name="extension">The file extension to filter by.</param>
    /// <returns>A list of matching file paths.</returns>
    internal List<string> FindFilesRecursive(string directory, string extension)
    {
        var result = new List<string>();

        try
        {
            foreach (var file in _fileSystem.Directory.GetFiles(directory))
            {
                if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(file);
                }
            }

            foreach (var subDir in _fileSystem.Directory.GetDirectories(directory))
            {
                result.AddRange(FindFilesRecursive(subDir, extension));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            PluginLogService.LogWarning("StrmRepair", $"Cannot access directory: {directory} - {ex.Message}", ex, _logger);
        }

        return result;
    }

    /// <summary>
    /// Processes a single .strm file: reads the target path, validates it,
    /// and attempts repair if broken.
    /// </summary>
    /// <param name="strmFilePath">The path to the .strm file.</param>
    /// <param name="dryRun">If true, no files will be modified.</param>
    /// <returns>The result for this file.</returns>
    internal StrmFileResult ProcessStrmFile(string strmFilePath, bool dryRun)
    {
        var fileResult = new StrmFileResult { StrmFilePath = strmFilePath };

        string targetPath;
        try
        {
            targetPath = _fileSystem.File.ReadAllText(strmFilePath).Trim();
        }
        catch (Exception ex)
        {
            PluginLogService.LogWarning("StrmRepair", $"Failed to read .strm file {strmFilePath}: {ex.Message}", ex, _logger);
            fileResult.Status = StrmFileStatus.InvalidContent;
            return fileResult;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            PluginLogService.LogDebug("StrmRepair", $"Empty .strm file: {strmFilePath}", _logger);
            fileResult.Status = StrmFileStatus.InvalidContent;
            return fileResult;
        }

        // Skip URL-based .strm files (e.g. http://, https://, rtsp://)
        if (targetPath.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            PluginLogService.LogDebug("StrmRepair", $"Skipping URL-based .strm file: {strmFilePath}", _logger);
            fileResult.OriginalTargetPath = targetPath;
            fileResult.Status = StrmFileStatus.Valid;
            return fileResult;
        }

        fileResult.OriginalTargetPath = targetPath;

        // Check if the target path is still valid
        if (_fileSystem.File.Exists(targetPath))
        {
            PluginLogService.LogDebug("StrmRepair", $"Valid .strm file: {strmFilePath} -> {targetPath}", _logger);
            fileResult.Status = StrmFileStatus.Valid;
            return fileResult;
        }

        // Target path is broken - try to repair
        PluginLogService.LogInfo("StrmRepair", $"Broken .strm file: {strmFilePath} -> {targetPath}", _logger);

        return TryRepairStrmFile(fileResult, dryRun);
    }

    /// <summary>
    /// Tries to repair a broken .strm file by searching the parent directory
    /// of the broken target path for a media file.
    /// </summary>
    /// <param name="fileResult">The file result with the broken path info.</param>
    /// <param name="dryRun">If true, no files will be modified.</param>
    /// <returns>The updated file result.</returns>
    internal StrmFileResult TryRepairStrmFile(StrmFileResult fileResult, bool dryRun)
    {
        var parentDir = _fileSystem.Path.GetDirectoryName(fileResult.OriginalTargetPath);

        if (string.IsNullOrEmpty(parentDir) || !_fileSystem.Directory.Exists(parentDir))
        {
            PluginLogService.LogWarning("StrmRepair", $"Parent directory does not exist for broken .strm target: {fileResult.OriginalTargetPath} (parent: {parentDir ?? "null"})", logger: _logger);
            fileResult.Status = StrmFileStatus.Broken;
            return fileResult;
        }

        // Search the parent directory for media files
        var mediaFiles = FindMediaFilesInDirectory(parentDir);

        if (mediaFiles.Count == 0)
        {
            PluginLogService.LogWarning("StrmRepair", $"No media files found in parent directory {parentDir} for broken .strm: {fileResult.StrmFilePath}", logger: _logger);
            fileResult.Status = StrmFileStatus.Broken;
            return fileResult;
        }

        if (mediaFiles.Count == 1)
        {
            // Exactly one media file found - this is our match
            var newTargetPath = mediaFiles[0];
            fileResult.NewTargetPath = newTargetPath;
            fileResult.Status = StrmFileStatus.Repaired;

            if (dryRun)
            {
                PluginLogService.LogInfo("StrmRepair", $"[DRY RUN] Would repair .strm file: {fileResult.StrmFilePath} | {fileResult.OriginalTargetPath} -> {newTargetPath}", _logger);
            }
            else
            {
                PluginLogService.LogInfo("StrmRepair", $"Repairing .strm file: {fileResult.StrmFilePath} | {fileResult.OriginalTargetPath} -> {newTargetPath}", _logger);

                try
                {
                    _fileSystem.File.WriteAllText(fileResult.StrmFilePath, newTargetPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PluginLogService.LogError("StrmRepair", $"Failed to write repaired .strm file {fileResult.StrmFilePath}: {ex.Message}", ex, _logger);
                    fileResult.Status = StrmFileStatus.Broken;
                    fileResult.NewTargetPath = null;
                    return fileResult;
                }
            }

            return fileResult;
        }

        // Multiple media files found - ambiguous
        PluginLogService.LogWarning("StrmRepair", $"Multiple media files ({mediaFiles.Count}) found in parent directory {parentDir} for broken .strm: {fileResult.StrmFilePath}. Candidates: {string.Join(", ", mediaFiles.Select(f => _fileSystem.Path.GetFileName(f)))}", logger: _logger);
        fileResult.Status = StrmFileStatus.Ambiguous;
        return fileResult;
    }

    /// <summary>
    /// Finds all media files (video files) in the given directory (non-recursive).
    /// </summary>
    /// <param name="directory">The directory to search.</param>
    /// <returns>A list of media file paths.</returns>
    internal List<string> FindMediaFilesInDirectory(string directory)
    {
        var mediaFiles = new List<string>();

        try
        {
            foreach (var file in _fileSystem.Directory.GetFiles(directory))
            {
                var extension = _fileSystem.Path.GetExtension(file);
                if (MediaExtensions.VideoExtensions.Contains(extension))
                {
                    mediaFiles.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            PluginLogService.LogWarning("StrmRepair", $"Cannot access directory: {directory} - {ex.Message}", ex, _logger);
        }

        return mediaFiles;
    }
}