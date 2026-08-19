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
    ///         and the test reports as <b>passed</b> (not skipped) - xUnit 2.9 has no runtime
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
    ///     know the probe itself is broken - before every other symlink test silently
    ///     degenerates to "passed by skipping".
    /// </summary>
    [Fact]
    public void SymlinkProbe_MustExecuteAtLeastOnceInLinuxCi()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows CI without Developer Mode legitimately cannot create symlinks -
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
    // IsSymlink - must never throw on non-existent / permission-denied paths.
    // -----------------------------------------------------------------------

    [Fact]
    public void IsSymlink_NonExistentPath_ReturnsFalse_DoesNotThrow()
    {
        // An early implementation used FileInfo(...).LinkTarget directly
        // without an Exists guard, which threw on missing paths - turning a routine
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
        // FileInfo pointed at a directory has Exists=false - the helper must handle this
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
            return; // skip - environment can't create symlinks
        }

        var target = Path.Join(_tempDir, "target.txt");
        var link = Path.Join(_tempDir, "link.txt");
        File.WriteAllText(target, "content");
        File.CreateSymbolicLink(link, target);

        Assert.True(_sut.IsSymlink(link));
        // The target itself is NOT a symlink - this is the tightest disambiguation.
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateSymlink_NullOrEmptyLinkPath_ThrowsArgumentException(string? linkPath)
    {
        // Platform-independent precondition guard (no symlink support required).
        var ex = Assert.Throws<ArgumentException>(() => _sut.CreateSymlink(linkPath!, "/some/target"));
        Assert.Equal("linkPath", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateSymlink_NullOrEmptyTargetPath_ThrowsArgumentException(string? targetPath)
    {
        var link = Path.Join(_tempDir, "link.txt");
        var ex = Assert.Throws<ArgumentException>(() => _sut.CreateSymlink(link, targetPath!));
        Assert.Equal("targetPath", ex.ParamName);
    }

    [Fact]
    public void CreateSymlink_LinkPathIsExistingRealFile_ThrowsIOException()
    {
        // A real file at the link path must never be silently clobbered — no symlink support needed.
        var link = Path.Join(_tempDir, "already-a-file.txt");
        File.WriteAllText(link, "real content");

        Assert.Throws<IOException>(() => _sut.CreateSymlink(link, Path.Join(_tempDir, "target.txt")));
        Assert.Equal("real content", File.ReadAllText(link)); // untouched
    }

    [Fact]
    public void CreateSymlink_LinkPathIsExistingDirectory_ThrowsIOException()
    {
        var link = Path.Join(_tempDir, "already-a-dir");
        Directory.CreateDirectory(link);

        Assert.Throws<IOException>(() => _sut.CreateSymlink(link, Path.Join(_tempDir, "target.txt")));
        Assert.True(Directory.Exists(link)); // untouched
    }

    // -----------------------------------------------------------------------
    // DeleteSymlink - the interesting one: the guard clause must fail loudly
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

    // ReplaceSymlink - TOCTOU data-loss guard (audit finding link-service-1)

    [Fact]
    public void ReplaceSymlink_DestIsRealFile_ThrowsAndDoesNotOverwrite()
    {
        // DATA-LOSS GUARD: if a REAL media file has replaced the symlink at destPath since the scan
        // (e.g. an import wrote the finished download there), the repair must REFUSE - overwriting it
        // with the temp symlink would destroy the bytes irreversibly. The real file must be untouched.
        if (!SymlinksSupported())
        {
            return;
        }

        var newTarget = Path.Join(_tempDir, "new-target.mkv");
        File.WriteAllText(newTarget, "the real new target");
        var source = Path.Join(_tempDir, "repair.jfh-tmp");
        _sut.CreateSymlink(source, newTarget);

        var dest = Path.Join(_tempDir, "special.mkv");
        File.WriteAllText(dest, "REAL MEDIA BYTES"); // a real file now sits where the symlink was

        Assert.Throws<InvalidOperationException>(() => _sut.ReplaceSymlink(source, dest));

        // The real file survived intact and was NOT turned into a symlink.
        Assert.Equal("REAL MEDIA BYTES", File.ReadAllText(dest));
        Assert.False(_sut.IsSymlink(dest), "a real file must never be replaced by the repair symlink");
        Assert.True(_sut.IsSymlink(source), "the temp symlink is left for the caller's cleanup");
    }

    [Fact]
    public void ReplaceSymlink_DestIsBrokenSymlink_RepairsSuccessfully()
    {
        // The LEGITIMATE repair case (renamed target): the symlink at destPath is broken because its
        // target moved. destPath is still a symbolic link, so the guard allows the move and the link
        // is repaired to the new target. This is the primary flow the feature exists for and must
        // NOT be blocked by the data-loss guard.
        if (!SymlinksSupported())
        {
            return;
        }

        var oldTarget = Path.Join(_tempDir, "old-name.mkv");
        var dest = Path.Join(_tempDir, "special.mkv");
        _sut.CreateSymlink(dest, oldTarget); // oldTarget never created → broken symlink
        Assert.True(_sut.IsSymlink(dest), "precondition: dest is a (broken) symlink");

        var newTarget = Path.Join(_tempDir, "new-name.mkv");
        File.WriteAllText(newTarget, "renamed target");
        var source = Path.Join(_tempDir, "repair.jfh-tmp");
        _sut.CreateSymlink(source, newTarget);

        _sut.ReplaceSymlink(source, dest);

        Assert.True(_sut.IsSymlink(dest), "dest is still a symlink after repair");
        Assert.Equal(newTarget, new FileInfo(dest).LinkTarget);
        Assert.False(File.Exists(source), "source moved into place");
    }

    [Fact]
    public void ReplaceSymlink_DestDoesNotExist_MovesSuccessfully()
    {
        // Nothing at destPath → nothing to lose → the move proceeds and creates the link.
        if (!SymlinksSupported())
        {
            return;
        }

        var newTarget = Path.Join(_tempDir, "target.mkv");
        File.WriteAllText(newTarget, "target");
        var source = Path.Join(_tempDir, "repair.jfh-tmp");
        _sut.CreateSymlink(source, newTarget);
        var dest = Path.Join(_tempDir, "brand-new.mkv"); // does not exist

        _sut.ReplaceSymlink(source, dest);

        Assert.True(_sut.IsSymlink(dest));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void ReplaceSymlink_DestIsRealFile_MessageMentionsDataLoss()
    {
        if (!SymlinksSupported())
        {
            return;
        }

        var newTarget = Path.Join(_tempDir, "t.mkv");
        File.WriteAllText(newTarget, "t");
        var source = Path.Join(_tempDir, "r.jfh-tmp");
        _sut.CreateSymlink(source, newTarget);
        var dest = Path.Join(_tempDir, "real.mkv");
        File.WriteAllText(dest, "real");

        var ex = Assert.Throws<InvalidOperationException>(() => _sut.ReplaceSymlink(source, dest));
        Assert.Contains(dest, ex.Message, StringComparison.Ordinal);
        Assert.Contains("data loss", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplaceSymlink_DestVanished_MovesRegularFileSourceIntoPlace()
    {
        // The scan-time dest was removed before the repair ran. File.GetAttributes throws
        // FileNotFoundException, the catch treats it as "nothing to lose", and the source is
        // moved into place. Uses plain files so it needs no symlink privileges and covers the
        // catch/early-move branch the symlink-gated DestDoesNotExist test skips on Windows.
        var source = Path.Join(_tempDir, "repair-source.txt");
        File.WriteAllText(source, "moved payload");
        var dest = Path.Join(_tempDir, "gone.txt"); // never created → vanished

        _sut.ReplaceSymlink(source, dest);

        Assert.True(File.Exists(dest));
        Assert.Equal("moved payload", File.ReadAllText(dest));
        Assert.False(File.Exists(source), "the source must be moved, not copied");
    }

    [Fact]
    public void ReplaceSymlink_DestIsRealFile_ThrowsBeforeAnyMove_OnAllPlatforms()
    {
        // DATA-LOSS GUARD, privilege-free variant: both source and dest are plain files, so the
        // guard throws before File.Move without needing any symlink. The symlink-based
        // DestIsRealFile test is skipped on the Windows coverage host, leaving this branch
        // uncovered there. The throw must happen before the move so both files stay intact.
        var source = Path.Join(_tempDir, "repair-source.txt");
        File.WriteAllText(source, "repair payload");
        var dest = Path.Join(_tempDir, "real-media.txt");
        File.WriteAllText(dest, "REAL MEDIA BYTES");

        var ex = Assert.Throws<InvalidOperationException>(() => _sut.ReplaceSymlink(source, dest));

        Assert.Contains(dest, ex.Message, StringComparison.Ordinal);
        Assert.Contains("no longer a symbolic link", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Proof the throw preceded the move: both files still exist with original bytes.
        Assert.Equal("REAL MEDIA BYTES", File.ReadAllText(dest));
        Assert.Equal("repair payload", File.ReadAllText(source));
    }
}