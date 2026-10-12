using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request DTO for updating only the Codecs-tab donut card order via
///     PUT /JellyfinHelper/Configuration/CodecCardOrder.
/// </summary>
public class CodecCardOrderUpdateRequest
{
    /// <summary>
    ///     Gets or sets the desired card order (canonical card ids). Null is treated as an empty list.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA1002:DoNotExposeGenericLists",
        Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<string>? CodecCardOrder { get; set; }
}
