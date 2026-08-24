using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Exercises the permission-denied branches of <see cref="TrashService.CheckPathAccess"/>,
///     <see cref="TrashService.GetTrashSummary"/>, and <see cref="TrashService.GetTrashContents"/>
///     that only run when the process cannot read or write a real directory. These require a genuine
///     OS permission denial: on Windows a deny ACL, on Unix a mode with the relevant bit stripped.
///     Because a privileged user (e.g. root in CI Docker) bypasses those restrictions, each test first
///     probes whether the denial actually bites and no-ops if it does not, so the branch is asserted
///     only when it is truly reachable, never falsely passed.
/// </summary>
public sealed class TrashServicePathAccessTests : IDisposable
{
    private readonly Mock<IPluginLogService> _mockPluginLog = new();
    private readonly ILogger _logger = TestMockFactory.CreateLogger().Object;
    private readonly TrashService _service;
    private readonly string _testRoot = Path.Join(Path.GetTempPath(), $"TrashPathAccess-{Guid.NewGuid():N}");

    public TrashServicePathAccessTests()
    {
        _service = new TrashService(_mockPluginLog.Object);
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        // Restore permissions first so the recursive delete can succeed, then remove the tree.
        try
        {
            RestoreFullAccess(_testRoot);
            foreach (var dir in Directory.EnumerateDirectories(_testRoot, "*", SearchOption.AllDirectories))
            {
                RestoreFullAccess(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(_testRoot))
                {
                    return;
                }

                Directory.Delete(_testRoot, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public void CheckPathAccess_ExistingDirNotWritable_ReportsExistsCanReadCannotWrite()
    {
        // A directory the process can read but not write must be reported as existing, readable,
        // and NOT writable, with an error message that specifically names the write failure.
        var dir = Path.Join(_testRoot, "read-only-dir");
        Directory.CreateDirectory(dir);
        DenyWrite(dir);

        if (!WriteIsActuallyDenied(dir))
        {
            // Privileged process (root/admin) bypasses the deny, the branch is unreachable here.
            return;
        }

        var result = _service.CheckPathAccess(dir, _logger);

        Assert.True(result.Exists);
        Assert.True(result.CanRead);
        Assert.False(result.CanWrite);
        Assert.False(result.HasFullAccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("write", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        _mockPluginLog.Verify(
            l => l.LogWarning(
                "Trash",
                It.Is<string>(m => m.Contains("Insufficient permissions") && m.Contains("write")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void CheckPathAccess_NonExistentPath_ParentNotWritable_ReportsCannotCreate()
    {
        // When the target does not exist, CheckPathAccess walks up to the nearest existing parent.
        // If that parent is not writable, the result must be Exists=false / CanRead=true /
        // CanWrite=false with a "no write permission on parent" message, the "cannot create" case.
        var parent = Path.Join(_testRoot, "locked-parent");
        Directory.CreateDirectory(parent);
        DenyWrite(parent);

        if (!WriteIsActuallyDenied(parent))
        {
            return;
        }

        var target = Path.Join(parent, "would-be-created");

        var result = _service.CheckPathAccess(target, _logger);

        Assert.False(result.Exists);
        Assert.True(result.CanRead);
        Assert.False(result.CanWrite);
        Assert.False(result.HasFullAccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("no write permission on parent", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        _mockPluginLog.Verify(
            l => l.LogWarning(
                "Trash",
                It.Is<string>(m => m.Contains("no write permission on parent")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void GetTrashSummary_UnreadableTrashFolder_ReturnsPartialAndLogsWarning()
    {
        // If the trash folder exists but its contents cannot be enumerated, GetTrashSummary must
        // swallow the access error, return the partial (here: zero) totals rather than throwing,
        // and log a "Partial trash summary" warning.
        var trash = Path.Join(_testRoot, "unreadable-summary");
        Directory.CreateDirectory(trash);
        DenyRead(trash);

        if (!EnumerationIsActuallyDenied(trash))
        {
            return;
        }

        var (totalSize, itemCount) = _service.GetTrashSummary(trash, _logger);

        Assert.Equal(0, totalSize);
        Assert.Equal(0, itemCount);
        _mockPluginLog.Verify(
            l => l.LogWarning(
                "Trash",
                It.Is<string>(m => m.Contains("Partial trash summary")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void GetTrashContents_UnreadableTrashFolder_ReturnsEmptyAndLogsWarning()
    {
        // Same denial for GetTrashContents: the enumeration catch must return an empty list (not
        // throw) and log a "Partial trash contents" warning.
        var trash = Path.Join(_testRoot, "unreadable-contents");
        Directory.CreateDirectory(trash);
        DenyRead(trash);

        if (!EnumerationIsActuallyDenied(trash))
        {
            return;
        }

        var result = _service.GetTrashContents(trash, 30, _logger);

        Assert.Empty(result);
        _mockPluginLog.Verify(
            l => l.LogWarning(
                "Trash",
                It.Is<string>(m => m.Contains("Partial trash contents")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    // ── Permission helpers (platform-branched) ────────────────────────────────

    private static void DenyWrite(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            DenyWriteWindows(dir);
        }
        else
        {
            // r-xr-xr-x: readable/enumerable but not writable.
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void DenyRead(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            DenyReadWindows(dir);
        }
        else
        {
            // --x------: no read bit, so directory enumeration is denied.
            File.SetUnixFileMode(dir, UnixFileMode.UserExecute);
        }
    }

    private static void RestoreFullAccess(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            RestoreFullAccessWindows(dir);
        }
        else
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    ///     Probes whether writing into <paramref name="dir"/> is actually blocked. A privileged
    ///     user bypasses the deny ACL / mode, in which case the target branch is unreachable and the
    ///     caller must no-op instead of asserting a branch that will not run.
    /// </summary>
    private static bool WriteIsActuallyDenied(string dir)
    {
        var probe = Path.Join(dir, $".probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>
    ///     Probes whether enumerating <paramref name="dir"/> is actually blocked (root bypass check).
    /// </summary>
    private static bool EnumerationIsActuallyDenied(string dir)
    {
        try
        {
            Directory.GetFileSystemEntries(dir);
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DenyWriteWindows(string dir)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.WriteData | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void DenyReadWindows(string dir)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreFullAccessWindows(string dir)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        // Purge any deny rules this test added so Dispose can delete the tree.
        security.PurgeAccessRules(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
