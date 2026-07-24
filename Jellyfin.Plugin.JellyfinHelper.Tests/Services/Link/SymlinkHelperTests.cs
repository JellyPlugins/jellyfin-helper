using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Integration tests for the production <see cref="SymlinkHelper"/> against a real filesystem.
///     Every test gets its own isolated temp directory to prevent cross-contamination.
///     Tests that require symlink creation privileges gracefully skip when unsupported
///     (Windows without Developer Mode / admin, some containerised CI runners).
/// </summary>
public sealed class SymlinkHelperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SymlinkHelper _sut;

    public SymlinkHelperTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-symlink-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _sut = new SymlinkHelper();
    }

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

    /// <summary>
    ///     Attempts to create a throwaway symlink to detect whether the current environment
    ///     permits symlink creation. Windows requires either Developer Mode enabled or admin
    ///     privileges; certain sandboxed CI environments also reject them.
    ///     <para>
    ///         When this returns <c>false</c>, the calling test performs an early <c>return</c>
    ///         and the test reports as <b>passed</b> (not skipped) — xUnit 2.9 has no runtime
    ///         Assert.Skip API, and the currently referenced packages do not include a
    ///         third-party SkippableFact package. The trade-off is deliberate: a hard
    ///         <c>Assert.Fail</c> on unsupported environments would break the CI matrix
    ///         (Windows without Developer Mode / rootless containers), while a
    ///         <c>throw new SkipException</c> would require adding another package. The
    ///         escape hatch is validated once via
    ///         <see cref="SymlinkProbe_MustExecuteAtLeastOnceInLinuxCi"/> so a regression
    ///         that broke <b>every</b> symlink test on Linux (where symlinks are guaranteed)
    ///         would still surface loudly.
    ///     </para>
    /// </summary>
    private bool SymlinksSupported()
    {
        var probeTarget = Path.Join(_tempDir, "probe-target.txt");
        var probeLink = Path.Join(_tempDir, "probe-link.txt");
        try
        {
            File.WriteAllText(probeTarget, "probe");
            File.CreateSymbolicLink(probeLink, probeTarget);
            File.Delete(probeLink);
            File.Delete(probeTarget);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Meta-test: guarantees that at least one environment in the CI matrix actually
    ///     exercises the symlink path. On Linux/macOS symlinks are guaranteed to work in
    ///     a per-test <c>Path.GetTempPath()</c> directory, so if this ever fails there we
    ///     know the probe itself is broken — before every other symlink test silently
    ///     degenerates to "passed by skipping".
    /// </summary>
    [Fact]
    public void SymlinkProbe_MustExecuteAtLeastOnceInLinuxCi()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows CI without Developer Mode legitimately cannot create symlinks —
            // the return-on-unsupported pattern in the other tests is the right answer
            // there.
            return;
        }

        Assert.True(
            SymlinksSupported(),
            "SymlinksSupported() reported false on a non-Windows host. If this ever fires, " +
            "the probe (or the underlying temp-dir permission model) is broken and every " +
            "downstream symlink test is silently no-op'ing.");
    }

    // -----------------------------------------------------------------------
    // IsSymlink — must never throw on non-existent / permission-denied paths.
    // -----------------------------------------------------------------------

    [Fact]
    public void IsSymlink_NonExistentPath_ReturnsFalse_DoesNotThrow()
    {
        // An early implementation used FileInfo(...).LinkTarget directly
        // without an Exists guard, which threw on missing paths — turning a routine
        // "does this file need repair?" check into a fatal error.
        var path = Path.Join(_tempDir, "does-not-exist.txt");
        Assert.False(_sut.IsSymlink(path));
    }

    [Fact]
    public void IsSymlink_RegularFile_ReturnsFalse()
    {
        var path = Path.Join(_tempDir, "regular.txt");
        File.WriteAllText(path, "hello");
        Assert.False(_sut.IsSymlink(path));
    }

    [Fact]
    public void IsSymlink_Directory_ReturnsFalse()
    {
        // FileInfo pointed at a directory has Exists=false — the helper must handle this
        // silently rather than throwing.
        var subDir = Path.Join(_tempDir, "subdir");
        Directory.CreateDirectory(subDir);
        Assert.False(_sut.IsSymlink(subDir));
    }

    [Fact]
    public void IsSymlink_RealSymlink_ReturnsTrue()
    {
        if (!SymlinksSupported())
        {
            return; // skip — environment can't create symlinks
        }

        var target = Path.Join(_tempDir, "target.txt");
        var link = Path.Join(_tempDir, "link.txt");
        File.WriteAllText(target, "content");
        File.CreateSymbolicLink(link, target);

        Assert.True(_sut.IsSymlink(link));
        // The target itself is NOT a symlink — this is the tightest disambiguation.
        Assert.False(_sut.IsSymlink(target));
    }

    [Fact]
    public void IsSymlink_BrokenSymlink_ReturnsTrue_OnAllPlatforms()
    {
        // The whole raison d'être of LinkRepairService is to *repair broken
        // symlinks*, so IsSymlink MUST detect them. The implementation now uses
        // File.GetAttributes + FileAttributes.ReparsePoint (rather than FileInfo.Exists +
        // LinkTarget), which inspects the link node itself and therefore behaves the same
        // on Windows and POSIX. A regression that reintroduces the old Exists-based gate
        // would fail this test on Windows.
        if (!SymlinksSupported())
        {
            return;
        }

        var target = Path.Join(_tempDir, "vanishing-target.txt");
        var link = Path.Join(_tempDir, "broken-link.txt");
        File.WriteAllText(target, "content");
        File.CreateSymbolicLink(link, target);
        File.Delete(target); // now the link is broken

        Assert.True(_sut.IsSymlink(link), "broken symlinks must still be recognised as symlinks");
    }

    [Fact]
    public void IsSymlink_RealSymlink_ReturnsTrue_And_Target_IsNot()
    {
        // This test confirms the public contract: _sut.IsSymlink(link)==true and
        // _sut.IsSymlink(target)==false for a real symlink pair. Together with
        // IsSymlink_BrokenSymlink_ReturnsTrue this ensures both sides of the two-condition
        // predicate fire correctly without testing .NET platform internals directly.
        if (!SymlinksSupported())
        {
            return;
        }

        var target = Path.Join(_tempDir, "target.txt");
        var link = Path.Join(_tempDir, "link.txt");
        File.WriteAllText(target, "content");
        File.CreateSymbolicLink(link, target);

        Assert.True(_sut.IsSymlink(link));
        Assert.False(_sut.IsSymlink(target));
    }

    /// <summary>
    ///     An empty string is rejected by File.GetAttributes with an
    ///     ArgumentException on all platforms. IsSymlink must absorb that exception and
    ///     return false rather than propagating it to the caller.
    /// </summary>
    [Fact]
    public void IsSymlink_EmptyString_ReturnsFalse_DoesNotThrow()
    {
        // string.Empty causes File.GetAttributes to throw ArgumentException
        // ("Path cannot be the empty string or all whitespace.").
        Assert.False(_sut.IsSymlink(string.Empty));
    }

    /// <summary>
    ///     A path that exceeds the OS maximum length triggers a
    ///     PathTooLongException inside File.GetAttributes. IsSymlink must absorb it and
    ///     return false.
    /// </summary>
    [Fact]
    public void IsSymlink_PathTooLong_ReturnsFalse_DoesNotThrow()
    {
        // 5 000 'a' characters is well above the MAX_PATH ceiling on every supported OS.
        var tooLong = new string('a', 5_000);
        Assert.False(_sut.IsSymlink(tooLong));
    }

    // -----------------------------------------------------------------------
    // GetSymlinkTarget
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSymlinkTarget_RegularFile_ReturnsNull()
    {
        var path = Path.Join(_tempDir, "regular.txt");
        File.WriteAllText(path, "hello");
        Assert.Null(_sut.GetSymlinkTarget(path));
    }

    [Fact]
    public void GetSymlinkTarget_NonExistentPath_ReturnsNull_DoesNotThrow()
    {
        var path = Path.Join(_tempDir, "ghost.txt");
        Assert.Null(_sut.GetSymlinkTarget(path));
    }

    /// <summary>
    ///     An empty string is rejected by FileInfo constructor / LinkTarget
    ///     accessor with an ArgumentException. GetSymlinkTarget must absorb it and return null.
    /// </summary>
    [Fact]
    public void GetSymlinkTarget_EmptyString_ReturnsNull_DoesNotThrow()
    {
        Assert.Null(_sut.GetSymlinkTarget(string.Empty));
    }

    /// <summary>
    ///     A path exceeding the OS maximum length triggers a
    ///     PathTooLongException inside FileInfo. GetSymlinkTarget must absorb it and return null.
    /// </summary>
    [Fact]
    public void GetSymlinkTarget_PathTooLong_ReturnsNull_DoesNotThrow()
    {
        var tooLong = new string('a', 5_000);
        Assert.Null(_sut.GetSymlinkTarget(tooLong));
    }

    [Fact]
    public void GetSymlinkTarget_RealSymlink_ReturnsAbsoluteOrRelativeTarget()
    {
        if (!SymlinksSupported())
        {
            return;
        }

        var target = Path.Join(_tempDir, "target.txt");
        var link = Path.Join(_tempDir, "link.txt");
        File.WriteAllText(target, "content");
        File.CreateSymbolicLink(link, target);

        var result = _sut.GetSymlinkTarget(link);
        Assert.NotNull(result);
        // The exact target string may be relative or absolute depending on OS/normalisation,
        // but it must at least name the target file.
        Assert.Contains("target.txt", result!, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // CreateSymlink
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateSymlink_ValidPaths_CreatesFunctionalSymlink()
    {
        if (!SymlinksSupported())
        {
            return;
        }

        var target = Path.Join(_tempDir, "src.txt");
        var link = Path.Join(_tempDir, "link.txt");
        File.WriteAllText(target, "payload");

        _sut.CreateSymlink(link, target);

        Assert.True(_sut.IsSymlink(link));
        Assert.Equal("payload", File.ReadAllText(link));
    }

    [Fact]
    public void CreateSymlink_LinkAlreadyExists_Throws()
    {
        if (!SymlinksSupported())
        {
            return;
        }

        var target = Path.Join(_tempDir, "src.txt");
        var link = Path.Join(_tempDir, "link.txt");
        File.WriteAllText(target, "payload");
        File.WriteAllText(link, "already here");

        Assert.ThrowsAny<IOException>(() => _sut.CreateSymlink(link, target));
    }

    // -----------------------------------------------------------------------
    // DeleteSymlink — the interesting one: the guard clause must fail loudly
    // on non-symlinks so we never accidentally delete a real file.
    // -----------------------------------------------------------------------

    [Fact]
    public void DeleteSymlink_ActualSymlink_RemovesLinkButNotTarget()
    {
        // A naive implementation could follow the link and delete the target.
        // The contract is: delete only the link, target survives.
        if (!SymlinksSupported())
        {
            return;
        }

        var target = Path.Join(_tempDir, "target.txt");
        var link = Path.Join(_tempDir, "link.txt");
        File.WriteAllText(target, "important");
        File.CreateSymbolicLink(link, target);

        _sut.DeleteSymlink(link);

        Assert.False(File.Exists(link));
        Assert.True(File.Exists(target), "DeleteSymlink must not touch the target file.");
        Assert.Equal("important", File.ReadAllText(target));
    }

    [Fact]
    public void DeleteSymlink_RegularFile_ThrowsInvalidOperationException_AndDoesNotDeleteFile()
    {
        // BUG GUARD: the helper must refuse to delete a non-symlink path. Without this
        // guard, a mis-routed cleanup call could silently wipe a real file.
        var path = Path.Join(_tempDir, "regular.txt");
        File.WriteAllText(path, "irreplaceable");

        var ex = Assert.Throws<InvalidOperationException>(() => _sut.DeleteSymlink(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a symbolic link", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Crucially: the file survives the failed call.
        Assert.True(File.Exists(path));
        Assert.Equal("irreplaceable", File.ReadAllText(path));
    }

    [Fact]
    public void DeleteSymlink_NonExistentPath_ThrowsInvalidOperationException()
    {
        // A missing path is treated as "not a symlink" (IsSymlink → false), which is safer
        // than swallowing silently: the caller learns that their target was not what they
        // thought it was.
        var path = Path.Join(_tempDir, "ghost.txt");
        Assert.Throws<InvalidOperationException>(() => _sut.DeleteSymlink(path));
    }
}