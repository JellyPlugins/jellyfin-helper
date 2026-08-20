using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Tests for <see cref="ReparsePointGuard" />.
///     The happy-path that calls <c>delete</c> on a real reparse point cannot be exercised
///     in unit tests without symlink/junction creation privileges (unavailable in CI).
///     All testable branches — non-existent path, real non-reparse directory, fail-closed
///     throw, and delete-action never-called — are covered here.
/// </summary>
public sealed class ReparsePointGuardTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReparsePointGuardTests" /> class.
    /// </summary>
    public ReparsePointGuardTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "jfh-rpguard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    // ── IsReparsePoint ────────────────────────────────────────────────────────

    [Fact]
    public void IsReparsePoint_NonExistentPath_ReturnsFalse()
    {
        var path = Path.Join(_tempDir, "does-not-exist");
        Assert.False(ReparsePointGuard.IsReparsePoint(path));
    }

    [Fact]
    public void IsReparsePoint_RealDirectory_ReturnsFalse()
    {
        var dir = Path.Join(_tempDir, "real");
        Directory.CreateDirectory(dir);
        Assert.False(ReparsePointGuard.IsReparsePoint(dir));
    }

    // ── DeleteLinkNode ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteLinkNode_NonExistentPath_ThrowsAndLeavesNothingChanged()
    {
        var path = Path.Join(_tempDir, "does-not-exist");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ReparsePointGuard.DeleteLinkNode(path, _ => { }));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("aborting to avoid data loss", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteLinkNode_RealDirectory_ThrowsAndLeavesDirectoryUnchanged()
    {
        var dir = Path.Join(_tempDir, "real");
        Directory.CreateDirectory(dir);

        Assert.Throws<InvalidOperationException>(
            () => ReparsePointGuard.DeleteLinkNode(dir, _ => { }));

        // Fail-closed: the real directory must still exist.
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteLinkNode_RealDirectory_DeleteActionIsNeverInvoked()
    {
        var dir = Path.Join(_tempDir, "real");
        Directory.CreateDirectory(dir);
        var invoked = false;

        try
        {
            ReparsePointGuard.DeleteLinkNode(dir, _ => invoked = true);
        }
        catch (InvalidOperationException)
        {
            // Expected throw — the action must not have been called.
        }

        Assert.False(invoked);
    }
}
