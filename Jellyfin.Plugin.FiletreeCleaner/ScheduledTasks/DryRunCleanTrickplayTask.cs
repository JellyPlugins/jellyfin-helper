using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FiletreeCleaner.ScheduledTasks;

/// <summary>
/// A scheduled task to perform a dry run of the trickplay folder cleanup.
/// </summary>
public class DryRunCleanTrickplayTask : CleanTrickplayTask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DryRunCleanTrickplayTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public DryRunCleanTrickplayTask(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<DryRunCleanTrickplayTask> logger)
        : base(libraryManager, fileSystem, logger)
    {
    }

    /// <inheritdoc />
    public override string Name => "Trickplay Folder Cleaner (Dry Run)";

    /// <inheritdoc />
    public override string Key => "TrickplayFolderCleanerDryRun";

    /// <inheritdoc />
    public override string Description => "Logs which .trickplay folders would be deleted without actually deleting them.";

    /// <inheritdoc />
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(true, progress, cancellationToken);
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No default triggers for dry run
        return Array.Empty<TaskTriggerInfo>();
    }
}