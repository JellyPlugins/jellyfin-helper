using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;

/// <summary>
///     Re-runs the Discovery sidebar injection at server startup, after dependency injection is built and Jellyfin's web root is mounted.
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
        // Plugin.Instance is set in the plugin constructor, which always runs before hosted services start, so it is expected to be non-null here.
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
