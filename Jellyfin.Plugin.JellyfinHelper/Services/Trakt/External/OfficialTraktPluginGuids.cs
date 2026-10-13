using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;

/// <summary>
///     Identifiers of the official Jellyfin Trakt plugin that the Helper reads from. Kept beside
///     <see cref="OfficialTraktPluginReader"/> so every piece of foreign-plugin coupling lives in one place.
/// </summary>
public static class OfficialTraktPluginGuids
{
    /// <summary>
    ///     The file name the official plugin's configuration is persisted to under Jellyfin's plugin
    ///     configurations directory. Jellyfin derives it from the plugin's configuration type name (<c>Trakt</c>).
    /// </summary>
    public const string ConfigFileName = "Trakt.xml";

    /// <summary>
    ///     The official Trakt plugin's stable plugin id (from its <c>Plugin.Id</c>). Used only to detect that the
    ///     plugin is installed and active.
    /// </summary>
    public static readonly Guid PluginId = new("4fe3201e-d6ae-4f2e-8917-e12bda571281");
}
