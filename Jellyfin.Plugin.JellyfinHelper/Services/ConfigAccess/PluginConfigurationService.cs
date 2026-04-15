using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;

/// <summary>
/// Default production implementation of <see cref="IPluginConfigurationService"/>
/// that delegates to the <see cref="Plugin.Instance"/> singleton.
/// </summary>
public class PluginConfigurationService : IPluginConfigurationService
{
    /// <inheritdoc />
    public bool IsInitialized => Plugin.Instance != null;

    /// <inheritdoc />
    public string PluginVersion => Plugin.Instance?.Version?.ToString() ?? "unknown";

    /// <inheritdoc />
    public PluginConfiguration GetConfiguration()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void SaveConfiguration()
    {
        Plugin.Instance?.SaveConfiguration();
    }
}