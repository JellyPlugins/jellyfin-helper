using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Service for finding and repairing broken link file references.
///     Delegates link-type-specific logic (reading/writing targets) to
///     <see cref="ILinkHandler" /> strategies, keeping this service-agnostic
///     of any particular link format.
/// </summary>
public class LinkRepairService : ILinkRepairService
{
    /// <summary>
    ///     Maximum number of directories to visit during recursive scanning.
    ///     Acts as a safety valve against unresolved symlink loops or extremely deep trees.
    /// </summary>
    private const int MaxVisitedDirectories = 50_000;

    /// <summary>
    ///     Path comparer appropriate for the current OS.
    ///     Case-insensitive on Windows/macOS, case-sensitive on Linux.
    /// </summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly IFileSystem _fileSystem;
    private readonly IReadOnlyList<ILinkHandler> _handlers;
    private readonly ILogger<LinkRepairService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LinkRepairService" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system abstraction.</param>
    /// <param name="handlers">The registered link handlers (one per link type).</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public LinkRepairService(
        IFileSystem fileSystem,
        IEnumerable<ILinkHandler> handlers,
        IPluginLogService pluginLog,
        ILogger<LinkRepairService> logger)
    {
        _fileSystem = fileSystem;
        _handlers = handlers.ToList().AsReadOnly();
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Scans the given library paths for link files, validates their target paths,
    ///     and repairs broken references by searching the parent directory for a media file.
    /// </summary>
    /// <param name="libraryPaths">The library paths to scan for link files.</param>
    /// <param name="dryRun">If true, no files will be modified.</param>
    /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
    /// <returns>The result of the repair operation.</returns>
    public LinkRepairResult RepairLinks(
        IEnumerable<string> libraryPaths,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var result = new LinkRepairResult();
        var linkFiles = FindLinkFiles(libraryPaths, cancellationToken);

        _pluginLog.LogInfo("LinkRepair", $"Found {linkFiles.Count} link files to check", _logger);

        foreach (var (filePath, handler) in linkFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileResult = ProcessLinkFile(filePath, handler, dryRun);
            result.FileResults.Add(fileResult);
        }

        var repairedLabel = dryRun ? "would repair" : "repaired";
        _pluginLog.LogInfo(
            "LinkRepair",
            $"Link repair complete: {result.ValidCount} valid, {result.RepairedCount} {repairedLabel}, {result.BrokenCount} broken, {result.AmbiguousCount} ambiguous, {result.InvalidContentCount} invalid content",
            _logger);

        return result;
    }

    /// <summary>
    ///     Finds all link files in the given library paths using recursive search.
    ///     Each file is paired with the handler that recognized it.
    /// </summary>
    /// <param name="libraryPaths">The library paths to search.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of (filePath, handler) tuples.</returns>
    internal List<(string FilePath, ILinkHandler Handler)> FindLinkFiles(
        IEnumerable<string> libraryPaths,
        CancellationToken cancellationToken = default)
    {
        var linkFiles = new List<(string FilePath, ILinkHandler Handler)>();
        var visitedDirectories = new HashSet<string>(PathComparer);

        foreach (var libraryPath in libraryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_fileSystem.Directory.Exists(libraryPath))
            {
                _pluginLog.LogWarning("LinkRepair", $"Library path does not exist: {libraryPath}", logger: _logger);
                continue;
            }

            FindLinkFilesRecursive(libraryPath, linkFiles, cancellationToken, visitedDirectories);
        }

        return linkFiles;
    }

    /// <summary>
    ///     Recursively scans a directory for files that any registered handler recognizes.
    /// </summary>
    private void FindLinkFilesRecursive(
        string directory,
        List<(string FilePath, ILinkHandler Handler)> result,
        CancellationToken cancellationToken,
        HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(PathComparer);
        var normalized = _fileSystem.Path.GetFullPath(directory);

        // Resolve symlink targets so that two different symlinked paths
        // pointing to the same physical directory are recognized as duplicates.
        try
        {
            var dirInfo = _fileSystem.DirectoryInfo.New(normalized);
            var resolved = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved != null)
            {
                normalized = _fileSystem.Path.GetFullPath(resolved.FullName);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // If symlink resolution fails (permissions, unsupported FS, etc.),
            // fall back to the lexically normalized path.
        }

        if (!visited.Add(normalized))
        {
            return;
        }

        // Guard against excessively deep directory trees (e.g. unresolved symlink loops)
        if (visited.Count > MaxVisitedDirectories)
        {
            _pluginLog.LogWarning(
                "LinkRepair",
                $"Visited directory limit ({MaxVisitedDirectories}) reached — aborting deeper traversal at: {directory}",
                logger: _logger);
            return;
        }

        try
        {
            foreach (var file in _fileSystem.Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var matchingHandler = _handlers.FirstOrDefault(h => h.CanHandle(file));
                    if (matchingHandler != null)
                    {
                        result.Add((file, matchingHandler));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _pluginLog.LogWarning(
                        "LinkRepair",
                        $"Cannot inspect file: {file} - {ex.Message}",
                        ex,
                        _logger);
                }
            }

            foreach (var subDir in _fileSystem.Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FindLinkFilesRecursive(subDir, result, cancellationToken, visited);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            _pluginLog.LogWarning("LinkRepair", $"Cannot access directory: {directory} - {ex.Message}", ex, _logger);
        }
    }

    /// <summary>
    ///     Processes a single link file: reads the target path via the handler, validates it,
    ///     and attempts repair if broken.
    /// </summary>
    /// <param name="linkFilePath">The path to the link file.</param>
    /// <param name="handler">The handler that owns this link type.</param>
    /// <param name="dryRun">If true, no files will be modified.</param>
    /// <returns>The result for this file.</returns>
    internal LinkFileResult ProcessLinkFile(string linkFilePath, ILinkHandler handler, bool dryRun)
    {
        var fileResult = new LinkFileResult { LinkFilePath = linkFilePath };

        string? targetPath;
        try
        {
            targetPath = handler.ReadTarget(linkFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                "LinkRepair",
                $"Failed to read link file {linkFilePath}: {ex.Message}",
                ex,
                _logger);
            fileResult.Status = LinkFileStatus.InvalidContent;
            return fileResult;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            _pluginLog.LogWarning("LinkRepair", $"Failed to read link file {linkFilePath}", logger: _logger);
            fileResult.Status = LinkFileStatus.InvalidContent;
            return fileResult;
        }

        // Skip URL-based targets only for handlers that legitimately support them (e.g. .strm files).
        // Symlink targets are always filesystem paths — a "://" in a symlink target is not a URL.
        // file:// URIs are excluded because they reference local files and must be validated like paths.
        if (handler.SupportsUrlTargets
            && Uri.TryCreate(targetPath, UriKind.Absolute, out var uri)
            && uri.Scheme != Uri.UriSchemeFile)
        {
            _pluginLog.LogDebug("LinkRepair", $"Skipping URL-based link file: {linkFilePath}", _logger);
            fileResult.OriginalTargetPath = targetPath;
            fileResult.Status = LinkFileStatus.Valid;
            return fileResult;
        }

        fileResult.OriginalTargetPath = targetPath;
        string normalizedTargetPath;
        try
        {
            // Convert file:// URIs to local paths before normalization
            var pathToNormalize = targetPath;
            if (Uri.TryCreate(targetPath, UriKind.Absolute, out var fileUri)
                && fileUri.Scheme == Uri.UriSchemeFile)
            {
                pathToNormalize = fileUri.LocalPath;
            }

            normalizedTargetPath = _fileSystem.Path.IsPathRooted(pathToNormalize)
                ? _fileSystem.Path.GetFullPath(pathToNormalize)
                : _fileSystem.Path.GetFullPath(
                    _fileSystem.Path.Combine(
                        _fileSystem.Path.GetDirectoryName(linkFilePath) ?? string.Empty,
                        pathToNormalize));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _pluginLog.LogWarning(
                "LinkRepair",
                $"Invalid target path in link file {linkFilePath}: {targetPath}",
                ex,
                _logger);
            fileResult.Status = LinkFileStatus.InvalidContent;
            return fileResult;
        }

        // Keep OriginalTargetPath as-is (set above); use normalizedTargetPath for validation
        // Check if the target path is still valid
        if (_fileSystem.File.Exists(normalizedTargetPath))
        {
            _pluginLog.LogDebug("LinkRepair", $"Valid link file: {linkFilePath} -> {normalizedTargetPath}", _logger);
            fileResult.Status = LinkFileStatus.Valid;
            return fileResult;
        }

        // Target path is broken - try to repair
        _pluginLog.LogInfo("LinkRepair", $"Broken link file: {linkFilePath} -> {targetPath}", _logger);

        return TryRepairLinkFile(fileResult, handler, dryRun, normalizedTargetPath);
    }

    /// <summary>
    ///     Tries to repair a broken link file by searching the parent directory
    ///     of the broken target path for a media file.
    /// </summary>
    /// <param name="fileResult">The file result with the broken path info.</param>
    /// <param name="handler">The handler to use for writing the repaired target.</param>
    /// <param name="dryRun">If true, no files will be modified.</param>
    /// <param name="normalizedTargetPath">The normalized (absolute) target path for filesystem operations.</param>
    /// <returns>The updated file result.</returns>
    private LinkFileResult TryRepairLinkFile(LinkFileResult fileResult, ILinkHandler handler, bool dryRun, string normalizedTargetPath)
    {
        var parentDir = _fileSystem.Path.GetDirectoryName(normalizedTargetPath);

        if (string.IsNullOrEmpty(parentDir) || !_fileSystem.Directory.Exists(parentDir))
        {
            _pluginLog.LogWarning(
                "LinkRepair",
                $"Parent directory does not exist for broken link target: {fileResult.OriginalTargetPath} (parent: {parentDir ?? "null"})",
                logger: _logger);
            fileResult.Status = LinkFileStatus.Broken;
            return fileResult;
        }

        // Search the parent directory for media files
        var mediaFiles = FindMediaFilesInDirectory(parentDir);

        switch (mediaFiles.Count)
        {
            case 0:
                _pluginLog.LogWarning(
                    "LinkRepair",
                    $"No media files found in parent directory {parentDir} for broken link: {fileResult.LinkFilePath}",
                    logger: _logger);
                fileResult.Status = LinkFileStatus.Broken;
                return fileResult;
            case 1:
                {
                    // Exactly one media file found - this is our match
                    var newTargetPath = mediaFiles[0];
                    fileResult.NewTargetPath = newTargetPath;

                    if (dryRun)
                    {
                        _pluginLog.LogInfo(
                            "LinkRepair",
                            $"[Dry Run] Would repair link file: {fileResult.LinkFilePath} | {fileResult.OriginalTargetPath} -> {newTargetPath}",
                            _logger);
                        // Mark as Repaired (not Broken) so dry-run summary correctly reports
                        // "Would repair: N" instead of inflating the Broken count.
                        // LinkFileStatus.Repaired covers both actual repairs and dry-run candidates.
                        fileResult.Status = LinkFileStatus.Repaired;
                    }
                    else
                    {
                        _pluginLog.LogInfo(
                            "LinkRepair",
                            $"Repairing link file: {fileResult.LinkFilePath} | {fileResult.OriginalTargetPath} -> {newTargetPath}",
                            _logger);

                        try
                        {
                            handler.WriteTarget(fileResult.LinkFilePath, newTargetPath);
                            fileResult.Status = LinkFileStatus.Repaired;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                        {
                            _pluginLog.LogError(
                                "LinkRepair",
                                $"Failed to write repaired link file {fileResult.LinkFilePath}: {ex.Message}",
                                ex,
                                _logger);
                            fileResult.Status = LinkFileStatus.Broken;
                            fileResult.NewTargetPath = null;
                            return fileResult;
                        }
                    }

                    return fileResult;
                }
        }

        // Multiple media files found - ambiguous
        _pluginLog.LogWarning(
            "LinkRepair",
            $"Multiple media files ({mediaFiles.Count}) found in parent directory {parentDir} for broken link: {fileResult.LinkFilePath}. Candidates: {string.Join(", ", mediaFiles.Select(f => _fileSystem.Path.GetFileName(f)))}",
            logger: _logger);
        fileResult.Status = LinkFileStatus.Ambiguous;
        return fileResult;
    }

    /// <summary>
    ///     Finds all media files (video files) in the given directory (non-recursive).
    ///     <para>
    ///         Note: This enumerates all files in the directory without a count limit.
    ///         In edge cases with very large flat directories this could be slow, but
    ///         it is only called for the parent directory of a broken link target, so
    ///         the directory typically contains a small number of files.
    ///     </para>
    /// </summary>
    /// <param name="directory">The directory to search.</param>
    /// <returns>A list of media file paths.</returns>
    internal List<string> FindMediaFilesInDirectory(string directory)
    {
        var mediaFiles = new List<string>();

        try
        {
            mediaFiles.AddRange(
                from file in _fileSystem.Directory.EnumerateFiles(directory)
                let extension = _fileSystem.Path.GetExtension(file)
                where MediaExtensions.VideoExtensions.Contains(extension)
                select file);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            _pluginLog.LogWarning("LinkRepair", $"Cannot access directory: {directory} - {ex.Message}", ex, _logger);
        }

        return mediaFiles;
    }
}