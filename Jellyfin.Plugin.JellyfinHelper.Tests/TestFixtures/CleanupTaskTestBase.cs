using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
/// Base class for cleanup-related scheduled task tests.
/// Handles the <see cref="CleanupConfigHelper.ConfigOverride"/> lifecycle
/// and provides a default <see cref="PluginConfiguration"/>.
/// </summary>
[Collection("ConfigOverride")]
public abstract class CleanupTaskTestBase : IDisposable
{
    /// <summary>
    /// Gets or sets the plugin configuration used for this test.
    /// Subclasses can modify it in their constructor after calling base().
    /// </summary>
    protected PluginConfiguration Config { get; set; }

    protected CleanupTaskTestBase()
    {
        Config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.DryRun,
            EmptyMediaFolderTaskMode = TaskMode.DryRun,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            StrmRepairTaskMode = TaskMode.DryRun,
            UseTrash = false,
            ConfigVersion = 1,
        };
        CleanupConfigHelper.ConfigOverride = Config;
    }

    public virtual void Dispose()
    {
        CleanupConfigHelper.ConfigOverride = null;
    }

    /// <summary>
    /// Builds a platform-native absolute test path from segments.
    /// Ensures <see cref="Path.GetDirectoryName"/> returns a value
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
    protected sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }
}
