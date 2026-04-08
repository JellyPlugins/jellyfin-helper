using System.Collections.ObjectModel;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// The result of a media statistics scan.
/// </summary>
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
    /// Gets the total video size across all movie libraries in bytes.
    /// </summary>
    public long TotalMovieVideoSize => Movies.Sum(l => l.VideoSize);

    /// <summary>
    /// Gets the total video size across all TV show libraries in bytes.
    /// </summary>
    public long TotalTvShowVideoSize => TvShows.Sum(l => l.VideoSize);

    /// <summary>
    /// Gets the total video size across all music libraries in bytes.
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
}