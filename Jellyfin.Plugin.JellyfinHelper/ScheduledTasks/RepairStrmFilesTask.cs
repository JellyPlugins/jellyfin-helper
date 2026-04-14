using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Strm;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
/// Scheduled task that scans for broken .strm files and repairs them
/// by searching the parent directory for a renamed media file.
/// </summary>
public class RepairStrmFilesTask
{
    private readonly ILogger<RepairStrmFilesTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginLogService _pluginLog;
    private readonly StrmRepairService _strmRepairService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepairStrmFilesTask"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="fileSystem">The file system abstraction.</param>
    /// <param name="strmRepairServiceLogger">The logger for the strm repair service.</param>
    public RepairStrmFilesTask(
        ILogger<RepairStrmFilesTask> logger,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        IFileSystem fileSystem,
        ILogger<StrmRepairService> strmRepairServiceLogger)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _strmRepairService = new StrmRepairService(fileSystem, pluginLog, strmRepairServiceLogger);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepairStrmFilesTask"/> class.
    /// This constructor is used for testing to inject a mock service.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="strmRepairService">The strm repair service.</param>
    internal RepairStrmFilesTask(
        ILogger<RepairStrmFilesTask> logger,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        StrmRepairService strmRepairService)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _strmRepairService = strmRepairService;
    }

    /// <summary>
    /// Executes the .strm file repair task.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var dryRun = CleanupConfigHelper.IsDryRunStrmRepair();

        _pluginLog.LogInfo("StrmRepair", "Task started.", _logger);
        progress.Report(0);

        var libraryPaths = CleanupConfigHelper.GetFilteredLibraryLocations(_libraryManager);

        if (libraryPaths.Count == 0)
        {
            _pluginLog.LogWarning("StrmRepair", "No library paths configured for .strm repair", logger: _logger);
            progress.Report(100);
            return Task.CompletedTask;
        }

        _pluginLog.LogInfo("StrmRepair", $"Running .strm repair (DryRun: {dryRun}) on {libraryPaths.Count} library paths: {string.Join(", ", libraryPaths)}", _logger);

        progress.Report(10);

        cancellationToken.ThrowIfCancellationRequested();

        var result = _strmRepairService.RepairStrmFiles(libraryPaths, dryRun, cancellationToken);

        progress.Report(90);

        _pluginLog.LogInfo("StrmRepair", $"Task finished. Valid: {result.ValidCount}, Repaired: {result.RepairedCount}, Broken: {result.BrokenCount}, Ambiguous: {result.AmbiguousCount}, Invalid: {result.InvalidContentCount}", _logger);

        progress.Report(100);
        return Task.CompletedTask;
    }
}
