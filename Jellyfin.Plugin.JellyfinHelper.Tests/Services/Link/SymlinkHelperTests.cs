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

    [Fact]
    public void DeleteSymlink_DirectorySymlink_RemovesLinkButNotTarget()
    {
        // Verifies the FileAttributes.Directory branch works properly and uses Directory.Delete
        if (!SymlinksSupported())
        {
            return;
        }

        var targetDir = Path.Join(_tempDir, "target-dir");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Join(targetDir, "keep.txt"), "safe");

        var linkDir = Path.Join(_tempDir, "link-dir");
        File.CreateSymbolicLink(linkDir, targetDir);

        _sut.DeleteSymlink(linkDir);

        Assert.False(Directory.Exists(linkDir), "The symlink directory node must be deleted.");
        Assert.True(Directory.Exists(targetDir), "The target directory must survive.");
        Assert.True(File.Exists(Path.Join(targetDir, "keep.txt")), "Contents inside the target directory must survive.");
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

    // -----------------------------------------------------------------------
    // Concurrent-replacement (TOCTOU) branches driven deterministically via
    // filesystem seams. These paths previously carried [ExcludeFromCodeCoverage]
    // because a real move race cannot be provoked reliably; the seams let a test
    // subclass simulate each race outcome exactly.
    // -----------------------------------------------------------------------

    /// <summary>
    ///     A <see cref="SymlinkHelper" /> whose filesystem primitives are scripted so the TOCTOU
    ///     branches of <see cref="SymlinkHelper.ReplaceSymlink" /> and
    ///     <see cref="SymlinkHelper.DeleteSymlink" /> run without any real symlink or move race.
    /// </summary>
    private sealed class ScriptedSymlinkHelper : SymlinkHelper
    {
        // Queue of attribute results returned by successive GetAttributes calls. A null entry means
        // "throw FileNotFoundException" (the entry vanished); otherwise the FileAttributes are returned.
        private readonly Queue<FileAttributes?> _attributeScript = new();
        private bool _firstMoveThrowsIoException;

        public int MoveFileCalls { get; private set; }

        public int MoveFileOverwriteCalls { get; private set; }

        public bool GetAttributesThrowsUnauthorized { get; init; }

        public void QueueAttributes(params FileAttributes?[] results)
        {
            foreach (var r in results)
            {
                _attributeScript.Enqueue(r);
            }
        }

        public void MakeFirstMoveFailAsExists() => _firstMoveThrowsIoException = true;

        internal override FileAttributes GetAttributes(string path)
        {
            if (GetAttributesThrowsUnauthorized)
            {
                throw new UnauthorizedAccessException("access denied (simulated)");
            }

            if (_attributeScript.Count == 0)
            {
                throw new InvalidOperationException("attribute script exhausted");
            }

            var next = _attributeScript.Dequeue();
            if (next is null)
            {
                throw new FileNotFoundException("entry vanished (simulated)");
            }

            return next.Value;
        }

        internal override void MoveFile(string source, string dest)
        {
            MoveFileCalls++;
            if (_firstMoveThrowsIoException && MoveFileCalls == 1)
            {
                throw new IOException("destination already exists (simulated)");
            }
        }

        internal override void MoveFileOverwrite(string source, string dest) => MoveFileOverwriteCalls++;

        internal override bool FileExists(string path) => true;

        internal override bool DirectoryExists(string path) => false;

        // Any entry the scripted attributes mark as a ReparsePoint is treated as a genuine symlink
        // (non-null LinkTarget), so IsSymlinkFromAttributes keys off the scripted attributes alone.
        internal override string? GetLinkTarget(string path) => "/some/target";
    }

    // A symlink-looking attribute set: ReparsePoint flag present. Combined with the overridden
    // GetLinkTarget above, this satisfies IsSymlinkFromAttributes so the move/re-stat control flow
    // is what the test exercises.
    private const FileAttributes SymlinkAttrs = FileAttributes.ReparsePoint;

    [Fact]
    public void ReplaceSymlink_DestVanishesBetweenMoveAndRestat_RetriesCleanMove()
    {
        // Scripted race: initial stat says "symlink", the non-overwriting move fails because the
        // destination exists, but by the re-stat the destination has vanished (FileNotFound). The
        // helper must fall back to a clean, non-overwriting move rather than an overwrite.
        var helper = new ScriptedSymlinkHelper();
        helper.QueueAttributes(SymlinkAttrs, null); // 1st stat: symlink; re-stat: vanished
        helper.MakeFirstMoveFailAsExists();

        helper.ReplaceSymlink("/tmp/source", "/tmp/dest");

        // Two non-overwriting moves (the failed first, then the clean retry); no overwrite move.
        Assert.Equal(2, helper.MoveFileCalls);
        Assert.Equal(0, helper.MoveFileOverwriteCalls);
    }

    [Fact]
    public void ReplaceSymlink_DestBecomesRealFileBetweenMoveAndRestat_ThrowsDataLoss()
    {
        // Scripted race: initial stat says "symlink", the move fails (dest exists), and the re-stat
        // now reports a NON-symlink (a real file raced into place). The helper must refuse with a
        // data-loss error and never attempt the overwriting move.
        var helper = new ScriptedSymlinkHelper();
        helper.QueueAttributes(SymlinkAttrs, FileAttributes.Normal); // symlink → real file
        helper.MakeFirstMoveFailAsExists();

        var ex = Assert.Throws<InvalidOperationException>(
            () => helper.ReplaceSymlink("/tmp/source", "/tmp/dest"));

        Assert.Contains("became a real file", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data loss", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, helper.MoveFileOverwriteCalls); // never overwrote the real file
    }

    [Fact]
    public void ReplaceSymlink_DestStillSymlinkAfterFailedMove_UsesOverwriteMove()
    {
        // Scripted race: initial stat says "symlink", the non-overwriting move fails (dest exists),
        // and the re-stat confirms it is STILL a symlink. The helper must complete via the
        // single-syscall overwriting move.
        var helper = new ScriptedSymlinkHelper();
        helper.QueueAttributes(SymlinkAttrs, SymlinkAttrs); // symlink both times
        helper.MakeFirstMoveFailAsExists();

        helper.ReplaceSymlink("/tmp/source", "/tmp/dest");

        Assert.Equal(1, helper.MoveFileCalls);           // the single failed non-overwriting attempt
        Assert.Equal(1, helper.MoveFileOverwriteCalls);  // resolved via overwrite
    }

    [Fact]
    public void DeleteSymlink_GetAttributesThrowsAccessError_ThrowsInspectionFailure()
    {
        // When the attribute read itself fails (permission denied / IO error), the helper must NOT
        // claim "not a symbolic link" — that would send an operator investigating the wrong cause.
        // It reports an inspection failure and refuses to delete the unverified entry.
        var helper = new ScriptedSymlinkHelper { GetAttributesThrowsUnauthorized = true };

        var ex = Assert.Throws<InvalidOperationException>(() => helper.DeleteSymlink("/tmp/whatever"));

        Assert.Contains("could not be inspected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnauthorizedAccessException", ex.Message, StringComparison.Ordinal);
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
    }
}