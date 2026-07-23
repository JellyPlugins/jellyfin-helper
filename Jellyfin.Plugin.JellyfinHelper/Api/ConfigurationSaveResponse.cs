using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for PUT /JellyfinHelper/Configuration.</summary>
public sealed class ConfigurationSaveResponse
{
    /// <summary>Gets or sets the confirmation message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets any non-fatal warnings (e.g. unreachable Arr instances).</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
