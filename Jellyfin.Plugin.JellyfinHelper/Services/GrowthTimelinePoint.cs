using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// A single data point in the library growth timeline, representing cumulative
/// size and file count at a specific point in time.
/// </summary>
public class GrowthTimelinePoint
{
    /// <summary>
    /// Gets or sets the date for this data point (start of the time bucket).
    /// </summary>
    [JsonPropertyName("date")]
    public DateTime Date { get; set; }

    /// <summary>
    /// Gets or sets the cumulative total size in bytes up to and including this time bucket.
    /// </summary>
    [JsonPropertyName("cumulativeSize")]
    public long CumulativeSize { get; set; }

    /// <summary>
    /// Gets or sets the cumulative total file count up to and including this time bucket.
    /// </summary>
    [JsonPropertyName("cumulativeFileCount")]
    public int CumulativeFileCount { get; set; }
}