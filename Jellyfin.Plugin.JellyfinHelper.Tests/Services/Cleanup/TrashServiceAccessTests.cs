using Jellyfin.Plugin.JellyfinHelper.Services;
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
        var requestedRoot = Path.Join(Path.GetTempPath(), $"trash-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(requestedRoot);
        // Canonicalize the trusted OS temp location first: macOS /var aliases /private/var.
        _testRoot = PathValidator.ResolveDirectoryPath(CanonicalizeTrustedDirectory(requestedRoot));
    }

    public void Dispose()
    {
        try
        {
            // Unlink only test-created junction nodes before recursively deleting our root.
            foreach (var link in Directory.EnumerateDirectories(_testRoot)
                         .Where(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
            {
                Directory.Delete(link);
            }

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

    [Theory]
    [InlineData("")]
    [InlineData("existing-directory")]
    [InlineData("existing-file")]
    [InlineData("missing/child")]
    public void CheckPathAccess_ProtectedLinkedTargetOrAncestor_IsRefusedBeforeAccessProbes(string suffix)
    {
        var linkedParent = Path.Join(_testRoot, "linked");
        var protectedRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/etc";
        DirectoryLinkTestHelper.Create(linkedParent, protectedRoot);
        var descendant = suffix switch
        {
            "existing-directory" => OperatingSystem.IsWindows() ? "System32" : "ssl",
            "existing-file" => OperatingSystem.IsWindows() ? "win.ini" : "passwd",
            _ => suffix,
        };
        var target = Path.Join(linkedParent, descendant);
        if (suffix == "existing-directory")
        {
            Assert.True(Directory.Exists(target));
            Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
        }
        else if (suffix == "existing-file")
        {
            Assert.True(File.Exists(target));
        }

        var service = new GuardedAccessTrashService(_mockPluginLog.Object);

        var result = service.CheckPathAccess(target, _mockLogger.Object);

        Assert.False(result.Exists);
        Assert.False(result.CanRead);
        Assert.False(result.CanWrite);
        Assert.Equal("Access check refused for a protected or unverifiable path.", result.ErrorMessage);
        Assert.Equal(0, service.AccessProbeCount);
        Assert.Empty(service.AccessProbePaths);
    }

    [Fact]
    public void CheckPathAccess_ProtectedTarget_IsRefusedWithoutAccessProbes()
    {
        var protectedRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/etc";
        var protectedPath = Path.Join(protectedRoot, "jfh-trash");
        var service = new GuardedAccessTrashService(_mockPluginLog.Object);

        var result = service.CheckPathAccess(protectedPath, _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.False(result.Exists);
        Assert.Equal(0, service.AccessProbeCount);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    [InlineData("security")]
    public void CheckPathAccess_UnverifiableAncestor_FailsClosed(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "denied" => new UnauthorizedAccessException("metadata denied"),
            "security" => new System.Security.SecurityException("protected metadata"),
            _ => new IOException("metadata failed"),
        };
        var service = new GuardedAccessTrashService(_mockPluginLog.Object, failure);

        var result = service.CheckPathAccess(_testRoot, _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.Equal("Access check refused for a protected or unverifiable path.", result.ErrorMessage);
        Assert.Equal(0, service.AccessProbeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("child")]
    [InlineData("missing/child")]
    public void CheckPathAccess_SafeAncestorLink_ProbesResolvedExternalTarget(string suffix)
    {
        var external = Path.Join(_testRoot, "external-trash");
        Directory.CreateDirectory(Path.Join(external, "child"));
        var link = Path.Join(_testRoot, "alias");
        DirectoryLinkTestHelper.Create(link, external);
        var service = new GuardedAccessTrashService(_mockPluginLog.Object);

        var result = service.CheckPathAccess(Path.Join(link, suffix), _mockLogger.Object);

        Assert.True(result.HasFullAccess);
        Assert.Equal(suffix != "missing/child", result.Exists);
        var expectedProbe = suffix == "child" ? Path.Join(external, "child") : external;
        Assert.NotEmpty(service.AccessProbePaths);
        Assert.All(service.AccessProbePaths, path => Assert.Equal(expectedProbe, path));
        Assert.Empty(Directory.GetFiles(external, ".jfh-access-probe-*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Join(external, "missing")));
    }

    [Fact]
    public void CheckPathAccess_LinkTargetWithProtectedAncestor_IsRefused()
    {
        var protectedRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/etc";
        var ancestor = Path.Join(_testRoot, "ancestor");
        var link = Path.Join(_testRoot, "indirect");
        DirectoryLinkTestHelper.Create(ancestor, protectedRoot);
        DirectoryLinkTestHelper.Create(link, Path.Join(ancestor, OperatingSystem.IsWindows() ? "System32" : "ssl"));
        var service = new GuardedAccessTrashService(_mockPluginLog.Object);

        var result = service.CheckPathAccess(link, _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.Equal("Access check refused for a protected or unverifiable path.", result.ErrorMessage);
        Assert.Equal(0, service.AccessProbeCount);
    }

    [Fact]
    public void CheckPathAccess_CyclicAncestorLink_FailsClosed()
    {
        var link = Path.Join(_testRoot, "cycle");
        DirectoryLinkTestHelper.Create(link, link);
        var service = new GuardedAccessTrashService(_mockPluginLog.Object);

        var result = service.CheckPathAccess(Path.Join(link, "child"), _mockLogger.Object);

        Assert.False(result.HasFullAccess);
        Assert.Equal("Access check refused for a protected or unverifiable path.", result.ErrorMessage);
        Assert.Equal(0, service.AccessProbeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing/child")]
    public void CheckPathAccess_AliasRetargetedAfterResolution_StillProbesOriginalSafeTarget(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        var external = Path.Join(_testRoot, "external-trash");
        Directory.CreateDirectory(external);
        var link = Path.Join(_testRoot, "alias");
        DirectoryLinkTestHelper.Create(link, external);
        var protectedRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/etc";
        var service = new GuardedAccessTrashService(_mockPluginLog.Object, afterResolution: () =>
        {
            Directory.Delete(link);
            DirectoryLinkTestHelper.Create(link, protectedRoot);
        });

        var result = service.CheckPathAccess(Path.Join(link, suffix), _mockLogger.Object);

        Assert.True(result.HasFullAccess);
        Assert.Equal(suffix.Length == 0, result.Exists);
        Assert.NotEmpty(service.AccessProbePaths);
        Assert.All(service.AccessProbePaths, path => Assert.Equal(external, path));
        Assert.Empty(Directory.GetFiles(external, ".jfh-access-probe-*"));
        // This covers replacement of a resolved-away alias, not replacement of an
        // ordinary ancestor in the resolved target itself (still a TOCTOU concern).
    }

    [Fact]
    public void CanonicalizeTrustedDirectory_ResolvesAncestorAliasesBeforeProbeExpectations()
    {
        var target = Path.Join(_testRoot, "target");
        var child = Path.Join(target, "child");
        var alias = Path.Join(_testRoot, "temp-alias");
        Directory.CreateDirectory(child);
        DirectoryLinkTestHelper.Create(alias, target);

        var canonical = PathValidator.ResolveDirectoryPath(CanonicalizeTrustedDirectory(Path.Join(alias, "child")));
        var service = new GuardedAccessTrashService(_mockPluginLog.Object);
        var result = service.CheckPathAccess(canonical, _mockLogger.Object);

        Assert.Equal(child, canonical);
        Assert.True(result.HasFullAccess);
        Assert.NotEmpty(service.AccessProbePaths);
        Assert.All(service.AccessProbePaths, path => Assert.Equal(child, path));
    }

    private static string CanonicalizeTrustedDirectory(string path)
    {
        var normalized = Path.GetFullPath(path);
        var root = Path.GetPathRoot(normalized)!;
        var resolved = root;
        foreach (var segment in normalized[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Join(resolved, segment));
            resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }

        return resolved;
    }

    private sealed class GuardedAccessTrashService : TrashService
    {
        private readonly Exception? _failure;
        private readonly Action? _afterResolution;

        public GuardedAccessTrashService(IPluginLogService pluginLog, Exception? failure = null, Action? afterResolution = null)
            : base(pluginLog)
        {
            _failure = failure;
            _afterResolution = afterResolution;
        }

        public List<string> AccessProbePaths { get; } = [];

        public int AccessProbeCount { get; private set; }

        internal override string ResolveAccessProbePath(string path)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            var resolved = base.ResolveAccessProbePath(path);
            _afterResolution?.Invoke();
            return resolved;
        }

        internal override bool CanReadDirectory(string directoryPath)
        {
            AccessProbeCount++;
            AccessProbePaths.Add(directoryPath);
            // A regression must fail the test, never read or write a protected target.
            _ = PathValidator.ResolveDirectoryPath(directoryPath);
            return base.CanReadDirectory(directoryPath);
        }

        internal override bool CanWriteDirectory(string directoryPath)
        {
            AccessProbeCount++;
            AccessProbePaths.Add(directoryPath);
            _ = PathValidator.ResolveDirectoryPath(directoryPath);
            return base.CanWriteDirectory(directoryPath);
        }
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
