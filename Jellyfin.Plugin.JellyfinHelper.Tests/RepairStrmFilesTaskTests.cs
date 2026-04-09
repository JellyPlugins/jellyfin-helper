using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
/// Tests for <see cref="RepairStrmFilesTask"/>.
/// Note: Name/Key/Category/GetDefaultTriggers are now on the master HelperCleanupTask.
/// </summary>
public class RepairStrmFilesTaskTests
{
    private static RepairStrmFilesTask CreateTask()
    {
        var loggerMock = new Mock<ILogger<RepairStrmFilesTask>>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var fileSystem = new MockFileSystem();
        var serviceLoggerMock = new Mock<ILogger<StrmRepairService>>();
        var service = new StrmRepairService(fileSystem, serviceLoggerMock.Object);
        return new RepairStrmFilesTask(loggerMock.Object, libraryManagerMock.Object, service);
    }
}