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
/// A scheduled task to perform a dry run of the empty media folder cleanup.
/// </summary>
public class DryRunCleanEmptyMediaFoldersTask : CleanEmptyMediaFoldersTask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DryRunCleanEmptyMediaFoldersTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public DryRunCleanEmptyMediaFoldersTask(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<DryRunCleanEmptyMediaFoldersTask> logger)
        : base(libraryManager, fileSystem, logger)
    {
    }

    /// <inheritdoc />
    public override string Name => "Empty Media Folder Cleaner (Dry Run)";

    /// <inheritdoc />
    public override string Key => "EmptyMediaFolderCleanerDryRun";

    /// <inheritdoc />
    public override string Description => "Logs which empty media folders would be deleted without actually deleting them.";

    /// <inheritdoc />
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(true, progress, cancellationToken);
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}