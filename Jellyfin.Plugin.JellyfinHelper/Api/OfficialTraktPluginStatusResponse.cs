namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Reports whether the official Jellyfin Trakt plugin is installed and active, so the config page can offer
///     the "source through the official plugin" mode and relax the own-client-id requirement.
/// </summary>
public sealed class OfficialTraktPluginStatusResponse
{
    /// <summary>
    ///     Gets or sets a value indicating whether the official Trakt plugin is present and active.
    /// </summary>
    public bool Present { get; set; }
}
