using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for PUT /JellyfinHelper/Configuration/CodecCardOrder.</summary>
public sealed class CodecCardOrderResponse
{
    /// <summary>Gets or sets the confirmation message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the stored, normalized card order after the update.</summary>
    [SuppressMessage(
        "Usage",
        "CA1002:DoNotExposeGenericLists",
        Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<string> CodecCardOrder { get; set; } = [];
}
