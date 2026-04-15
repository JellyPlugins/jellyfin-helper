using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Strm;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using Moq;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
/// Tests for <see cref="RepairStrmFilesTask"/>.
/// Note: Name/Key/Category/GetDefaultTriggers are now on the master HelperCleanupTask.
/// </summary>
public class RepairStrmFilesTaskTests
{
    private static RepairStrmFilesTask CreateTask()
    {
        var fileSystem = new MockFileSystem();
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        var strmRepairService = new StrmRepairService(
            fileSystem,
            pluginLog,
            TestMockFactory.CreateLogger<StrmRepairService>().Object);
        return new RepairStrmFilesTask(
            TestMockFactory.CreateLogger<RepairStrmFilesTask>().Object,
            new Mock<ILibraryManager>().Object,
            pluginLog,
            strmRepairService,
            configHelperMock.Object);
    }
}