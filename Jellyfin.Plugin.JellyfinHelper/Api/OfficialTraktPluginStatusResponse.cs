namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Reports whether the official Jellyfin Trakt plugin is installed and active, so the config page can show
///     whether Trakt can be sourced (the official plugin is the only Trakt source).
/// </summary>
public sealed class OfficialTraktPluginStatusResponse
{
    /// <summary>
    ///     Gets or sets a value indicating whether the official Trakt plugin is present and active.
    /// </summary>
    public bool Present { get; set; }
}
