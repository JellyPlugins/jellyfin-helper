using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     Scheduled task that scans for broken link files (.strm and symlinks) and repairs them
///     by searching the parent directory for a renamed media file.
/// </summary>
public class RepairLinksTask
{
    private readonly ICleanupConfigHelper _configHelper;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RepairLinksTask> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly ILinkRepairService _linkRepairService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RepairLinksTask" /> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="linkRepairService">The link repair service.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    public RepairLinksTask(
        ILogger<RepairLinksTask> logger,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILinkRepairService linkRepairService,
        ICleanupConfigHelper configHelper)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _linkRepairService = linkRepairService;
        _configHelper = configHelper;
    }

    /// <summary>
    ///     Executes the link file repair task.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var dryRun = _configHelper.IsDryRunLinkRepair();

        _pluginLog.LogInfo(
            "LinkRepair",
            dryRun ? "Task started (Dry Run). No links will be modified." : "Task started.",
            _logger);
        progress.Report(0);

        var libraryPaths = _configHelper.GetFilteredLibraryLocations(_libraryManager);

        if (libraryPaths.Count == 0)
        {
            _pluginLog.LogWarning("LinkRepair", "No library paths configured for link repair.", logger: _logger);
            progress.Report(100);
            return Task.CompletedTask;
        }

        _pluginLog.LogInfo(
            "LinkRepair",
            $"Scanning {libraryPaths.Count} library paths...",
            _logger);

        progress.Report(10);

        cancellationToken.ThrowIfCancellationRequested();

        var result = _linkRepairService.RepairLinks(libraryPaths, dryRun, cancellationToken);

        progress.Report(90);

        _pluginLog.LogInfo(
            "LinkRepair",
            dryRun
                ? $"Task finished (Dry Run). Valid: {result.ValidCount}, Would repair: {result.RepairedCount}, Broken: {result.BrokenCount}, Ambiguous: {result.AmbiguousCount}, Invalid: {result.InvalidContentCount}"
                : $"Task finished. Valid: {result.ValidCount}, Repaired: {result.RepairedCount}, Broken: {result.BrokenCount}, Ambiguous: {result.AmbiguousCount}, Invalid: {result.InvalidContentCount}",
            _logger);

        progress.Report(100);
        return Task.CompletedTask;
    }
}