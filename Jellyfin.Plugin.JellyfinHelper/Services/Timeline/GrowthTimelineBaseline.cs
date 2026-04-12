using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
/// Stores the baseline snapshot taken during the first growth timeline scan.
/// This baseline records the size of each directory at the time of the first scan,
/// enabling accurate diff-based growth tracking for subsequent scans.
/// </summary>
public class GrowthTimelineBaseline
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Gets or sets the timestamp of the first scan that created this baseline.
    /// Directories created after this timestamp are treated as "new additions"
    /// with their full size. Directories that already existed are tracked by their
    /// size diff relative to the baseline.
    /// </summary>
    [JsonPropertyName("firstScanTimestamp")]
    public DateTime FirstScanTimestamp { get; set; }

    /// <summary>
    /// Gets the per-directory baseline entries keyed by the directory's full path.
    /// Each entry records the creation date and size at the time of the first scan.
    /// </summary>
    [JsonPropertyName("directories")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, BaselineDirectoryEntry> Directories { get; } = new(PathComparer);
}