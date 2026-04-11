using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// A lightweight snapshot of statistics for historical trend tracking.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class StatisticsSnapshot
{
    /// <summary>
    /// Gets or sets the UTC timestamp of this snapshot.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the total video file count.
    /// </summary>
    public int TotalVideoFileCount { get; set; }

    /// <summary>
    /// Gets or sets the total audio file count.
    /// </summary>
    public int TotalAudioFileCount { get; set; }

    /// <summary>
    /// Gets or sets the total movie video size in bytes.
    /// </summary>
    public long TotalMovieVideoSize { get; set; }

    /// <summary>
    /// Gets or sets the total TV show video size in bytes.
    /// </summary>
    public long TotalTvShowVideoSize { get; set; }

    /// <summary>
    /// Gets or sets the total music audio size in bytes.
    /// </summary>
    public long TotalMusicAudioSize { get; set; }

    /// <summary>
    /// Gets or sets the total trickplay size in bytes.
    /// </summary>
    public long TotalTrickplaySize { get; set; }

    /// <summary>
    /// Gets or sets the total subtitle size in bytes.
    /// </summary>
    public long TotalSubtitleSize { get; set; }

    /// <summary>
    /// Gets or sets the total image size in bytes.
    /// </summary>
    public long TotalImageSize { get; set; }

    /// <summary>
    /// Gets or sets the total NFO/metadata size in bytes.
    /// </summary>
    public long TotalNfoSize { get; set; }

    /// <summary>
    /// Gets or sets the overall total size in bytes (sum of all categories across all libraries).
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Gets the per-library size breakdown.
    /// </summary>
    public Dictionary<string, long> LibrarySizes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a snapshot from a full statistics result.
    /// </summary>
    /// <param name="result">The full statistics result.</param>
    /// <returns>A lightweight snapshot.</returns>
    public static StatisticsSnapshot FromResult(MediaStatisticsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var snapshot = new StatisticsSnapshot
        {
            Timestamp = result.ScanTimestamp,
            TotalVideoFileCount = result.TotalVideoFileCount,
            TotalAudioFileCount = result.TotalAudioFileCount,
            TotalMovieVideoSize = result.TotalMovieVideoSize,
            TotalTvShowVideoSize = result.TotalTvShowVideoSize,
            TotalMusicAudioSize = result.TotalMusicAudioSize,
            TotalTrickplaySize = result.TotalTrickplaySize,
            TotalSubtitleSize = result.TotalSubtitleSize,
            TotalImageSize = result.TotalImageSize,
            TotalNfoSize = result.TotalNfoSize,
        };

        long totalSize = 0;
        foreach (var lib in result.Libraries)
        {
            snapshot.LibrarySizes[lib.LibraryName] = lib.TotalSize;
            totalSize += lib.TotalSize;
        }

        snapshot.TotalSize = totalSize;
        return snapshot;
    }
}