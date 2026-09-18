using System;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;

/// <summary>
///     Default production implementation of IPluginConfigurationService that delegates to the Instance singleton.
/// </summary>
public class PluginConfigurationService : IPluginConfigurationService
{
    private readonly IPluginAccessor _accessor;

    // Guards the read-mutate-save triple in ReadAndMutate so concurrent callers
    // cannot interleave their own mutations on the shared PluginConfiguration object.
    // Static so the guard holds process-wide even if more than one service instance
    // ever exists (a second DI container, a manually constructed service, or parallel
    // test hosts): concurrent saves serialize to Jellyfin's FileShare.None config
    // file instead of colliding with a sharing-violation IOException that surfaces
    // as a 500 on otherwise valid concurrent writes.
    private static readonly Lock MutateLock = new();

    // Bounded retries for the config-file save inside ReadAndMutate. The save opens
    // the file with FileShare.None, so any out-of-band writer (Jellyfin's own admin
    // save, a second host, a scanner holding the file) can transiently collide.
    private static readonly TimeSpan[] SaveRetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)];

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationService"/> class
    /// wired to the real <see cref="Plugin.Instance"/> singleton (production path).
    /// </summary>
    public PluginConfigurationService()
        : this(new DefaultPluginAccessor())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationService"/> class
    /// with an injected accessor (testing seam). Allows a test host to pin both the
    /// "plugin present" and "plugin absent" branches without racing against
    /// process-wide state managed by other tests. Marked <c>internal</c> so it is not
    /// part of the public API surface - only
    /// <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>-linked
    /// assemblies (i.e. the test project) can invoke it.
    /// </summary>
    /// <param name="accessor">
    /// The plugin accessor abstraction that reports whether the plugin singleton exists
    /// and exposes its configuration + version. Must not be <c>null</c>.
    /// </param>
    internal PluginConfigurationService(IPluginAccessor accessor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    /// <summary>
    ///     Minimal abstraction over the Instance singleton, exposing only the shape the service consumes.
    /// </summary>
    internal interface IPluginAccessor
    {
        /// <summary>Gets a value indicating whether the plugin singleton has been created.</summary>
        bool IsInitialized { get; }

        /// <summary>Gets the plugin's version string, or <c>null</c> when uninitialised.</summary>
        string? Version { get; }

        /// <summary>Gets the plugin configuration, or <c>null</c> when uninitialised.</summary>
        PluginConfiguration? Configuration { get; }

        /// <summary>Persists the current configuration to disk. No-op when uninitialised.</summary>
        void SaveConfiguration();
    }

    /// <inheritdoc />
    public bool IsInitialized => _accessor.IsInitialized;

    /// <inheritdoc />
    public string PluginVersion => _accessor.Version ?? "unknown";

    /// <inheritdoc />
    /// <remarks>
    ///     <strong>Initialization guard </strong> Throws InvalidOperationException when the plugin singleton has not yet been created.
    /// </remarks>
    public PluginConfiguration GetConfiguration()
    {
        if (!_accessor.IsInitialized)
        {
            throw new InvalidOperationException("Plugin configuration is not yet available. Check IsInitialized before calling GetConfiguration.");
        }

        // _accessor.Configuration is non-null whenever IsInitialized is true (both properties read Plugin.Instance, which is either null or fully constructed).
        return _accessor.Configuration!;
    }

    /// <inheritdoc />
    public void SaveConfiguration()
    {
        _accessor.SaveConfiguration();
    }

    /// <inheritdoc />
    public void ReadAndMutate(Action<PluginConfiguration> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (MutateLock)
        {
            var config = _accessor.Configuration;
            if (config == null)
            {
                // Plugin not initialised - nothing to mutate or save.
                return;
            }

            // Run the mutation exactly once; only the persistence below retries.
            // Re-running mutate could double-apply non-idempotent edits such as the
            // cleanup-totals increments in CleanupTrackingService.
            mutate(config);
            SaveWithRetry();
        }
    }

    /// <summary>
    ///     Persists the configuration and retries transient file lock collisions.
    /// </summary>
    private void SaveWithRetry()
    {
        for (var attempt = 0; attempt <= SaveRetryDelays.Length; attempt++)
        {
            try
            {
                _accessor.SaveConfiguration();
                return;
            }
            catch (IOException) when (attempt < SaveRetryDelays.Length)
            {
                Thread.Sleep(SaveRetryDelays[attempt]);
            }
        }
    }

    /// <summary>
    /// Default accessor that reads directly from <see cref="Plugin.Instance"/>. This
    /// preserves the exact production behaviour of the previous implementation.
    /// </summary>
    private sealed class DefaultPluginAccessor : IPluginAccessor
    {
        public bool IsInitialized => Plugin.Instance is not null;

        public string? Version => Plugin.Instance?.Version?.ToString();

        public PluginConfiguration? Configuration => Plugin.Instance?.Configuration;

        public void SaveConfiguration() => Plugin.Instance?.SaveConfiguration();
    }
}
