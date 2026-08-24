using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     A scheduled task to clean up orphaned subtitle files (.srt, .ass, .sub, etc.)
///     that no longer have a corresponding video file with the same base name.
/// </summary>
/// <remarks>
///     <para>
///         Subtitle files typically follow a naming convention where the base name matches the video file:
///         <c>Movie Name (2021).mkv</c> -> <c>Movie Name (2021).en.srt</c> or <c>Movie Name (2021).srt</c>.
///     </para>
///     <para>
///         This task scans all directories recursively and for each subtitle file, checks whether any video
///         file with a matching base name exists in the same directory. The matching is flexible:
///         <c>Movie.en.srt</c> matches <c>Movie.mkv</c> because we strip language suffixes from the subtitle name.
///     </para>
///     <para>
///         Note: Only subtitle files are cleaned. Images like <c>backdrop.jpg</c>, <c>poster.jpg</c> etc.
///         are NOT touched because they typically don't follow the video-name pattern and serve the entire folder.
///     </para>
/// </remarks>
public class CleanOrphanedSubtitlesTask : BaseLibraryCleanupTask
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CleanOrphanedSubtitlesTask" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trackingService">The cleanup tracking service.</param>
    /// <param name="trashService">The trash service.</param>
    public CleanOrphanedSubtitlesTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        ILogger<CleanOrphanedSubtitlesTask> logger,
        ICleanupConfigHelper configHelper,
        ICleanupTrackingService trackingService,
        ITrashService trashService)
        : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
    {
    }

    /// <inheritdoc />
    protected override string TaskName => "SubtitleCleaner";

    /// <inheritdoc />
    protected override string ItemLabel => "files";

    /// <inheritdoc />
    protected override TaskMode GetTaskMode()
    {
        return ConfigHelper.GetOrphanedSubtitleTaskMode();
    }

    /// <inheritdoc />
    protected override (int Deleted, long BytesFreed) ProcessLocation(
        string libraryPath,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        long bytesFreed = 0;
        var config = ConfigHelper.GetConfig();

        try
        {
            // Process directories: for each directory, check all subtitle files
            var allDirs = new[] { libraryPath }.Concat(
                TryGetSubdirectories(libraryPath));

            // Hoist trash path computation out of loop - libraryPath is constant per iteration
            var trashFullPath = ConfigHelper.GetTrashPath(libraryPath);
            var normalizedTrash = Path.GetFullPath(trashFullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashSep = normalizedTrash + Path.DirectorySeparatorChar;
            var normalizedTrashAlt = normalizedTrash + Path.AltDirectorySeparatorChar;

            foreach (var dirPath in allDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryPrepareDirectory(
                        dirPath,
                        normalizedTrash,
                        normalizedTrashSep,
                        normalizedTrashAlt,
                        out var files,
                        out var videoBaseNames))
                {
                    continue;
                }

                // Check each subtitle file
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (fileDeleted, fileBytes) = ProcessSubtitleFile(
                        file,
                        videoBaseNames,
                        dryRun,
                        config,
                        trashFullPath);
                    deletedCount += fileDeleted;
                    bytesFreed += fileBytes;
                }
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            PluginLog.LogError(TaskName, $"Error scanning directory: {libraryPath}", ex, Logger);
        }

        return (deletedCount, bytesFreed);
    }

    /// <summary>
    ///     Applies the per-directory guards (trickplay skip, trash-tree skip, reparse-point skip),
    ///     lists the files, and builds the video base-name set. Verbatim extraction of the head of the
    ///     directory loop body in <see cref="ProcessLocation" />: each original <c>continue</c> becomes
    ///     <c>return false</c> (skip this directory). No path/extension guard, ordering, or condition is changed.
    /// </summary>
    /// <param name="dirPath">The directory to prepare.</param>
    /// <param name="normalizedTrash">The normalized trash path (no trailing separator).</param>
    /// <param name="normalizedTrashSep">The normalized trash path with a trailing directory separator.</param>
    /// <param name="normalizedTrashAlt">The normalized trash path with a trailing alt directory separator.</param>
    /// <param name="files">On success, the files listed in the directory.</param>
    /// <param name="videoBaseNames">On success, the set of video base names present in the directory.</param>
    /// <returns><c>true</c> if the directory should be processed; <c>false</c> to skip it.</returns>
    private bool TryPrepareDirectory(
        string dirPath,
        string normalizedTrash,
        string normalizedTrashSep,
        string normalizedTrashAlt,
        out FileSystemMetadata[] files,
        out HashSet<string> videoBaseNames)
    {
        files = Array.Empty<FileSystemMetadata>();
        videoBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Skip .trickplay folders - handled by CleanTrickplayTask
        if (Path.GetFileName(dirPath).EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Skip the trash folder and everything inside it
        var normalizedDir = Path.GetFullPath(dirPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (normalizedDir.Equals(normalizedTrash, comparison)
            || normalizedDir.StartsWith(normalizedTrashSep, comparison)
            || normalizedDir.StartsWith(normalizedTrashAlt, comparison))
        {
            return false;
        }

        // Symlink-traversal guard: FileInfo.LinkTarget below only inspects the FINAL
        // subtitle file, so a symlinked ANCESTOR directory could still redirect our
        // File.Delete into a real media tree. Skip any directory that is itself a reparse
        // point (symlink/junction). We only ever clean subtitles inside real directories.
        try
        {
            if (IsReparsePoint(dirPath))
            {
                PluginLog.LogWarning(TaskName, $"Skipping symlinked directory (reparse point): {dirPath}", logger: Logger);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogWarning(TaskName, $"Could not stat directory, skipping: {dirPath}", ex, Logger);
            return false;
        }

        try
        {
            files = FileSystem.GetFiles(dirPath).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogWarning(TaskName, $"Could not list files in: {dirPath}", ex, Logger);
            return false;
        }

        // Get all video base names in this directory
        foreach (var file in files)
        {
            if (MediaExtensions.VideoExtensions.Contains(Path.GetExtension(file.FullName)))
            {
                videoBaseNames.Add(Path.GetFileNameWithoutExtension(file.FullName));
            }
        }

        // If there are no videos in this directory at all, skip - subtitles here
        // are likely managed by the folder itself (season folder, etc.)
        // The EmptyMediaFolder task handles entire orphaned folders.
        if (videoBaseNames.Count == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Processes a single subtitle file: verbatim extraction of the per-file body of the
    ///     subtitle loop in <see cref="ProcessLocation" />. Behaviour is identical — each original
    ///     <c>continue</c> becomes a <c>return (0, 0)</c> (skip, nothing deleted). No TaskMode gate,
    ///     orphan-detection condition, path/extension guard, delete/trash decision, or ordering is changed.
    /// </summary>
    /// <param name="file">The candidate file entry from the directory listing.</param>
    /// <param name="videoBaseNames">The set of video base names present in the same directory.</param>
    /// <param name="dryRun">Whether the task is running in dry-run mode.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="trashFullPath">The resolved trash path for this location.</param>
    /// <returns>The number of files deleted (0 or 1) and the bytes freed by this file.</returns>
    private (int Deleted, long BytesFreed) ProcessSubtitleFile(
        FileSystemMetadata file,
        HashSet<string> videoBaseNames,
        bool dryRun,
        PluginConfiguration config,
        string trashFullPath)
    {
        if (!MediaExtensions.SubtitleExtensions.Contains(Path.GetExtension(file.FullName)))
        {
            return (0, 0);
        }

        // Extract the base name of the subtitle, stripping language suffixes
        // e.g., "Movie.en.srt" -> "Movie", "Movie.en.forced.srt" -> "Movie"
        var subtitleBaseName = GetSubtitleBaseName(file.FullName, videoBaseNames);

        if (videoBaseNames.Contains(subtitleBaseName))
        {
            return (0, 0); // Video exists, subtitle is valid
        }

        // Check orphan age
        if (!ConfigHelper.IsFileOldEnoughForDeletion(file.FullName))
        {
            PluginLog.LogDebug(
                TaskName,
                $"Skipping too-new orphaned subtitle (min age {config.OrphanMinAgeDays}d): {file.FullName}",
                Logger);
            return (0, 0);
        }

        // Symlink guard for ALL modes (dry-run, trash, hard-delete): a subtitle that is
        // itself a reparse point (symlink/junction) is never counted, never trashed, and
        // never deleted. On NAS setups the subtitle may be a link whose target lives in a
        // foreign tree; deleting or trashing the link relocates/removes the reference while
        // the target stays behind. Hoisted above the mode branches (mutually exclusive per
        // iteration) so a single check covers all three and dry-run output matches the real
        // run. Uses IsReparsePointAnyType because a directory-typed link can surface as a
        // file entry on some mounts. A stat failure is treated as "skip" (fail closed) and
        // must not surface as a misleading delete error.
        bool subtitleIsReparsePoint;
        try
        {
            subtitleIsReparsePoint = IsReparsePointAnyType(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogWarning(TaskName, $"Could not stat subtitle, skipping: {file.FullName}", ex, Logger);
            return (0, 0);
        }

        if (subtitleIsReparsePoint)
        {
            PluginLog.LogWarning(TaskName, $"Skipping symlinked subtitle file: {file.FullName}", logger: Logger);
            return (0, 0);
        }

        if (dryRun)
        {
            PluginLog.LogInfo(
                TaskName,
                $"[Dry Run] Would delete orphaned subtitle: {file.FullName}",
                Logger);
            return (1, 0);
        }

        if (config.UseTrash)
        {
            PluginLog.LogInfo(TaskName, $"Moving orphaned subtitle to trash: {file.FullName}", Logger);
            var size = TrashService.MoveFileToTrash(file.FullName, trashFullPath, Logger);
            if (size <= 0)
            {
                return (0, 0);
            }

            return (1, size);
        }

        PluginLog.LogInfo(TaskName, $"Deleting orphaned subtitle: {file.FullName}", Logger);
        try
        {
            // Re-read file size from disk immediately before deletion to avoid
            // stale values from the earlier directory-listing snapshot (H-13).
            var subtitleInfo = new FileInfo(file.FullName);
            var freshSize = subtitleInfo.Exists ? subtitleInfo.Length : 0;
            File.Delete(file.FullName);
            return (1, freshSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogError(TaskName, $"Failed to delete: {file.FullName}", ex, Logger);
            return (0, 0);
        }
    }

    /// <summary>
    ///     Extracts the base name of a subtitle file, stripping language and format suffixes.
    ///     For example:
    ///     "Movie Name (2021).en.srt" -> "Movie Name (2021)"
    ///     "Movie Name (2021).en.forced.srt" -> "Movie Name (2021)"
    ///     "Movie Name (2021).srt" -> "Movie Name (2021)"
    ///     "Movie Name (2021).de.hi.ass" -> "Movie Name (2021)"
    ///     "Movie Name (2021).es-MX.srt" -> "Movie Name (2021)"
    ///     "Movie Name (2021).pt-BR.forced.srt" -> "Movie Name (2021)"
    ///     "Movie Name (2021).zh-Hans.srt" -> "Movie Name (2021)".
    ///     If the stripped result does not match any known video base name, falls back to
    ///     the original unsplit name (i.e., the filename without its subtitle extension)
    ///     to avoid false-orphan detection when the movie title itself contains language codes.
    /// </summary>
    /// <param name="filePath">The full path to the subtitle file.</param>
    /// <param name="videoBaseNames">The set of video base names present in the same directory.</param>
    /// <returns>The base name without language and format suffixes, or the original unsplit name as fallback.</returns>
    internal static string GetSubtitleBaseName(string filePath, HashSet<string> videoBaseNames)
    {
        // Start with filename without extension: "Movie.en.forced"
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        // Known subtitle suffixes to strip (language codes, flags)
        // We strip from right to left as long as the last segment matches a known pattern
        var parts = nameWithoutExt.Split('.');
        if (parts.Length <= 1)
        {
            return nameWithoutExt;
        }

        // Strip known language/flag suffixes from the end
        var endIndex = parts.Length - 1;
        while (endIndex > 0 && IsSubtitleSuffix(parts[endIndex]))
        {
            endIndex--;
        }

        // Rejoin the parts up to endIndex
        var candidateBase = string.Join('.', parts, 0, endIndex + 1);

        // Verify the stripped candidate actually matches a video file in the directory.
        // If the movie title itself contains something that looks like a language code
        // (e.g. "en.mkv"), stripping would produce a wrong base name. Fall back to the
        // original unsplit name so that the caller's videoBaseNames lookup stays accurate.
        if (videoBaseNames.Contains(candidateBase))
        {
            return candidateBase;
        }

        return nameWithoutExt;
    }

    /// <summary>
    ///     Determines whether a string segment is a known subtitle suffix (language code or flag).
    ///     Supports simple codes (e.g., "en", "eng", "forced") as well as BCP-47 regional/script
    ///     tags (e.g., "es-MX", "pt-BR", "zh-Hans", "sr-Latn").
    ///     Uses explicit allowlists to avoid false positives with non-language segments like "DTS", "HDR", etc.
    /// </summary>
    /// <param name="segment">The dot-separated segment to check.</param>
    /// <returns>True if the segment is a recognized subtitle suffix; otherwise false.</returns>
    internal static bool IsSubtitleSuffix(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        // Direct match: known flags (forced, sdh, hi, cc, etc.) or language codes (en, de, eng, deu, etc.)
        if (MediaExtensions.SubtitleFlags.Contains(segment) || MediaExtensions.KnownLanguageCodes.Contains(segment))
        {
            return true;
        }

        // BCP-47 regional/script tags: "es-MX", "pt-BR", "zh-Hans", "sr-Latn", "en-US",
        // "es-419", "zh-Hans-TW", etc.
        // Supported formats:
        //   {lang}-{region}         e.g. en-US, pt-BR
        //   {lang}-{3-digit-region} e.g. es-419
        //   {lang}-{script}         e.g. zh-Hans, sr-Latn
        //   {lang}-{script}-{region} e.g. zh-Hans-TW
        var subtags = segment.Split('-');
        if (subtags.Length < 2 || subtags.Length > 3)
        {
            return false;
        }

        if (!MediaExtensions.KnownLanguageCodes.Contains(subtags[0]))
        {
            return false;
        }

        // Helper: 2-letter alphabetic region (ISO 3166-1 alpha-2)
        static bool IsAlphaRegion(string value) =>
            value.Length == 2 && char.IsLetter(value[0]) && char.IsLetter(value[1]);

        // Helper: 3-digit numeric region (UN M.49, e.g. 419)
        static bool IsNumericRegion(string value) =>
            value.Length == 3 && char.IsDigit(value[0]) && char.IsDigit(value[1]) && char.IsDigit(value[2]);

        // Helper: 4-letter script (ISO 15924, e.g. Hans, Latn, Cyrl)
        static bool IsScript(string value) =>
            value.Length == 4 && char.IsLetter(value[0]) && char.IsLetter(value[1])
            && char.IsLetter(value[2]) && char.IsLetter(value[3]);

        if (subtags.Length == 2)
        {
            // {lang}-{region} or {lang}-{script}
            return IsAlphaRegion(subtags[1]) || IsNumericRegion(subtags[1]) || IsScript(subtags[1]);
        }

        // subtags.Length == 3: {lang}-{script}-{region}
        return IsScript(subtags[1]) && (IsAlphaRegion(subtags[2]) || IsNumericRegion(subtags[2]));
    }

    /// <summary>
    ///     Returns all subdirectories under <paramref name="libraryPath" /> using an explicit stack
    ///     so that a single unreadable directory does not abort the scan and there is no
    ///     per-call recursion depth risk on deep trees.
    /// </summary>
    private List<string> TryGetSubdirectories(string libraryPath)
    {
        var result = new List<string>();
        var stack = new Stack<string>();

        // Reject a reparse-point (symlink/junction) library ROOT before enumerating its children.
        // FileSystem.GetDirectories(libraryPath) returns the children as ordinary paths, and the
        // root itself is never pushed onto the stack, so the per-directory reparse guard in
        // ProcessLocation never sees the symlinked ancestor. Without this check the children of a
        // symlinked root would be traversed and cleaned inside a foreign tree. A stat failure is
        // treated as "do not traverse" (fail closed).
        try
        {
            if (IsReparsePoint(libraryPath))
            {
                PluginLog.LogWarning(TaskName, $"Skipping symlinked library root (reparse point): {libraryPath}", logger: Logger);
                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogWarning(TaskName, $"Could not stat library root, not traversing: {libraryPath}", ex, Logger);
            return result;
        }

        // Seed the stack with the direct children of the library root.
        try
        {
            foreach (var d in FileSystem.GetDirectories(libraryPath))
            {
                stack.Push(d.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogWarning(TaskName, $"Could not enumerate subdirectories of: {libraryPath}", ex, Logger);
            return result;
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            result.Add(current);

            // Do not enumerate children of reparse-point (symlink/junction) directories.
            // The per-directory guard in the caller already skips processing their content;
            // not traversing here prevents following links into foreign trees before that
            // guard has a chance to run. A stat failure is treated as "do not traverse"
            // (fail closed) so a single unreadable entry does not abort the whole scan.
            bool currentIsReparsePoint;
            try
            {
                currentIsReparsePoint = IsReparsePoint(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(TaskName, $"Could not stat directory, not traversing: {current}", ex, Logger);
                continue;
            }

            if (currentIsReparsePoint)
            {
                continue;
            }

            try
            {
                foreach (var d in FileSystem.GetDirectories(current))
                {
                    stack.Push(d.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(TaskName, $"Could not enumerate subdirectories of: {current}", ex, Logger);
            }
        }

        return result;
    }
}