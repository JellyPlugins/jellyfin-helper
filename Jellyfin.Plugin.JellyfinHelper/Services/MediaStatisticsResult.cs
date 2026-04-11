using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// The result of a media statistics scan.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class MediaStatisticsResult
{
    /// <summary>
    /// Gets the list of all library statistics.
    /// </summary>
    public Collection<LibraryStatistics> Libraries { get; } = new();

    /// <summary>
    /// Gets the list of movie library statistics.
    /// </summary>
    public Collection<LibraryStatistics> Movies { get; } = new();

    /// <summary>
    /// Gets the list of TV show library statistics.
    /// </summary>
    public Collection<LibraryStatistics> TvShows { get; } = new();

    /// <summary>
    /// Gets the list of music library statistics.
    /// </summary>
    public Collection<LibraryStatistics> Music { get; } = new();

    /// <summary>
    /// Gets the list of other library statistics.
    /// </summary>
    public Collection<LibraryStatistics> Other { get; } = new();

    /// <summary>
    /// Gets or sets the UTC timestamp when this scan was performed.
    /// </summary>
    public DateTime ScanTimestamp { get; set; } = DateTime.UtcNow;

    // === Size Totals ===

    /// <summary>
    /// Gets the total video size across all movie libraries in bytes.
    /// </summary>
    public long TotalMovieVideoSize => Movies.Sum(l => l.VideoSize);

    /// <summary>
    /// Gets the total video size across all TV show libraries in bytes.
    /// </summary>
    public long TotalTvShowVideoSize => TvShows.Sum(l => l.VideoSize);

    /// <summary>
    /// Gets the total audio size across all music libraries in bytes.
    /// </summary>
    public long TotalMusicAudioSize => Music.Sum(l => l.AudioSize);

    /// <summary>
    /// Gets the total trickplay size across all libraries in bytes.
    /// </summary>
    public long TotalTrickplaySize => Libraries.Sum(l => l.TrickplaySize);

    /// <summary>
    /// Gets the total subtitle size across all libraries in bytes.
    /// </summary>
    public long TotalSubtitleSize => Libraries.Sum(l => l.SubtitleSize);

    /// <summary>
    /// Gets the total image size across all libraries in bytes.
    /// </summary>
    public long TotalImageSize => Libraries.Sum(l => l.ImageSize);

    /// <summary>
    /// Gets the total NFO/metadata size across all libraries in bytes.
    /// </summary>
    public long TotalNfoSize => Libraries.Sum(l => l.NfoSize);

    /// <summary>
    /// Gets the total video file count across all libraries.
    /// </summary>
    public int TotalVideoFileCount => Libraries.Sum(l => l.VideoFileCount);

    /// <summary>
    /// Gets the total audio file count across all libraries.
    /// </summary>
    public int TotalAudioFileCount => Libraries.Sum(l => l.AudioFileCount);

    // === Aggregated Codec/Quality ===

    /// <summary>
    /// Gets the aggregated container format breakdown across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalContainerFormats => AggregateDictionaries(Libraries.Select(l => l.ContainerFormats));

    /// <summary>
    /// Gets the aggregated resolution breakdown across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalResolutions => AggregateDictionaries(Libraries.Select(l => l.Resolutions));

    /// <summary>
    /// Gets the aggregated video codec breakdown across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalVideoCodecs => AggregateDictionaries(Libraries.Select(l => l.VideoCodecs));

    /// <summary>
    /// Gets the aggregated video audio codec breakdown across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalVideoAudioCodecs => AggregateDictionaries(Libraries.Select(l => l.VideoAudioCodecs));

    /// <summary>
    /// Gets the aggregated music audio codec breakdown across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalMusicAudioCodecs => AggregateDictionaries(Music.Select(l => l.MusicAudioCodecs));

    /// <summary>
    /// Gets the aggregated container sizes across all libraries.
    /// </summary>
    public Dictionary<string, long> TotalContainerSizes => AggregateLongDictionaries(Libraries.Select(l => l.ContainerSizes));

    /// <summary>
    /// Gets the aggregated resolution sizes across all libraries.
    /// </summary>
    public Dictionary<string, long> TotalResolutionSizes => AggregateLongDictionaries(Libraries.Select(l => l.ResolutionSizes));

    /// <summary>
    /// Gets the aggregated video codec sizes across all libraries.
    /// </summary>
    public Dictionary<string, long> TotalVideoCodecSizes => AggregateLongDictionaries(Libraries.Select(l => l.VideoCodecSizes));

    /// <summary>
    /// Gets the aggregated video audio codec sizes across all libraries.
    /// </summary>
    public Dictionary<string, long> TotalVideoAudioCodecSizes => AggregateLongDictionaries(Libraries.Select(l => l.VideoAudioCodecSizes));

    /// <summary>
    /// Gets the aggregated music audio codec sizes across all libraries.
    /// </summary>
    public Dictionary<string, long> TotalMusicAudioCodecSizes => AggregateLongDictionaries(Music.Select(l => l.MusicAudioCodecSizes));

    // === Aggregated Health Checks ===

    /// <summary>
    /// Gets the total number of video files without subtitles.
    /// </summary>
    public int TotalVideosWithoutSubtitles => Libraries.Sum(l => l.VideosWithoutSubtitles);

    /// <summary>
    /// Gets the total number of video files without poster/images.
    /// </summary>
    public int TotalVideosWithoutImages => Libraries.Sum(l => l.VideosWithoutImages);

    /// <summary>
    /// Gets the total number of video files without NFO metadata.
    /// </summary>
    public int TotalVideosWithoutNfo => Libraries.Sum(l => l.VideosWithoutNfo);

    /// <summary>
    /// Gets the total number of orphaned metadata directories.
    /// </summary>
    public int TotalOrphanedMetadataDirectories => Libraries.Sum(l => l.OrphanedMetadataDirectories);

    // === Aggregated Health Check Detail Paths ===

    /// <summary>
    /// Gets the aggregated list of video file paths that have no subtitle file in the same directory.
    /// </summary>
    public Collection<string> TotalVideosWithoutSubtitlesPaths =>
        new(Libraries.SelectMany(l => l.VideosWithoutSubtitlesPaths).ToList());

    /// <summary>
    /// Gets the aggregated list of video file paths that have no image/poster in the same directory.
    /// </summary>
    public Collection<string> TotalVideosWithoutImagesPaths =>
        new(Libraries.SelectMany(l => l.VideosWithoutImagesPaths).ToList());

    /// <summary>
    /// Gets the aggregated list of video file paths that have no NFO metadata in the same directory.
    /// </summary>
    public Collection<string> TotalVideosWithoutNfoPaths =>
        new(Libraries.SelectMany(l => l.VideosWithoutNfoPaths).ToList());

    /// <summary>
    /// Gets the aggregated list of directory paths that contain only metadata but no video.
    /// </summary>
    public Collection<string> TotalOrphanedMetadataDirectoriesPaths =>
        new(Libraries.SelectMany(l => l.OrphanedMetadataDirectoriesPaths).ToList());

    private static Dictionary<string, int> AggregateDictionaries(IEnumerable<Dictionary<string, int>> dictionaries)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var dict in dictionaries)
        {
            foreach (var kvp in dict)
            {
                if (result.TryGetValue(kvp.Key, out var current))
                {
                    result[kvp.Key] = current + kvp.Value;
                }
                else
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }

        return result;
    }

    private static Dictionary<string, long> AggregateLongDictionaries(IEnumerable<Dictionary<string, long>> dictionaries)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var dict in dictionaries)
        {
            foreach (var kvp in dict)
            {
                if (result.TryGetValue(kvp.Key, out var current))
                {
                    result[kvp.Key] = current + kvp.Value;
                }
                else
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }

        return result;
    }
}