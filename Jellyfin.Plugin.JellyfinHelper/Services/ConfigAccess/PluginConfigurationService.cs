using System;
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
    // cannot interleave their mutations on the shared PluginConfiguration object.
    private readonly Lock _mutateLock = new();

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

        lock (_mutateLock)
        {
            var config = _accessor.Configuration;
            if (config == null)
            {
                // Plugin not initialised - nothing to mutate or save.
                return;
            }

            mutate(config);
            _accessor.SaveConfiguration();
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
