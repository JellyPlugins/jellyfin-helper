using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

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
    ///     Gets the list of book (eBook) library statistics. Populated only when a Book library exists, so the UI can render a Books section conditionally (mirroring the Music behaviour).
    /// </summary>
    public Collection<LibraryStatistics> Books { get; } = new();

    /// <summary>
    /// Gets the list of other library statistics.
    /// </summary>
    public Collection<LibraryStatistics> Other { get; } = new();

    /// <summary>
    /// Gets or sets the UTC timestamp when this scan was performed.
    /// </summary>
    public DateTime ScanTimestamp { get; set; } = DateTime.MinValue;

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

    /// <summary>
    /// Gets the total size of eBook files across all libraries in bytes.
    /// </summary>
    public long TotalBookSize => Libraries.Sum(l => l.BookSize);

    /// <summary>
    /// Gets the total eBook file count across all libraries.
    /// </summary>
    public int TotalBookFileCount => Libraries.Sum(l => l.BookFileCount);

    /// <summary>
    ///     Gets the eBook format breakdown (format label -> count) aggregated across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalBookFormats => AggregateDictionaries(Libraries.Select(l => l.BookFormats));

    /// <summary>
    ///     Gets all video libraries (Movies + TV Shows + Other, excluding Music). Used for aggregations that only apply to video content.
    /// </summary>
    private IEnumerable<LibraryStatistics> VideoLibraries =>
        Movies.Concat(TvShows).Concat(Other);

    /// <summary>
    /// Gets the aggregated container format breakdown across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalContainerFormats => AggregateDictionaries(Libraries.Select(l => l.ContainerFormats));

    /// <summary>
    /// Gets the aggregated resolution breakdown across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, int> TotalResolutions => AggregateDictionaries(VideoLibraries.Select(l => l.Resolutions));

    /// <summary>
    /// Gets the aggregated video codec breakdown across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, int> TotalVideoCodecs => AggregateDictionaries(VideoLibraries.Select(l => l.VideoCodecs));

    /// <summary>
    /// Gets the aggregated video audio codec breakdown across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, int> TotalVideoAudioCodecs => AggregateDictionaries(VideoLibraries.Select(l => l.VideoAudioCodecs));

    /// <summary>
    /// Gets the aggregated music audio codec breakdown across all libraries.
    /// </summary>
    public Dictionary<string, int> TotalMusicAudioCodecs => AggregateDictionaries(Music.Select(l => l.MusicAudioCodecs));

    /// <summary>
    /// Gets the aggregated container sizes across all libraries.
    /// </summary>
    public Dictionary<string, long> TotalContainerSizes => AggregateLongDictionaries(Libraries.Select(l => l.ContainerSizes));

    /// <summary>
    /// Gets the aggregated resolution sizes across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, long> TotalResolutionSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.ResolutionSizes));

    /// <summary>
    /// Gets the aggregated video codec sizes across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, long> TotalVideoCodecSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.VideoCodecSizes));

    /// <summary>
    /// Gets the aggregated video audio codec sizes across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, long> TotalVideoAudioCodecSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.VideoAudioCodecSizes));

    /// <summary>
    /// Gets the aggregated music audio codec sizes across all libraries.
    /// </summary>
    public Dictionary<string, long> TotalMusicAudioCodecSizes => AggregateLongDictionaries(Music.Select(l => l.MusicAudioCodecSizes));

    /// <summary>
    /// Gets the aggregated dynamic range breakdown across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, int> TotalDynamicRanges => AggregateDictionaries(VideoLibraries.Select(l => l.DynamicRanges));

    /// <summary>
    /// Gets the aggregated dynamic range sizes across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, long> TotalDynamicRangeSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.DynamicRangeSizes));

    /// <summary>
    /// Gets the aggregated video bitrate tier breakdown across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, int> TotalVideoBitrateTiers => AggregateDictionaries(VideoLibraries.Select(l => l.VideoBitrateTiers));

    /// <summary>
    /// Gets the aggregated video bitrate tier sizes across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, long> TotalVideoBitrateTierSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.VideoBitrateTierSizes));

    /// <summary>
    /// Gets the aggregated audio language breakdown across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, int> TotalAudioLanguages => AggregateDictionaries(VideoLibraries.Select(l => l.AudioLanguages));

    /// <summary>
    /// Gets the aggregated audio language sizes across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, long> TotalAudioLanguageSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.AudioLanguageSizes));

    /// <summary>
    /// Gets the aggregated subtitle language breakdown across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, int> TotalSubtitleLanguages => AggregateDictionaries(VideoLibraries.Select(l => l.SubtitleLanguages));

    /// <summary>
    /// Gets the aggregated subtitle language sizes across video libraries only (Movies + TV Shows + Other).
    /// </summary>
    public Dictionary<string, long> TotalSubtitleLanguageSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.SubtitleLanguageSizes));

    /// <summary>
    /// Gets the aggregated watched tier breakdown across video libraries only (Watched / Never watched). Watched status is only ever populated for Movies/TV Shows/Other; aggregating over VideoLibraries (rather than all Libraries) keeps this consistent with the sibling AudioLanguages/SubtitleLanguages aggregates above and guards against silent double-counting if a future release starts tracking watched status for other library types.
    /// </summary>
    public Dictionary<string, int> TotalWatchedTiers => AggregateDictionaries(VideoLibraries.Select(l => l.WatchedTiers));

    /// <summary>
    /// Gets the aggregated watched tier sizes across video libraries only.
    /// </summary>
    public Dictionary<string, long> TotalWatchedTierSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.WatchedTierSizes));

    /// <summary>
    /// Gets the aggregated per-user watched counts across video libraries only (username -> files watched).
    /// Watched status is only ever populated for Movies/TV Shows/Other, matching TotalWatchedTiers above.
    /// </summary>
    public Dictionary<string, int> TotalWatchedByUser => AggregateDictionaries(VideoLibraries.Select(l => l.WatchedByUserPaths.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count, StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// Gets the aggregated per-user watched sizes across video libraries only (username -> total bytes watched).
    /// </summary>
    public Dictionary<string, long> TotalWatchedByUserSizes => AggregateLongDictionaries(VideoLibraries.Select(l => l.WatchedByUserSizes));

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

    /// <summary>
    /// Gets the set of root paths for all movie libraries.
    /// </summary>
    [JsonInclude]
    public HashSet<string> MovieRootPaths => AggregateRootPaths(Movies);

    /// <summary>
    /// Gets the set of root paths for all TV show libraries.
    /// </summary>
    [JsonInclude]
    public HashSet<string> TvShowRootPaths => AggregateRootPaths(TvShows);

    /// <summary>
    /// Gets the set of root paths for all music libraries.
    /// </summary>
    [JsonInclude]
    public HashSet<string> MusicRootPaths => AggregateRootPaths(Music);

    /// <summary>
    /// Gets the set of root paths for all book libraries.
    /// </summary>
    [JsonInclude]
    public HashSet<string> BookRootPaths => AggregateRootPaths(Books);

    /// <summary>
    /// Gets the set of root paths for all other libraries.
    /// </summary>
    [JsonInclude]
    public HashSet<string> OtherRootPaths => AggregateRootPaths(Other);

    private static HashSet<string> AggregateRootPaths(IEnumerable<LibraryStatistics> libraries)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            foreach (var path in library.RootPaths)
            {
                result.Add(path);
            }
        }

        return result;
    }

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
