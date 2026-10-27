using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
///     Base class for cleanup-related scheduled task tests. Provides mock-based ICleanupConfigHelper, ICleanupTrackingService, and ITrashService instances, along with a default PluginConfiguration.
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
        // IsFileOldEnoughForDeletion is a SEPARATE method used by the file-based orphan cleaners (subtitle cleaner).
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
    /// Builds a platform-native absolute test path from segments. Ensures <see cref="Path.GetDirectoryName"/> returns a value consistent with the path used in mock setups, regardless of OS.
    /// </summary>
    /// <param name="segments">The path segments to combine.</param>
    /// <returns>A platform-native absolute test path string.</returns>
    protected static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    /// <summary>
    /// Verifies that the logger mock received at least one log call containing the given message part.
    /// </summary>
    /// <typeparam name="T">The logger category type.</typeparam>
    /// <param name="loggerMock">The logger mock to verify.</param>
    /// <param name="messagePart">The text part expected in the log message.</param>
    /// <param name="level">The log level to check.</param>
    protected static void VerifyLogContains<T>(Mock<ILogger<T>> loggerMock, string messagePart, LogLevel level)
    {
        loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Verifies that the logger mock never received a log call containing the given message part.
    /// </summary>
    /// <typeparam name="T">The logger category type.</typeparam>
    /// <param name="loggerMock">The logger mock to verify.</param>
    /// <param name="messagePart">The text part that should not appear in log messages.</param>
    /// <param name="level">The log level to check.</param>
    protected static void VerifyLogNeverContains<T>(Mock<ILogger<T>> loggerMock, string messagePart, LogLevel level)
    {
        loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// A synchronous implementation of <see cref="IProgress{T}"/> that invokes the callback immediately.
    /// </summary>
    /// <typeparam name="T">The type of progress update value.</typeparam>
    /// <param name="handler">The callback action to invoke immediately on report.</param>
    protected sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        /// <inheritdoc />
        public void Report(T value) => handler(value);
    }
}