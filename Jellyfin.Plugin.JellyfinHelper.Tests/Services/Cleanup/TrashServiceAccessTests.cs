using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Unit tests for CheckPathAccess, CanReadDirectory, and CanWriteDirectory.
/// </summary>
public sealed class TrashServiceAccessTests : IDisposable
{
    private readonly Mock<IPluginLogService> _mockPluginLog;
    private readonly Mock<ILogger> _mockLogger;
    private readonly TrashService _service;
    private readonly string _testRoot;

    public TrashServiceAccessTests()
    {
        _mockPluginLog = new Mock<IPluginLogService>();
        _mockLogger = TestMockFactory.CreateLogger();
        _service = new TrashService(_mockPluginLog.Object);
        _testRoot = Path.Join(Path.GetTempPath(), $"trash-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient file locks must not fail the test suite.
        }
    }

    [Fact]
    public void CheckPathAccess_EmptyPath_ReturnsNoAccess()
    {
        var result = _service.CheckPathAccess(string.Empty, _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.False(result.CanRead);
        Assert.False(result.CanWrite);
        Assert.False(result.Exists);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void CheckPathAccess_NullPath_ReturnsNoAccess()
    {
        var result = _service.CheckPathAccess(null!, _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void CheckPathAccess_WhitespacePath_ReturnsNoAccess()
    {
        var result = _service.CheckPathAccess("   ", _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void CheckPathAccess_ExistingWritableDir_ReturnsFullAccess()
    {
        var result = _service.CheckPathAccess(_testRoot, _mockLogger.Object);

        Assert.True(result.HasFullAccess);
        Assert.True(result.Exists);
        Assert.True(result.CanRead);
        Assert.True(result.CanWrite);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void CheckPathAccess_NonExistentPath_WritableParent_ReturnsCanCreate()
    {
        var nonExistent = Path.Join(_testRoot, "does-not-exist-yet");

        var result = _service.CheckPathAccess(nonExistent, _mockLogger.Object);

        Assert.True(result.HasFullAccess);
        Assert.False(result.Exists);
        Assert.True(result.CanRead);
        Assert.True(result.CanWrite);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData(false, false, "read or write")]
    [InlineData(false, true, "read")]
    [InlineData(true, false, "write")]
    [InlineData(true, true, null)]
    public void CheckPathAccess_ProbeResults_ReportExactMissingPermission(bool canRead, bool canWrite, string? missingAccess)
    {
        var service = new PermissionProbeTrashService(_mockPluginLog.Object, canRead, canWrite);

        var result = service.CheckPathAccess(_testRoot, _mockLogger.Object);

        Assert.True(result.Exists);
        Assert.Equal(canRead, result.CanRead);
        Assert.Equal(canWrite, result.CanWrite);
        Assert.Equal(canRead && canWrite, result.HasFullAccess);
        if (missingAccess is null)
        {
            Assert.Null(result.ErrorMessage);
        }
        else
        {
            Assert.Equal($"Insufficient permissions: cannot {missingAccess} path '{Path.GetFullPath(_testRoot)}'.", result.ErrorMessage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckPathAccess_WriteProbeCleanupFailure_IsLoggedWithoutLosingConfirmedAccess(bool accessDenied)
    {
        Exception failure = accessDenied ? new UnauthorizedAccessException("probe cleanup denied") : new IOException("probe cleanup failed");
        var service = new ProbeCleanupFailureTrashService(_mockPluginLog.Object, failure);

        var result = service.CheckPathAccess(_testRoot, _mockLogger.Object);

        Assert.True(result.HasFullAccess);
        var probe = Assert.IsType<string>(service.ProbePath);
        Assert.True(File.Exists(probe));
        _mockPluginLog.Verify(log => log.LogWarning("Trash", It.Is<string>(message => message.Contains(probe, StringComparison.Ordinal)), failure, It.IsAny<ILogger>()), Times.Once);
    }

    [Fact]
    public void CheckPathAccess_DeeplyNestedNonExistentPath_WritableAncestor_ReturnsCanCreate()
    {
        var deepPath = Path.Join(_testRoot, "level1", "level2", "level3");

        var result = _service.CheckPathAccess(deepPath, _mockLogger.Object);

        Assert.True(result.HasFullAccess);
        Assert.False(result.Exists);
        Assert.True(result.CanWrite);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    public void CheckPathAccess_InvalidWindowsReservedName_HandlesGracefully(string reservedName)
    {
        // On Windows these are reserved; on Linux they are valid.
        // The method should not throw regardless of platform.
        var testPath = Path.Join(_testRoot, reservedName);
        var result = _service.CheckPathAccess(testPath, _mockLogger.Object);

        // We just verify it doesn't throw and returns a result
        Assert.NotNull(result);
    }

    [Fact]
    public void CheckPathAccess_MalformedPath_ReturnsInvalidPathError()
    {
        // A non-whitespace path with an embedded null char passes the IsNullOrWhiteSpace guard but makes Path.GetFullPath throw ArgumentException.
        var result = _service.CheckPathAccess("bad\0path", _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.False(result.Exists);
        Assert.False(result.CanRead);
        Assert.False(result.CanWrite);
        Assert.NotNull(result.ErrorMessage);
        Assert.StartsWith("Invalid path:", result.ErrorMessage, StringComparison.Ordinal);
        _mockPluginLog.Verify(
            l => l.LogWarning(
                "Trash",
                It.Is<string>(msg => msg.Contains("invalid path")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }
    private sealed class PermissionProbeTrashService : TrashService
    {
        private readonly bool _canRead;
        private readonly bool _canWrite;

        public PermissionProbeTrashService(IPluginLogService pluginLog, bool canRead, bool canWrite)
            : base(pluginLog)
        {
            _canRead = canRead;
            _canWrite = canWrite;
        }

        internal override bool CanReadDirectory(string directoryPath) => _canRead;

        internal override bool CanWriteDirectory(string directoryPath) => _canWrite;
    }

    private sealed class ProbeCleanupFailureTrashService : TrashService
    {
        private readonly Exception _failure;

        public ProbeCleanupFailureTrashService(IPluginLogService pluginLog, Exception failure)
            : base(pluginLog)
        {
            _failure = failure;
        }

        public string? ProbePath { get; private set; }

        internal override void DeleteWriteAccessProbe(string probePath)
        {
            ProbePath = probePath;
            throw _failure;
        }
    }
}
