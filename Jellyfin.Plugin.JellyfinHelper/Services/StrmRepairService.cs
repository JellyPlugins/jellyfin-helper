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

        _logger.LogInformation("Found {Count} .strm files to check", strmFiles.Count);

        foreach (var strmFile in strmFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileResult = ProcessStrmFile(strmFile, dryRun);
            result.FileResults.Add(fileResult);
        }

        _logger.LogInformation(
            "STRM repair complete: {Valid} valid, {Repaired} repaired, {Broken} broken, {Ambiguous} ambiguous, {Invalid} invalid content",
            result.ValidCount,
            result.RepairedCount,
            result.BrokenCount,
            result.AmbiguousCount,
            result.InvalidContentCount);

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
                _logger.LogWarning("Library path does not exist: {Path}", libraryPath);
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
            _logger.LogWarning("Cannot access directory: {Directory} - {Message}", directory, ex.Message);
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
            _logger.LogWarning("Failed to read .strm file {Path}: {Message}", strmFilePath, ex.Message);
            fileResult.Status = StrmFileStatus.InvalidContent;
            return fileResult;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            _logger.LogDebug("Empty .strm file: {Path}", strmFilePath);
            fileResult.Status = StrmFileStatus.InvalidContent;
            return fileResult;
        }

        // Skip URL-based .strm files (e.g. http://, https://, rtsp://)
        if (targetPath.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping URL-based .strm file: {Path}", strmFilePath);
            fileResult.OriginalTargetPath = targetPath;
            fileResult.Status = StrmFileStatus.Valid;
            return fileResult;
        }

        fileResult.OriginalTargetPath = targetPath;

        // Check if the target path is still valid
        if (_fileSystem.File.Exists(targetPath))
        {
            _logger.LogDebug("Valid .strm file: {Path} -> {Target}", strmFilePath, targetPath);
            fileResult.Status = StrmFileStatus.Valid;
            return fileResult;
        }

        // Target path is broken - try to repair
        _logger.LogInformation("Broken .strm file: {Path} -> {Target}", strmFilePath, targetPath);

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
            _logger.LogWarning(
                "Parent directory does not exist for broken .strm target: {Target} (parent: {Parent})",
                fileResult.OriginalTargetPath,
                parentDir ?? "null");
            fileResult.Status = StrmFileStatus.Broken;
            return fileResult;
        }

        // Search the parent directory for media files
        var mediaFiles = FindMediaFilesInDirectory(parentDir);

        if (mediaFiles.Count == 0)
        {
            _logger.LogWarning(
                "No media files found in parent directory {Parent} for broken .strm: {StrmFile}",
                parentDir,
                fileResult.StrmFilePath);
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
                _logger.LogInformation(
                    "[DRY RUN] Would repair .strm file: {StrmFile} | {OldTarget} -> {NewTarget}",
                    fileResult.StrmFilePath,
                    fileResult.OriginalTargetPath,
                    newTargetPath);
            }
            else
            {
                _logger.LogInformation(
                    "Repairing .strm file: {StrmFile} | {OldTarget} -> {NewTarget}",
                    fileResult.StrmFilePath,
                    fileResult.OriginalTargetPath,
                    newTargetPath);

                try
                {
                    _fileSystem.File.WriteAllText(fileResult.StrmFilePath, newTargetPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError("Failed to write repaired .strm file {Path}: {Message}", fileResult.StrmFilePath, ex.Message);
                    fileResult.Status = StrmFileStatus.Broken;
                    fileResult.NewTargetPath = null;
                    return fileResult;
                }
            }

            return fileResult;
        }

        // Multiple media files found - ambiguous
        _logger.LogWarning(
            "Multiple media files ({Count}) found in parent directory {Parent} for broken .strm: {StrmFile}. Candidates: {Candidates}",
            mediaFiles.Count,
            parentDir,
            fileResult.StrmFilePath,
            string.Join(", ", mediaFiles.Select(f => _fileSystem.Path.GetFileName(f))));
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
            _logger.LogWarning("Cannot access directory: {Directory} - {Message}", directory, ex.Message);
        }

        return mediaFiles;
    }
}