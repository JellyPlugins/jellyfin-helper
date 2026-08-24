using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
/// Base class for cleanup-related scheduled task tests.
/// Provides mock-based <see cref="ICleanupConfigHelper"/>, <see cref="ICleanupTrackingService"/>,
/// and <see cref="ITrashService"/> instances, along with a default <see cref="PluginConfiguration"/>.
/// </summary>
public abstract class CleanupTaskTestBase : IDisposable
{
    /// <summary>
    /// Gets or sets the plugin configuration used for this test.
    /// Subclasses can modify it in their constructor after calling base().
    /// </summary>
    protected PluginConfiguration Config { get; set; }

    /// <summary>
    /// Gets the mock for the cleanup config helper.
    /// </summary>
    protected Mock<ICleanupConfigHelper> MockConfigHelper { get; }

    /// <summary>
    /// Gets the mock for the cleanup tracking service.
    /// </summary>
    protected Mock<ICleanupTrackingService> MockTrackingService { get; }

    /// <summary>
    /// Gets the mock for the trash service.
    /// </summary>
    protected Mock<ITrashService> MockTrashService { get; }

    protected CleanupTaskTestBase()
    {
        Config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.DryRun,
            EmptyMediaFolderTaskMode = TaskMode.DryRun,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            LinkRepairTaskMode = TaskMode.DryRun,
            UseTrash = false,
            ConfigVersion = 1,
        };

        MockConfigHelper = new Mock<ICleanupConfigHelper>();
        MockConfigHelper.Setup(x => x.GetConfig()).Returns(() => Config);
        MockConfigHelper.Setup(x => x.GetTrickplayTaskMode()).Returns(() => Config.TrickplayTaskMode);
        MockConfigHelper.Setup(x => x.GetEmptyMediaFolderTaskMode()).Returns(() => Config.EmptyMediaFolderTaskMode);
        MockConfigHelper.Setup(x => x.GetOrphanedSubtitleTaskMode()).Returns(() => Config.OrphanedSubtitleTaskMode);
        MockConfigHelper.Setup(x => x.GetLinkRepairTaskMode()).Returns(() => Config.LinkRepairTaskMode);
        MockConfigHelper.Setup(x => x.IsDryRunTrickplay()).Returns(() => Config.TrickplayTaskMode == TaskMode.DryRun);
        MockConfigHelper.Setup(x => x.IsDryRunEmptyMediaFolders()).Returns(() => Config.EmptyMediaFolderTaskMode == TaskMode.DryRun);
        MockConfigHelper.Setup(x => x.IsDryRunOrphanedSubtitles()).Returns(() => Config.OrphanedSubtitleTaskMode == TaskMode.DryRun);
        MockConfigHelper.Setup(x => x.IsDryRunLinkRepair()).Returns(() => Config.LinkRepairTaskMode == TaskMode.DryRun);
        MockConfigHelper.Setup(x => x.IsOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        // IsFileOldEnoughForDeletion is a SEPARATE method used by the file-based orphan cleaners
        // (subtitle cleaner). Without this setup Moq returns the default value (false) for any
        // file path, which makes the orphan-subtitle branch silently skip every candidate as
        // "too new". Tests that specifically want to exercise the age gate can override this
        // to false on their own Mock<ICleanupConfigHelper>.
        MockConfigHelper.Setup(x => x.IsFileOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        MockConfigHelper.Setup(x => x.GetTrashPath(It.IsAny<string>())).Returns<string>(lib => Path.Join(lib, ".trash"));
        MockConfigHelper.Setup(x => x.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns<ILibraryManager>(lm =>
            {
                var virtualFolders = lm.GetVirtualFolders();
                return virtualFolders
                    .Where(f => Jellyfin.Plugin.JellyfinHelper.Services.Cleanup.CleanupConfigHelper.IsCleanupEligibleCollectionType(f.CollectionType))
                    .Where(f => !(f.Name ?? string.Empty).Contains("collection", StringComparison.OrdinalIgnoreCase)
                             && !(f.Name ?? string.Empty).Contains("boxset", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(f => f.Locations)
                    .Where(loc => !loc.Contains("/collections", StringComparison.OrdinalIgnoreCase)
                               && !loc.Contains("\\collections", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

        MockTrackingService = new Mock<ICleanupTrackingService>();
        MockTrashService = new Mock<ITrashService>();
    }

    public virtual void Dispose()
    {
        // No static cleanup needed - all state is in mock instances
    }

    /// <summary>
    /// Builds a platform-native absolute test path from segments.
    /// Ensures <see>
    ///     <cref>Path.GetDirectoryName</cref>
    /// </see>
    /// returns a value
    /// consistent with the path used in mock setups, regardless of OS.
    /// </summary>
    protected static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    /// <summary>
    /// Verifies that the logger mock received at least one log call containing the given message part.
    /// </summary>
    protected static void VerifyLogContains<T>(Mock<ILogger<T>> loggerMock, string messagePart, LogLevel level)
    {
        loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Verifies that the logger mock never received a log call containing the given message part.
    /// </summary>
    protected static void VerifyLogNeverContains<T>(Mock<ILogger<T>> loggerMock, string messagePart, LogLevel level)
    {
        loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// A synchronous implementation of <see cref="IProgress{T}"/> that invokes the callback immediately.
    /// Unlike <see cref="Progress{T}"/>, this does not post to a SynchronizationContext.
    /// </summary>
    protected sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}