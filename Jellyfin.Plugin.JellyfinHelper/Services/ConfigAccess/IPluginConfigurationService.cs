using System;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;

/// <summary>
/// Abstracts access to the plugin's runtime configuration.
/// Consumers MUST use this service instead of accessing <see cref="Plugin.Instance"/> directly
/// so that configuration reads/writes are testable without a real plugin singleton.
/// </summary>
public interface IPluginConfigurationService
{
    /// <summary>Gets a value indicating whether the plugin singleton is initialized.</summary>
    bool IsInitialized { get; }

    /// <summary>Gets the plugin version string, or "unknown" when the plugin is not available.</summary>
    string PluginVersion { get; }

    /// <summary>
    /// Gets the current plugin configuration.
    /// Returns a default <see cref="PluginConfiguration"/> when the plugin is not initialized.
    /// </summary>
    /// <returns>The current plugin configuration instance.</returns>
    PluginConfiguration GetConfiguration();

    /// <summary>
    /// Persists the current in-memory configuration to disk without replacing the object reference.
    /// Use this after mutating individual properties on the existing configuration.
    /// </summary>
    void SaveConfiguration();

    /// <summary>
    /// Atomically reads the current configuration, applies <paramref name="mutate"/>, and
    /// saves it — all under a write lock that prevents concurrent callers from interleaving
    /// their own mutations.  Callers must not cache the <see cref="PluginConfiguration"/>
    /// reference passed to the delegate beyond the delegate's lifetime.
    /// </summary>
    /// <param name="mutate">Action that receives the live config object and applies changes to it.</param>
    void ReadAndMutate(Action<PluginConfiguration> mutate);
}