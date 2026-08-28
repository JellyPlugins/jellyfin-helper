using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
///     A single directory entry in the growth baseline, recording its state at the time
///     of the first scan.
/// </summary>
public class BaselineDirectoryEntry
{
    /// <summary>
    ///     Gets the directory creation date (UTC).
    /// </summary>
    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; init; }

    /// <summary>
    ///     Gets or sets the total size in bytes at the time of the baseline scan.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    ///     Gets or sets the number of directories/files in this group. Used for grouped baselines where multiple items are aggregated by library and first letter.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }
}