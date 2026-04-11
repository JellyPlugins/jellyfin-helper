using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Service that calculates media file statistics per library type.
/// </summary>
public partial class MediaStatisticsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MediaStatisticsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStatisticsService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public MediaStatisticsService(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<MediaStatisticsService> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Calculates statistics for all configured libraries.
    /// </summary>
    /// <returns>The aggregated media statistics.</returns>
    public MediaStatisticsResult CalculateStatistics()
    {
        var result = new MediaStatisticsResult();

        var virtualFolders = _libraryManager.GetVirtualFolders();

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
                _logger.LogDebug("Scanning library location: {Location} (type: {Type})", location, collectionType);
                AnalyzeDirectoryRecursive(location, libraryStats, skipHealthChecks: skipHealth);
            }

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

        return result;
    }

    /// <summary>
    /// Recursively analyzes a directory and accumulates file size statistics.
    /// </summary>
    /// <param name="directoryPath">The directory to analyze.</param>
    /// <param name="stats">The statistics accumulator.</param>
    /// <param name="skipHealthChecks">When true, skip health check counters (e.g. for boxset/collection libraries).</param>
    private void AnalyzeDirectoryRecursive(string directoryPath, LibraryStatistics stats, bool skipHealthChecks = false)
    {
        try
        {
            var files = _fileSystem.GetFiles(directoryPath, false).ToList();

            bool hasVideo = false;
            bool hasSubs = false;
            bool hasImage = false;
            bool hasNfo = false;
            bool hasAnyNonTrickplayFile = false;

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file.FullName);
                var size = file.Length;
                hasAnyNonTrickplayFile = true;

                if (MediaExtensions.VideoExtensions.Contains(ext))
                {
                    stats.VideoSize += size;
                    stats.VideoFileCount++;
                    hasVideo = true;

                    // Container format tracking
                    var container = ext.TrimStart('.').ToUpperInvariant();
                    FileSystemHelper.IncrementCount(stats.ContainerFormats, container);
                    FileSystemHelper.AccumulateValue(stats.ContainerSizes, container, size);

                    // Resolution parsing from filename
                    var resolution = ParseResolution(file.Name);
                    FileSystemHelper.IncrementCount(stats.Resolutions, resolution);
                    FileSystemHelper.AccumulateValue(stats.ResolutionSizes, resolution, size);

                    // Video codec parsing from filename
                    var codec = ParseVideoCodec(file.Name);
                    FileSystemHelper.IncrementCount(stats.VideoCodecs, codec);
                    FileSystemHelper.AccumulateValue(stats.VideoCodecSizes, codec, size);

                    // Audio codec parsing from video filename (e.g. "Movie.DTS.mkv")
                    var audioCodec = ParseAudioCodec(file.Name, ext);
                    if (!string.Equals(audioCodec, "Unknown", StringComparison.Ordinal))
                    {
                        FileSystemHelper.IncrementCount(stats.VideoAudioCodecs, audioCodec);
                        FileSystemHelper.AccumulateValue(stats.VideoAudioCodecSizes, audioCodec, size);
                    }
                }
                else if (MediaExtensions.SubtitleExtensions.Contains(ext))
                {
                    stats.SubtitleSize += size;
                    stats.SubtitleFileCount++;
                    hasSubs = true;
                }
                else if (MediaExtensions.ImageExtensions.Contains(ext))
                {
                    stats.ImageSize += size;
                    stats.ImageFileCount++;
                    hasImage = true;
                }
                else if (MediaExtensions.NfoExtensions.Contains(ext))
                {
                    stats.NfoSize += size;
                    stats.NfoFileCount++;
                    hasNfo = true;
                }
                else if (MediaExtensions.AudioExtensions.Contains(ext))
                {
                    stats.AudioSize += size;
                    stats.AudioFileCount++;

                    // Audio codec parsing from filename and extension
                    var audioCodec = ParseAudioCodec(file.Name, ext);
                    FileSystemHelper.IncrementCount(stats.MusicAudioCodecs, audioCodec);
                    FileSystemHelper.AccumulateValue(stats.MusicAudioCodecSizes, audioCodec, size);
                }
                else
                {
                    stats.OtherSize += size;
                    stats.OtherFileCount++;
                }
            }

            // Health checks — per-directory analysis
            // Boxset/collection libraries are excluded: they are Jellyfin-internal virtual folders
            // that group related movies and typically only contain posters/images, not real media.
            if (!skipHealthChecks)
            {
                if (hasVideo)
                {
                    var videoFiles = files
                        .Where(f => MediaExtensions.VideoExtensions.Contains(Path.GetExtension(f.FullName)))
                        .ToList();
                    int videoCount = videoFiles.Count;

                    if (!hasSubs)
                    {
                        stats.VideosWithoutSubtitles += videoCount;
                        foreach (var vf2 in videoFiles)
                        {
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
                else if (hasAnyNonTrickplayFile && (hasSubs || hasImage || hasNfo))
                {
                    stats.OrphanedMetadataDirectories++;
                    stats.OrphanedMetadataDirectoriesPaths.Add(directoryPath);
                }
            }

            // Recurse into subdirectories
            var subDirs = _fileSystem.GetDirectories(directoryPath, false);
            foreach (var subDir in subDirs)
            {
                if (subDir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    var trickplaySize = FileSystemHelper.CalculateDirectorySize(_fileSystem, subDir.FullName, _logger);
                    stats.TrickplaySize += trickplaySize;
                    stats.TrickplayFolderCount++;
                }
                else
                {
                    AnalyzeDirectoryRecursive(subDir.FullName, stats, skipHealthChecks);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not access directory {Path}", directoryPath);
        }
    }

    /// <summary>
    /// Parses a resolution tier from a video filename.
    /// </summary>
    /// <param name="fileName">The video filename.</param>
    /// <returns>A resolution label such as "4K", "1080p", "720p", "480p", or "Unknown".</returns>
    internal static string ParseResolution(string fileName)
    {
        if (ResolutionRegex4K().IsMatch(fileName))
        {
            return "4K";
        }

        if (ResolutionRegex1080().IsMatch(fileName))
        {
            return "1080p";
        }

        if (ResolutionRegex720().IsMatch(fileName))
        {
            return "720p";
        }

        if (ResolutionRegex480().IsMatch(fileName))
        {
            return "480p";
        }

        if (ResolutionRegex576().IsMatch(fileName))
        {
            return "576p";
        }

        return "Unknown";
    }

    /// <summary>
    /// Parses a video codec from a video filename.
    /// </summary>
    /// <param name="fileName">The video filename.</param>
    /// <returns>A codec label such as "HEVC", "H.264", "AV1", "VP9", "MPEG", or "Unknown".</returns>
    internal static string ParseVideoCodec(string fileName)
    {
        if (CodecRegexHevc().IsMatch(fileName))
        {
            return "HEVC";
        }

        if (CodecRegexH264().IsMatch(fileName))
        {
            return "H.264";
        }

        if (CodecRegexAv1().IsMatch(fileName))
        {
            return "AV1";
        }

        if (CodecRegexVp9().IsMatch(fileName))
        {
            return "VP9";
        }

        if (CodecRegexMpeg().IsMatch(fileName))
        {
            return "MPEG";
        }

        if (CodecRegexXvid().IsMatch(fileName))
        {
            return "XviD";
        }

        if (CodecRegexDivx().IsMatch(fileName))
        {
            return "DivX";
        }

        return "Unknown";
    }

    /// <summary>
    /// Parses an audio codec from a filename and its extension.
    /// </summary>
    /// <param name="fileName">The audio filename.</param>
    /// <param name="extension">The file extension (with leading dot).</param>
    /// <returns>A codec label such as "AAC", "FLAC", "MP3", "Opus", "Vorbis", "WAV", "WMA", or "Unknown".</returns>
    internal static string ParseAudioCodec(string fileName, string extension)
    {
        // First try to detect from filename tags (e.g. "Song.FLAC.mp3" or "[AAC]")
        if (AudioCodecRegexFlac().IsMatch(fileName))
        {
            return "FLAC";
        }

        if (AudioCodecRegexAac().IsMatch(fileName))
        {
            return "AAC";
        }

        if (AudioCodecRegexOpus().IsMatch(fileName))
        {
            return "Opus";
        }

        if (AudioCodecRegexDts().IsMatch(fileName))
        {
            return "DTS";
        }

        if (AudioCodecRegexAc3().IsMatch(fileName))
        {
            return "AC3";
        }

        if (AudioCodecRegexEac3().IsMatch(fileName))
        {
            return "EAC3";
        }

        if (AudioCodecRegexTrueHd().IsMatch(fileName))
        {
            return "TrueHD";
        }

        if (AudioCodecRegexVorbis().IsMatch(fileName))
        {
            return "Vorbis";
        }

        if (AudioCodecRegexPcm().IsMatch(fileName))
        {
            return "PCM";
        }

        if (AudioCodecRegexAlac().IsMatch(fileName))
        {
            return "ALAC";
        }

        // Fall back to extension-based detection via MediaExtensions mapping
        return MediaExtensions.AudioExtensionToCodec.TryGetValue(extension, out var codecFromExt)
            ? codecFromExt
            : "Unknown";
    }

    // Source-generated regex patterns for resolution detection
    [GeneratedRegex(@"(?i)[\.\-_ \[\(](2160p|4k|uhd)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex ResolutionRegex4K();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]1080[pi][\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex ResolutionRegex1080();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]720p[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex ResolutionRegex720();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](480p|sd)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex ResolutionRegex480();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]576p[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex ResolutionRegex576();

    // Source-generated regex patterns for video codec detection
    [GeneratedRegex(@"(?i)[\.\-_ \[\(](hevc|h\.?265|x\.?265)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex CodecRegexHevc();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](h\.?264|x\.?264|avc)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex CodecRegexH264();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]av1[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex CodecRegexAv1();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]vp9[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex CodecRegexVp9();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](mpeg[24]?|mp2v)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex CodecRegexMpeg();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]xvid[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex CodecRegexXvid();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]divx[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex CodecRegexDivx();

    // Source-generated regex patterns for audio codec detection
    [GeneratedRegex(@"(?i)[\.\-_ \[\(]flac[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexFlac();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]aac[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexAac();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]opus[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexOpus();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](dts[\-_ ]?(hd|ma|x)?|dts)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexDts();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](ac[\-_ ]?3|dolby[\-_ ]?digital)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexAc3();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](eac[\-_ ]?3|ddp|dolby[\-_ ]?digital[\-_ ]?plus|atmos)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexEac3();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](truehd|true[\-_ ]?hd)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexTrueHd();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]vorbis[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexVorbis();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(](pcm|lpcm)[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexPcm();

    [GeneratedRegex(@"(?i)[\.\-_ \[\(]alac[\.\-_ \]\)]", RegexOptions.None)]
    private static partial Regex AudioCodecRegexAlac();
}
