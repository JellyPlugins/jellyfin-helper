using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>
///     Service that calculates media file statistics per library type.
/// </summary>
public class MediaStatisticsService : IMediaStatisticsService
{
    private readonly ICleanupConfigHelper _configHelper;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaStatisticsService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MediaStatisticsService" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    public MediaStatisticsService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        ILogger<MediaStatisticsService> logger,
        ICleanupConfigHelper configHelper)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
    }

    /// <summary>
    ///     Calculates statistics for all configured libraries.
    /// </summary>
    /// <returns>The aggregated media statistics.</returns>
    public MediaStatisticsResult CalculateStatistics()
    {
        var result = new MediaStatisticsResult();

        // Resolve trash folder name once - constant for the entire scan.
        var scanConfig = _configHelper.GetConfig();
        var scanTrashFolderName = (scanConfig.TrashFolderPath ?? string.Empty).Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var virtualFolders = _libraryManager.GetVirtualFolders();
        _pluginLog.LogInfo(
            "MediaStatistics",
            $"Starting media statistics scan for {virtualFolders.Count} libraries",
            _logger);

        // Pre-build a lookup of file paths -> BaseItem for all known library items.
        // This avoids calling FindByPath() per file during the scan, significantly
        // improving performance for large libraries.
        var itemLookup = BuildItemLookup();
        _pluginLog.LogDebug(
            "MediaStatistics",
            $"Pre-loaded {itemLookup.Count} library items for metadata lookup",
            _logger);

        foreach (var vf in virtualFolders)
        {
            var collectionType = vf.CollectionType;

            var isBoxsets = collectionType is CollectionTypeOptions.boxsets;
            var isMovies = collectionType is CollectionTypeOptions.movies
                or CollectionTypeOptions.homevideos
                or CollectionTypeOptions.musicvideos;
            var isTvShows = collectionType is CollectionTypeOptions.tvshows;
            var isMusic = collectionType is CollectionTypeOptions.music;

            var libraryStats = new LibraryStatistics
            {
                LibraryName = vf.Name ?? "Unknown",
                CollectionType = collectionType?.ToString() ?? "mixed"
            };

            // Health checks only apply to video libraries (Movies, TV Shows).
            // Music and boxset/collection libraries are excluded.
            var skipHealth = isBoxsets || isMusic;

            foreach (var location in vf.Locations)
            {
                libraryStats.RootPaths.Add(location);
                _pluginLog.LogDebug(
                    "MediaStatistics",
                    $"Scanning library location: {location} (type: {collectionType})",
                    _logger);

                // Pre-compute the resolved absolute trash path once per library root so it
                // is not re-derived on every recursive directory call.
                string? resolvedFullTrashPath = null;
                try
                {
                    resolvedFullTrashPath = Path.GetFullPath(_configHelper.GetTrashPath(location))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    _pluginLog.LogWarning(
                        "MediaStatistics",
                        $"Could not resolve trash path for library root: {location}",
                        ex,
                        _logger);
                }

                AnalyzeDirectoryRecursive(location, libraryStats, itemLookup, location, skipHealth, scanTrashFolderName, resolvedFullTrashPath);
            }

            _pluginLog.LogDebug(
                "MediaStatistics",
                $"Library '{libraryStats.LibraryName}': {libraryStats.VideoFileCount} videos, {libraryStats.AudioFileCount} audio, " +
                $"{libraryStats.SubtitleFileCount} subs, {libraryStats.TrickplayFolderCount} trickplay folders",
                _logger);
            result.Libraries.Add(libraryStats);

            if (isTvShows)
            {
                result.TvShows.Add(libraryStats);
            }
            else if (isMusic)
            {
                result.Music.Add(libraryStats);
            }
            else if (isMovies)
            {
                result.Movies.Add(libraryStats);
            }
            else
            {
                result.Other.Add(libraryStats);
            }
        }

        // Log summary
        var totalFiles = result.Libraries.Sum(l =>
            l.VideoFileCount + l.AudioFileCount + l.SubtitleFileCount + l.ImageFileCount + l.NfoFileCount +
            l.OtherFileCount);
        var totalSize = result.Libraries.Sum(l => l.TotalSize);
        _pluginLog.LogInfo(
            "MediaStatistics",
            $"Scan complete: {result.Libraries.Count} libraries, {totalFiles} files, {totalSize / (1024 * 1024)} MB total, " +
            $"{result.Libraries.Sum(l => l.VideosWithoutSubtitles)} videos without subs, " +
            $"{result.Libraries.Sum(l => l.VideosWithoutImages)} without images, " +
            $"{result.Libraries.Sum(l => l.OrphanedMetadataDirectories)} orphaned metadata dirs",
            _logger);

        result.ScanTimestamp = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    ///     Builds a lookup dictionary mapping file paths to their Jellyfin library items.
    ///     This pre-loads all items once to avoid per-file FindByPath calls during scanning.
    /// </summary>
    /// <returns>A case-insensitive dictionary of file path -> BaseItem.</returns>
    internal virtual Dictionary<string, BaseItem> BuildItemLookup()
    {
        var lookup = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var allItems = _libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    MediaTypes = [MediaType.Video, MediaType.Audio],
                    IsFolder = false
                });
            foreach (var item in allItems)
            {
                if (!string.IsNullOrEmpty(item.Path))
                {
                    lookup.TryAdd(item.Path, item);
                }
            }
        }
        catch (Exception ex)
        {
            _pluginLog.LogWarning(
                "MediaStatistics",
                "Could not pre-load library items for metadata lookup; falling back to per-file lookup",
                ex,
                _logger);
        }

        return lookup;
    }

    private long CalculateTrickplaySize(string directoryPath)
    {
        long total = 0;
        try
        {
            foreach (var file in _fileSystem.GetFiles(directoryPath, false))
            {
                total += file.Length;
            }

            foreach (var sub in _fileSystem.GetDirectories(directoryPath, false))
            {
                total += CalculateTrickplaySize(sub.FullName);
            }
        }
        catch (IOException)
        {
            // Intentionally empty: an unreadable path is skipped (best-effort trickplay size scan).
        }
        catch (UnauthorizedAccessException)
        {
            // Intentionally empty: an inaccessible path is skipped (best-effort trickplay size scan).
        }

        return total;
    }

    /// <summary>
    ///     Recursively analyzes a directory and accumulates file size statistics.
    /// </summary>
    /// <param name="directoryPath">The directory to analyze.</param>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="itemLookup">Pre-built lookup of file paths -> BaseItem for metadata extraction.</param>
    /// <param name="libraryRoot">The library root path (used for trash folder resolution).</param>
    /// <param name="skipHealthChecks">When true, skip health check counters (e.g. for boxset/collection libraries).</param>
    /// <param name="trashFolderName">Pre-resolved trash folder name; passed down to avoid re-reading config on every recursive call.</param>
    /// <param name="resolvedFullTrashPath">
    ///     The normalized absolute trash path for this library root, pre-computed by the caller
    ///     and threaded through every recursive call to avoid re-invoking
    ///     <see cref="ICleanupConfigHelper.GetTrashPath" /> on every directory.
    ///     Null when <paramref name="libraryRoot" /> is null.
    /// </param>
    private bool AnalyzeDirectoryRecursive(
        string directoryPath,
        LibraryStatistics stats,
        Dictionary<string, BaseItem> itemLookup,
        string? libraryRoot = null,
        bool skipHealthChecks = false,
        string? trashFolderName = null,
        string? resolvedFullTrashPath = null)
    {
        var containsVideo = false;
        try
        {
            var files = _fileSystem.GetFiles(directoryPath).ToList();

            var flags = new DirectoryFileFlags();

            // Collect video files for health checks and metadata extraction
            var videoFiles = new List<FileSystemMetadata>();
            // Cache streams from ExtractVideoMetadata to avoid redundant GetMediaStreams() calls
            // when checking for embedded subtitles later in health checks.
            var videoStreamsCache =
                new Dictionary<string, IReadOnlyList<MediaStream>?>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                ClassifyFile(
                    file,
                    stats,
                    itemLookup,
                    videoFiles,
                    videoStreamsCache,
                    flags);
            }

            containsVideo = flags.ContainsVideo;

            // Recurse into subdirectories
            var subDirs = _fileSystem.GetDirectories(directoryPath);

            var resolvedTrashFolderName = trashFolderName ?? string.Empty;

            // resolvedFullTrashPath is computed once at the top-level call site and threaded
            // through every recursive call - no config re-read on each directory.
            var scanContext = new SubdirectoryScanContext(
                resolvedTrashFolderName,
                libraryRoot,
                skipHealthChecks,
                trashFolderName,
                resolvedFullTrashPath);

            var subDirHasVideo = ScanSubdirectories(
                subDirs,
                stats,
                itemLookup,
                scanContext,
                ref containsVideo);

            // Health checks - per-directory analysis
            // Boxset/collection libraries are excluded: they are Jellyfin-internal virtual folders
            // that group related movies and typically only contain posters/images, not real media.
            if (!skipHealthChecks)
            {
                if (flags.HasVideo)
                {
                    RecordVideoHealth(
                        stats,
                        files,
                        videoFiles,
                        videoStreamsCache,
                        flags.HasImage,
                        flags.HasNfo);
                }
                else if (flags.HasAnyNonTrickplayFile && (flags.HasSubs || flags.HasImage || flags.HasNfo))
                {
                    RecordOrphanedMetadata(stats, directoryPath, subDirHasVideo);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning("MediaStatistics", $"Could not access directory: {directoryPath}", ex, _logger);
        }

        return containsVideo;
    }

    /// <summary>
    ///     Classifies a single file by extension and accumulates its size/count into the
    ///     statistics, updating the per-directory presence flags. Extracted verbatim from the
    ///     file loop of <see cref="AnalyzeDirectoryRecursive"/>.
    /// </summary>
    /// <param name="file">The file to classify.</param>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="itemLookup">Pre-built lookup of file paths -> BaseItem for metadata extraction.</param>
    /// <param name="videoFiles">Accumulator of video files for later health checks.</param>
    /// <param name="videoStreamsCache">Cache of media streams keyed by video file path.</param>
    /// <param name="flags">The per-directory presence flags updated as files are classified.</param>
    private void ClassifyFile(
        FileSystemMetadata file,
        LibraryStatistics stats,
        Dictionary<string, BaseItem> itemLookup,
        List<FileSystemMetadata> videoFiles,
        Dictionary<string, IReadOnlyList<MediaStream>?> videoStreamsCache,
        DirectoryFileFlags flags)
    {
        var ext = Path.GetExtension(file.FullName);
        var size = file.Length;
        // Set for any non-trickplay, non-audio file.
        // Orphaned-metadata detection uses this flag; audio files are excluded to
        // avoid false positives in audio-only directories.
        if (!MediaExtensions.AudioExtensionToCodec.ContainsKey(ext))
        {
            flags.HasAnyNonTrickplayFile = true;
        }

        if (MediaExtensions.VideoExtensions.Contains(ext))
        {
            stats.VideoSize += size;
            stats.VideoFileCount++;
            flags.HasVideo = true;
            flags.ContainsVideo = true;
            videoFiles.Add(file);

            // Container format tracking (from file extension - this IS the container)
            var container = ext.TrimStart('.').ToUpperInvariant();
            FileSystemHelper.IncrementCount(stats.ContainerFormats, container);
            FileSystemHelper.AccumulateValue(stats.ContainerSizes, container, size);
            FileSystemHelper.AddPath(stats.ContainerFormatPaths, container, file.FullName);

            // Extract metadata from Jellyfin MediaStreams (resolution, codecs, dynamic range)
            // and cache the streams for subtitle health checks below
            var streams = ExtractVideoMetadata(file.FullName, size, stats, itemLookup);
            videoStreamsCache[file.FullName] = streams;
        }
        else if (MediaExtensions.SubtitleExtensions.Contains(ext))
        {
            stats.SubtitleSize += size;
            stats.SubtitleFileCount++;
            flags.HasSubs = true;
        }
        else if (MediaExtensions.ImageExtensions.Contains(ext))
        {
            stats.ImageSize += size;
            stats.ImageFileCount++;
            flags.HasImage = true;
        }
        else if (MediaExtensions.NfoExtensions.Contains(ext))
        {
            stats.NfoSize += size;
            stats.NfoFileCount++;
            flags.HasNfo = true;
        }
        else if (MediaExtensions.AudioExtensionToCodec.ContainsKey(ext))
        {
            stats.AudioSize += size;
            stats.AudioFileCount++;

            // Extract music audio codec from Jellyfin metadata with extension fallback
            ExtractMusicAudioMetadata(file.FullName, ext, size, stats, itemLookup);
        }
        else
        {
            stats.OtherSize += size;
            stats.OtherFileCount++;
        }
    }

    /// <summary>
    ///     Iterates the subdirectories of the current directory, skipping the trash folder,
    ///     accumulating trickplay statistics, and recursing into the rest. Extracted verbatim
    ///     from the subdirectory loop of <see cref="AnalyzeDirectoryRecursive"/>.
    /// </summary>
    /// <param name="subDirs">The subdirectories to scan.</param>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="itemLookup">Pre-built lookup of file paths -> BaseItem for metadata extraction.</param>
    /// <param name="scanContext">The trash-resolution and recursion context.</param>
    /// <param name="containsVideo">Set true when any recursed subdirectory contained a video.</param>
    /// <returns><c>true</c> when at least one subdirectory contained a video.</returns>
    private bool ScanSubdirectories(
        IEnumerable<FileSystemMetadata> subDirs,
        LibraryStatistics stats,
        Dictionary<string, BaseItem> itemLookup,
        SubdirectoryScanContext scanContext,
        ref bool containsVideo)
    {
        var subDirHasVideo = false;

        foreach (var subDir in subDirs)
        {
            var normalizedSubDirFullName = Path.GetFullPath(subDir.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(
                    subDir.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    scanContext.ResolvedTrashFolderName,
                    StringComparison.OrdinalIgnoreCase)
                || (scanContext.ResolvedFullTrashPath != null && string.Equals(
                    normalizedSubDirFullName,
                    scanContext.ResolvedFullTrashPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (subDir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
            {
                var trickplaySize = CalculateTrickplaySize(subDir.FullName);
                stats.TrickplaySize += trickplaySize;
                stats.TrickplayFolderCount++;
            }
            else if (AnalyzeDirectoryRecursive(subDir.FullName, stats, itemLookup, scanContext.LibraryRoot, scanContext.SkipHealthChecks, scanContext.TrashFolderName, scanContext.ResolvedFullTrashPath))
            {
                subDirHasVideo = true;
                containsVideo = true;
            }
        }

        return subDirHasVideo;
    }

    /// <summary>
    ///     Records video-directory health statistics (missing subtitles, images, NFO) for a
    ///     directory that contains at least one video. Extracted verbatim from the health-check
    ///     block of <see cref="AnalyzeDirectoryRecursive"/>.
    /// </summary>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="files">All files in the current directory.</param>
    /// <param name="videoFiles">The video files in the current directory.</param>
    /// <param name="videoStreamsCache">Cache of media streams keyed by video file path.</param>
    /// <param name="hasImage">Whether the directory contains an image file.</param>
    /// <param name="hasNfo">Whether the directory contains an NFO file.</param>
    private void RecordVideoHealth(
        LibraryStatistics stats,
        List<FileSystemMetadata> files,
        List<FileSystemMetadata> videoFiles,
        Dictionary<string, IReadOnlyList<MediaStream>?> videoStreamsCache,
        bool hasImage,
        bool hasNfo)
    {
        var videoCount = videoFiles.Count;

        // Build a set of video stems that have a matching sidecar subtitle file
        var subsStems = files
            .Where(f => MediaExtensions.SubtitleExtensions.Contains(Path.GetExtension(f.FullName)))
            .Select(f => Path.GetFileNameWithoutExtension(f.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var vf2 in videoFiles)
        {
            var videoStem = Path.GetFileNameWithoutExtension(vf2.FullName);
            var hasSidecar = subsStems.Any(s =>
                string.Equals(s, videoStem, StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase));
            if (!hasSidecar &&
                !HasEmbeddedSubtitles(vf2.FullName, videoStreamsCache.GetValueOrDefault(vf2.FullName)))
            {
                stats.VideosWithoutSubtitles++;
                stats.VideosWithoutSubtitlesPaths.Add(vf2.FullName);
            }
        }

        if (!hasImage)
        {
            stats.VideosWithoutImages += videoCount;
            foreach (var vf2 in videoFiles)
            {
                stats.VideosWithoutImagesPaths.Add(vf2.FullName);
            }
        }

        if (!hasNfo)
        {
            stats.VideosWithoutNfo += videoCount;
            foreach (var vf2 in videoFiles)
            {
                stats.VideosWithoutNfoPaths.Add(vf2.FullName);
            }
        }
    }

    /// <summary>
    ///     Records an orphaned-metadata directory (metadata-only, no video) unless it is a
    ///     TV show container folder. Extracted verbatim from the health-check block of
    ///     <see cref="AnalyzeDirectoryRecursive"/>.
    /// </summary>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="directoryPath">The directory being analyzed.</param>
    /// <param name="subDirHasVideo">Whether any subdirectory contained a video.</param>
    private static void RecordOrphanedMetadata(
        LibraryStatistics stats,
        string directoryPath,
        bool subDirHasVideo)
    {
        // Special case for TV Shows: Don't mark as orphaned if it contains subdirectories with videos
        // (e.g. "Series 1" folder containing "Season 01")
        // OR if it is a known TV show container folder (Specials, Season XX)
        var isTvShow = string.Equals(stats.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase);
        var isTvContainer = false;
        if (isTvShow)
        {
            var dirName = Path.GetFileName(directoryPath);
            if (string.Equals(dirName, "Specials", StringComparison.OrdinalIgnoreCase) ||
                dirName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) ||
                dirName.StartsWith("Staffel ", StringComparison.OrdinalIgnoreCase))
            {
                isTvContainer = true;
            }
        }

        if (!isTvShow || (!subDirHasVideo && !isTvContainer))
        {
            stats.OrphanedMetadataDirectories++;
            stats.OrphanedMetadataDirectoriesPaths.Add(directoryPath);
        }
    }

    /// <summary>
    ///     Resolves the Jellyfin library item for a given file path by first checking the
    ///     pre-built batch lookup, then falling back to a per-file <see cref="ILibraryManager.FindByPath"/>
    ///     call. This ensures metadata is still available when the batch lookup missed the file
    ///     (e.g. newly added files or a failed bulk load).
    /// </summary>
    /// <param name="filePath">Full path to the media file.</param>
    /// <param name="itemLookup">Pre-built lookup of file paths -> BaseItem.</param>
    /// <returns>The resolved <see cref="BaseItem"/>, or <c>null</c> when the file is unknown to Jellyfin.</returns>
    private BaseItem? ResolveLibraryItem(string filePath, Dictionary<string, BaseItem> itemLookup)
    {
        if (itemLookup.TryGetValue(filePath, out var item))
        {
            return item;
        }

        // Per-file fallback when the batch lookup missed this file (e.g. newly added,
        // or BuildItemLookup failed and returned an empty dictionary).
        try
        {
            return _libraryManager.FindByPath(filePath, false);
        }
        catch (Exception ex)
        {
            _pluginLog.LogDebug(
                "MediaStatistics",
                $"Per-file fallback lookup failed for: {filePath}. {ex.GetType().Name}: {ex.Message}",
                _logger);
            return null;
        }
    }

    /// <summary>
    ///     Safely retrieves media streams for a library item, returning <c>null</c> on failure.
    /// </summary>
    /// <param name="item">The library item to query.</param>
    /// <param name="filePath">The file path (used for diagnostic logging only).</param>
    /// <returns>The media streams, or <c>null</c> when unavailable.</returns>
    private IReadOnlyList<MediaStream>? GetMediaStreamsSafe(BaseItem item, string filePath)
    {
        try
        {
            return item.GetMediaStreams();
        }
        catch (Exception ex)
        {
            _pluginLog.LogDebug(
                "MediaStatistics",
                $"Could not read media streams for: {filePath}. {ex.GetType().Name}: {ex.Message}",
                _logger);
            return null;
        }
    }

    /// <summary>
    ///     Extracts video metadata (codec, resolution, audio codec, dynamic range) from Jellyfin
    ///     MediaStream data and records it in the statistics. Uses a two-tier lookup: batch
    ///     dictionary first, then per-file <see cref="ILibraryManager.FindByPath"/> fallback.
    ///     Falls back to "Unknown" only when both lookups fail.
    /// </summary>
    /// <param name="filePath">Full path to the video file.</param>
    /// <param name="fileSize">Size of the video file in bytes.</param>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="itemLookup">Pre-built lookup of file paths -> BaseItem.</param>
    /// <returns>The media streams for the file, or <c>null</c> if unavailable (reused for subtitle checks).</returns>
    private IReadOnlyList<MediaStream>? ExtractVideoMetadata(
        string filePath,
        long fileSize,
        LibraryStatistics stats,
        Dictionary<string, BaseItem> itemLookup)
    {
        IReadOnlyList<MediaStream>? streams = null;

        var item = ResolveLibraryItem(filePath, itemLookup);
        if (item is not null)
        {
            streams = GetMediaStreamsSafe(item, filePath);
        }

        var videoStream = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audioStream = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

        // Video codec
        var videoCodec = ClassifyVideoCodec(videoStream?.Codec);
        FileSystemHelper.IncrementCount(stats.VideoCodecs, videoCodec);
        FileSystemHelper.AccumulateValue(stats.VideoCodecSizes, videoCodec, fileSize);
        FileSystemHelper.AddPath(stats.VideoCodecPaths, videoCodec, filePath);

        // Resolution from stream dimensions
        var resolution = ClassifyResolution(videoStream?.Width, videoStream?.Height);
        FileSystemHelper.IncrementCount(stats.Resolutions, resolution);
        FileSystemHelper.AccumulateValue(stats.ResolutionSizes, resolution, fileSize);
        FileSystemHelper.AddPath(stats.ResolutionPaths, resolution, filePath);

        // Dynamic range from video stream metadata
        var dynamicRange = ClassifyDynamicRange(videoStream);
        FileSystemHelper.IncrementCount(stats.DynamicRanges, dynamicRange);
        FileSystemHelper.AccumulateValue(stats.DynamicRangeSizes, dynamicRange, fileSize);
        FileSystemHelper.AddPath(stats.DynamicRangePaths, dynamicRange, filePath);

        // Audio codec from the primary audio stream (with profile differentiation)
        var audioCodec = ClassifyAudioCodec(audioStream?.Codec, audioStream?.Profile);
        if (!string.Equals(audioCodec, "Unknown", StringComparison.Ordinal))
        {
            FileSystemHelper.IncrementCount(stats.VideoAudioCodecs, audioCodec);
            FileSystemHelper.AccumulateValue(stats.VideoAudioCodecSizes, audioCodec, fileSize);
            FileSystemHelper.AddPath(stats.VideoAudioCodecPaths, audioCodec, filePath);
        }

        return streams;
    }

    /// <summary>
    ///     Extracts music audio codec metadata from Jellyfin MediaStream data and records it
    ///     in the statistics. Uses a two-tier item lookup (batch dictionary -> per-file FindByPath)
    ///     and falls back to extension-based mapping when neither lookup yields stream data.
    /// </summary>
    /// <param name="filePath">Full path to the audio file.</param>
    /// <param name="extension">The file extension (with leading dot).</param>
    /// <param name="fileSize">Size of the audio file in bytes.</param>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="itemLookup">Pre-built lookup of file paths -> BaseItem.</param>
    private void ExtractMusicAudioMetadata(
        string filePath,
        string extension,
        long fileSize,
        LibraryStatistics stats,
        Dictionary<string, BaseItem> itemLookup)
    {
        string audioCodec;

        var item = ResolveLibraryItem(filePath, itemLookup);
        if (item is not null)
        {
            var streams = GetMediaStreamsSafe(item, filePath);
            var audioStream = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            audioCodec = ClassifyAudioCodec(audioStream?.Codec, audioStream?.Profile);

            // When Jellyfin has the item but streams yield no useful codec, fall back to extension
            if (string.Equals(audioCodec, "Unknown", StringComparison.Ordinal))
            {
                audioCodec = MediaExtensions.AudioExtensionToCodec.GetValueOrDefault(extension, "Unknown");
            }
        }
        else
        {
            // Fallback: use extension-based mapping when Jellyfin doesn't know the file
            audioCodec = MediaExtensions.AudioExtensionToCodec.GetValueOrDefault(extension, "Unknown");
        }

        FileSystemHelper.IncrementCount(stats.MusicAudioCodecs, audioCodec);
        FileSystemHelper.AccumulateValue(stats.MusicAudioCodecSizes, audioCodec, fileSize);
        FileSystemHelper.AddPath(stats.MusicAudioCodecPaths, audioCodec, filePath);
    }

    /// <summary>
    ///     Checks whether the given video file has embedded (non-external) subtitle streams
    ///     using already-loaded media streams to avoid redundant lookups.
    /// </summary>
    /// <param name="filePath">Full path to the video file (used for testability/logging).</param>
    /// <param name="streams">The pre-loaded media streams from <see cref="ExtractVideoMetadata"/>.</param>
    /// <returns><c>true</c> when at least one embedded subtitle stream exists.</returns>
    internal virtual bool HasEmbeddedSubtitles(string filePath, IReadOnlyList<MediaStream>? streams)
    {
        return streams is not null && streams.Any(s => s is { Type: MediaStreamType.Subtitle, IsExternal: false });
    }

    /// <summary>
    ///     Classifies a video codec string from MediaStream metadata into a display label.
    /// </summary>
    /// <param name="codec">The codec string from MediaStream (e.g. "hevc", "h264", "av1").</param>
    /// <returns>A display label such as "HEVC", "H.264", "AV1", or "Unknown".</returns>
    internal static string ClassifyVideoCodec(string? codec)
    {
        if (string.IsNullOrEmpty(codec))
        {
            return "Unknown";
        }

        var upperCodec = codec.ToUpperInvariant();
        return upperCodec switch
        {
            "HEVC" or "H265" or "H.265" => "HEVC",
            "H264" or "H.264" or "AVC" => "H.264",
            "AV1" => "AV1",
            "VP9" => "VP9",
            "VP8" => "VP8",
            "MPEG2VIDEO" or "MPEG2" or "MP2V" => "MPEG-2",
            "MPEG4" or "MPEG-4" => "MPEG-4",
            "XVID" => "XviD",
            "DIVX" => "DivX",
            "VC1" or "VC-1" or "WMV3" => "VC-1",
            "THEORA" => "Theora",
            _ => upperCodec
        };
    }

    /// <summary>
    ///     Classifies a resolution from stream width/height into a display tier.
    /// </summary>
    /// <param name="width">The video stream width in pixels.</param>
    /// <param name="height">The video stream height in pixels.</param>
    /// <returns>A resolution label such as "8K", "4K", "1080p", "720p", "480p", or "Unknown".</returns>
    internal static string ClassifyResolution(int? width, int? height)
    {
        if (!width.HasValue || !height.HasValue || width.Value <= 0 || height.Value <= 0)
        {
            return "Unknown";
        }

        var w = width.Value;
        var h = height.Value;

        // Classify by the shorter (vertical for landscape, horizontal for portrait) dimension,
        // matching the industry convention where resolution labels (1080p, 4K, etc.) refer to
        // the vertical pixel count for standard aspect ratios.
        // NOTE: ultra-wide frames (e.g. 3840x1080) will be classified as 1080p because their
        // short dimension is 1080. This matches the vertical-line naming convention.
        var shortDimension = Math.Min(w, h);

        return shortDimension switch
        {
            >= 4320 => "8K", // 7680×4320 or higher (any orientation)
            >= 2160 => "4K", // 3840×2160 (any orientation)
            >= 1080 => "1080p", // any 1080-line source, including anamorphic/panoramic variants
            >= 720 => "720p", // 720p even with narrow width
            >= 576 => "576p",
            >= 480 => "480p",
            > 0 => "SD",
            _ => "Unknown"
        };
    }

    /// <summary>
    ///     Classifies the dynamic range of a video stream into a display label.
    /// </summary>
    /// <param name="videoStream">The video MediaStream to analyze.</param>
    /// <returns>A label such as "Dolby Vision", "HDR10+", "HDR10", "HLG", "SDR", or "Unknown".</returns>
    internal static string ClassifyDynamicRange(MediaStream? videoStream)
    {
        if (videoStream is null)
        {
            return "Unknown";
        }

        return ClassifyDynamicRange(videoStream.VideoRangeType, videoStream.VideoRange);
    }

    /// <summary>
    ///     Classifies the dynamic range from the given enum values into a display label.
    /// </summary>
    /// <param name="rangeType">The <see cref="VideoRangeType"/> value.</param>
    /// <param name="range">The <see cref="VideoRange"/> fallback value.</param>
    /// <returns>A label such as "Dolby Vision", "HDR10+", "HDR10", "HLG", "SDR", or "Unknown".</returns>
    internal static string ClassifyDynamicRange(VideoRangeType rangeType, VideoRange range)
    {
        // Use the strongly-typed VideoRangeType enum
        switch (rangeType)
        {
            case VideoRangeType.DOVI:
            case VideoRangeType.DOVIWithHDR10:
            case VideoRangeType.DOVIWithHDR10Plus:
            case VideoRangeType.DOVIWithHLG:
            case VideoRangeType.DOVIWithSDR:
                return "Dolby Vision";

            case VideoRangeType.HDR10Plus:
                return "HDR10+";

            case VideoRangeType.HDR10:
                return "HDR10";

            case VideoRangeType.HLG:
                return "HLG";

            case VideoRangeType.SDR:
                return "SDR";
        }

        // Fallback to VideoRange if VideoRangeType is unknown/default
        switch (range)
        {
            case VideoRange.HDR:
                return "HDR";

            case VideoRange.SDR:
                return "SDR";
        }

        return "Unknown";
    }

    /// <summary>
    ///     Classifies an audio codec and profile from MediaStream metadata into a detailed
    ///     display label that distinguishes variants like TrueHD Atmos, DTS-HD MA, etc.
    /// </summary>
    /// <param name="codec">The audio codec string (e.g. "truehd", "eac3", "dts", "aac").</param>
    /// <param name="profile">The audio profile string (e.g. "DTS-HD MA", "LC", "HE-AAC").</param>
    /// <returns>A detailed display label such as "TrueHD Atmos", "DTS-HD MA", "AAC", or "Unknown".</returns>
    internal static string ClassifyAudioCodec(string? codec, string? profile)
    {
        if (string.IsNullOrEmpty(codec))
        {
            return "Unknown";
        }

        var upperCodec = codec.ToUpperInvariant();
        var upperProfile = profile?.ToUpperInvariant() ?? string.Empty;

        return upperCodec switch
        {
            "TRUEHD" => upperProfile.Contains("ATMOS", StringComparison.Ordinal)
                ? "TrueHD Atmos"
                : "TrueHD",

            "EAC3" or "E-AC-3" => upperProfile.Contains("ATMOS", StringComparison.Ordinal) ||
                                  upperProfile.Contains("JOC", StringComparison.Ordinal)
                ? "EAC3 Atmos"
                : "EAC3",

            "AC3" or "A_AC3" => "AC3",

            "DTS" => ClassifyDtsProfile(upperProfile),

            "AAC" or "MP4A" => ClassifyAacProfile(upperProfile),

            "FLAC" => "FLAC",
            "MP3" => "MP3",
            "MP2" => "MP2",
            "OPUS" => "Opus",
            "VORBIS" => "Vorbis",
            "PCM_S16LE" or "PCM_S24LE" or "PCM_S32LE" or "PCM_F32LE" or "PCM" or "LPCM" => "PCM",
            "ALAC" => "ALAC",
            "WMAV2" or "WMAPRO" or "WMA" => "WMA",
            "WAV" => "WAV",
            _ => upperCodec
        };
    }

    /// <summary>
    ///     Classifies a DTS audio profile into a specific variant label.
    /// </summary>
    /// <param name="upperProfile">The uppercased profile string.</param>
    /// <returns>A label such as "DTS-HD MA", "DTS:X", "DTS-HD HRA", or "DTS".</returns>
    private static string ClassifyDtsProfile(string upperProfile)
    {
        if (upperProfile.Contains("DTS:X", StringComparison.Ordinal) ||
            upperProfile.Contains("DTS-X", StringComparison.Ordinal))
        {
            return "DTS:X";
        }

        if (upperProfile.Contains("DTS-HD MA", StringComparison.Ordinal) ||
            string.Equals(upperProfile, "MA", StringComparison.Ordinal))
        {
            return "DTS-HD MA";
        }

        if (upperProfile.Contains("DTS-HD HRA", StringComparison.Ordinal) ||
            string.Equals(upperProfile, "HRA", StringComparison.Ordinal))
        {
            return "DTS-HD HRA";
        }

        if (upperProfile.Contains("DTS-ES", StringComparison.Ordinal) ||
            string.Equals(upperProfile, "ES", StringComparison.Ordinal))
        {
            return "DTS-ES";
        }

        return "DTS";
    }

    /// <summary>
    ///     Classifies an AAC audio profile into a specific variant label.
    /// </summary>
    /// <param name="upperProfile">The uppercased profile string.</param>
    /// <returns>A label such as "AAC-LC", "HE-AAC", or "AAC" (generic fallback).</returns>
    private static string ClassifyAacProfile(string upperProfile)
    {
        if (upperProfile.Contains("HE-AAC", StringComparison.Ordinal) ||
            upperProfile.Contains("HE_AAC", StringComparison.Ordinal) ||
            upperProfile.Contains("HE AAC", StringComparison.Ordinal))
        {
            return "HE-AAC";
        }

        if (string.Equals(upperProfile, "LC", StringComparison.Ordinal) ||
            upperProfile.Contains("AAC-LC", StringComparison.Ordinal) ||
            upperProfile.Contains("AAC LC", StringComparison.Ordinal))
        {
            return "AAC-LC";
        }

        return "AAC";
    }

    /// <summary>
    ///     Groups the trash-resolution and recursion context threaded through
    ///     <see cref="ScanSubdirectories" /> and the recursive analysis, keeping the parameter
    ///     count within bounds without changing behaviour.
    /// </summary>
    /// <param name="ResolvedTrashFolderName">The resolved trash folder name (empty when unset).</param>
    /// <param name="LibraryRoot">The library root path (used for trash folder resolution).</param>
    /// <param name="SkipHealthChecks">When true, skip health check counters.</param>
    /// <param name="TrashFolderName">Pre-resolved trash folder name, threaded through recursion.</param>
    /// <param name="ResolvedFullTrashPath">The normalized absolute trash path for this library root.</param>
    private readonly record struct SubdirectoryScanContext(
        string ResolvedTrashFolderName,
        string? LibraryRoot,
        bool SkipHealthChecks,
        string? TrashFolderName,
        string? ResolvedFullTrashPath);

    /// <summary>
    ///     Mutable per-directory presence flags accumulated while classifying the files of a
    ///     single directory. Grouped into one object so <see cref="ClassifyFile" /> can update
    ///     them without an unwieldy list of <c>ref bool</c> parameters.
    /// </summary>
    private sealed class DirectoryFileFlags
    {
        /// <summary>Gets or sets a value indicating whether the directory contains a video file.</summary>
        public bool HasVideo { get; set; }

        /// <summary>Gets or sets a value indicating whether the directory contains a subtitle file.</summary>
        public bool HasSubs { get; set; }

        /// <summary>Gets or sets a value indicating whether the directory contains an image file.</summary>
        public bool HasImage { get; set; }

        /// <summary>Gets or sets a value indicating whether the directory contains an NFO file.</summary>
        public bool HasNfo { get; set; }

        /// <summary>Gets or sets a value indicating whether the directory contains any non-audio (non-trickplay) file.</summary>
        public bool HasAnyNonTrickplayFile { get; set; }

        /// <summary>Gets or sets a value indicating whether the directory (or a recursed subdirectory) contains a video.</summary>
        public bool ContainsVideo { get; set; }
    }
}