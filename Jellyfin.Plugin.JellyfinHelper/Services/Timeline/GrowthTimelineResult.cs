using System;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
/// The complete growth timeline result containing data points and metadata.
/// </summary>
public class GrowthTimelineResult
{
    /// <summary>
    /// Gets or sets the granularity used for bucketing (daily, weekly, monthly, quarterly, yearly).
    /// </summary>
    [JsonPropertyName("granularity")]
    public string Granularity { get; set; } = "monthly";

    /// <summary>
    /// Gets or sets the earliest file creation date found across all libraries.
    /// </summary>
    [JsonPropertyName("earliestFileDate")]
    public DateTime EarliestFileDate { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this timeline was computed.
    /// </summary>
    [JsonPropertyName("computedAt")]
    public DateTime ComputedAt { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp of the first scan that established the baseline. Directories created before this timestamp have their full size assigned to their creation date (historical reconstruction).
    /// </summary>
    [JsonPropertyName("firstScanTimestamp")]
    public DateTime? FirstScanTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the total number of media directories scanned.
    /// </summary>
    [JsonPropertyName("totalDirectoriesScanned")]
    public int TotalDirectoriesScanned { get; set; }

    /// <summary>
    ///     Gets the cumulative growth data points. The Populate handling ensures JSON deserialization adds items to this collection rather than trying to replace it (which would fail with a read-only property).
    /// </summary>
    [JsonPropertyName("dataPoints")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<GrowthTimelinePoint> DataPoints { get; } = new();
}
