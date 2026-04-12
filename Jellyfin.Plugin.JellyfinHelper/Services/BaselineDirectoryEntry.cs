using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

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
}