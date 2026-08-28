using System;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;

/// <summary>
///     Abstracts access to the plugin's runtime configuration. Consumers MUST use this service instead of accessing Instance directly so that configuration reads/writes are testable without a real plugin singleton.
/// </summary>
public interface IPluginConfigurationService
{
    /// <summary>Gets a value indicating whether the plugin singleton is initialized.</summary>
    bool IsInitialized { get; }

    /// <summary>Gets the plugin version string, or "unknown" when the plugin is not available.</summary>
    string PluginVersion { get; }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    /// <returns>The live shared plugin configuration instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the plugin singleton has not yet been created.
    /// Check <see cref="IsInitialized"/> before calling this method if the caller may run
    /// before the plugin is fully started.
    /// </exception>
    /// <remarks>
    ///     The returned object is the live shared reference. Treat it as read-only; any mutation must go through ReadAndMutate to stay under the write lock.
    /// </remarks>
    PluginConfiguration GetConfiguration();

    /// <summary>
    ///     Persists the current in-memory configuration to disk without replacing the object reference.
    /// </summary>
    void SaveConfiguration();

    /// <summary>
    ///     Atomically reads the current configuration, applies mutate, and saves it - all under a write lock that prevents concurrent callers from interleaving their own mutations.
    /// </summary>
    /// <param name="mutate">Action that receives the live config object and applies changes to it.</param>
    void ReadAndMutate(Action<PluginConfiguration> mutate);
}