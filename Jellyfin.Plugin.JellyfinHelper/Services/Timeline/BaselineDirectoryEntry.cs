using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
/// A single directory entry in the growth baseline, recording its state at the time
/// of the first scan.
/// </summary>
public class BaselineDirectoryEntry
{
    /// <summary>
    /// Gets or sets the directory creation date (UTC).
    /// </summary>
    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the total size in bytes at the time of the baseline scan.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the number of directories/files in this group.
    /// Used for grouped baselines where multiple items are aggregated
    /// by library and first letter. A value of 0 is treated as 1
    /// for backwards compatibility with legacy per-directory baselines.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}
