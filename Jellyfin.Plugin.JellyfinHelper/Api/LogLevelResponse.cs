namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for PUT /JellyfinHelper/Configuration/LogLevel.</summary>
public sealed class LogLevelResponse
{
    /// <summary>Gets or sets the confirmation message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the active log level after the update.</summary>
    public string PluginLogLevel { get; set; } = string.Empty;
}
