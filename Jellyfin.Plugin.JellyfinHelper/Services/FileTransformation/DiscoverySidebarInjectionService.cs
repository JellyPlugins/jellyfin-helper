using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;

/// <summary>
///     Re-runs the Discovery sidebar injection at server startup, after dependency injection is
///     built and Jellyfin's web root is mounted.
///     <para>
///         <see cref="Plugin"/>'s constructor already calls <see cref="Plugin.InjectScript"/>, but
///         the constructor runs very early during plugin discovery — before the File Transformation
///         plugin is guaranteed to be loaded and before the web assets are reliably in place. This
///         hosted service runs the same injection again at a robust point in the startup sequence,
///         which also self-heals the disk-write fallback after a Jellyfin web update overwrites
///         <c>index.html</c> (the injected tag returns on the next server start).
///     </para>
///     <para>
///         The injection is idempotent — <see cref="Services.FileTransformation.DiscoveryScriptTag.RemovalRegex"/>
///         strips any prior tag before re-inserting, and <see cref="Plugin.UpdateIndexHtml"/> skips
///         the write when the file already matches — so running it from both the constructor and
///         here never double-injects or churns the file.
///     </para>
/// </summary>
public sealed class DiscoverySidebarInjectionService : IHostedService
{
    private readonly ILogger<DiscoverySidebarInjectionService> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoverySidebarInjectionService"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public DiscoverySidebarInjectionService(ILogger<DiscoverySidebarInjectionService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Plugin.Instance is set in the plugin constructor, which always runs before hosted
        // services start, so it is expected to be non-null here. Guard anyway — a null instance
        // simply means there is nothing to inject, which is a no-op rather than an error.
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Discovery Sidebar] Startup injection skipped: plugin instance not available");
            }

            return Task.CompletedTask;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[Discovery Sidebar] Running startup injection (post-DI, web root mounted)");
        }

        plugin.InjectScript();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
