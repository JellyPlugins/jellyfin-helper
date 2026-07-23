using System;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;

/// <summary>
/// Default production implementation of <see cref="IPluginConfigurationService"/>
/// that delegates to the <see cref="Plugin.Instance"/> singleton.
/// <para>
///     The dependency on <see cref="Plugin.Instance"/> is expressed through a small
///     internal <see cref="IPluginAccessor"/> seam so tests can pin both branches
///     (present / absent) deterministically instead of relying on process-wide state
///     that other tests may set or clear. The <see cref="Plugin"/> singleton itself
///     cannot be instantiated in a unit test without a full Jellyfin host (its
///     constructor requires <c>IApplicationPaths</c>, <c>IXmlSerializer</c>, and a
///     logger), so this seam exposes ONLY the properties the service actually reads —
///     a much smaller surface that any test double can satisfy.
/// </para>
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
    /// part of the public API surface — only
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
    /// Minimal abstraction over the <see cref="Plugin.Instance"/> singleton, exposing
    /// only the shape the service consumes. Kept internal because callers outside
    /// this project have no legitimate need to swap it out — the DI container always
    /// constructs the service through the parameterless production constructor.
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
    /// <para>
    ///     <strong>Initialization guard (Finding #9):</strong> Throws <see cref="InvalidOperationException"/>
    ///     when the plugin singleton has not yet been created. Callers that may run before the
    ///     plugin is fully started must check <see cref="IsInitialized"/> first.
    /// </para>
    /// <para>
    ///     <strong>Mutation contract (Finding #10):</strong> Returns the live shared
    ///     <see cref="PluginConfiguration"/> reference held by the plugin singleton.
    ///     Callers MUST treat the returned object as read-only. Any mutation MUST go through
    ///     <see cref="ReadAndMutate"/> so that concurrent writes are serialized under the
    ///     write lock and the change is persisted atomically. Mutating the returned reference
    ///     directly bypasses the lock and will not be saved.
    /// </para>
    /// </remarks>
    public PluginConfiguration GetConfiguration()
    {
        if (!_accessor.IsInitialized)
        {
            throw new InvalidOperationException("Plugin configuration is not yet available. Check IsInitialized before calling GetConfiguration.");
        }

        // _accessor.Configuration is non-null whenever IsInitialized is true (both properties
        // read Plugin.Instance, which is either null or fully constructed). The null-forgiving
        // operator documents that assertion rather than silently returning a detached default.
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
                // Plugin not initialised — nothing to mutate or save.
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
