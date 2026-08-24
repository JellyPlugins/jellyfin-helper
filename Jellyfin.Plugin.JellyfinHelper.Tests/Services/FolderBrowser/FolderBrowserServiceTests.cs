using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FolderBrowser;

/// <summary>
///     Tests for <see cref="FolderBrowserService" />.
/// </summary>
public sealed class FolderBrowserServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FolderBrowserService _service;

    public FolderBrowserServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "jfh-fb-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _service = new FolderBrowserService(TestMockFactory.CreateLogger<FolderBrowserService>().Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ===== Constructor =====

    [Fact]
    public void Constructor_AllowsAnyLogger_WithoutThrowing()
    {
        var enabled = TestMockFactory.CreateLogger<FolderBrowserService>().Object;
        var disabled = TestMockFactory.CreateDisabledLogger<FolderBrowserService>().Object;

        // Constructor must not throw for either logger variant.
        var withEnabled = Record.Exception(() => new FolderBrowserService(enabled));
        var withDisabled = Record.Exception(() => new FolderBrowserService(disabled));

        Assert.Null(withEnabled);
        Assert.Null(withDisabled);
    }

    // ===== GetRoots =====

    [Fact]
    public void GetRoots_ReturnsSuccessfulResult_WithNoErrorAndCannotGoUp()
    {
        var result = _service.GetRoots();

        Assert.Null(result.Error);
        Assert.Null(result.CurrentPath);
        Assert.Null(result.ParentPath);
        Assert.False(result.CanGoUp);
        Assert.NotNull(result.Directories);
        Assert.NotEmpty(result.Directories);
    }

    [Fact]
    public void GetRoots_EveryEntryHasNonEmptyPathAndName()
    {
        var result = _service.GetRoots();

        Assert.All(result.Directories, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Path));
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
        });
    }

    [Fact]
    public void GetRoots_EntriesAreSortedByNameCaseInsensitively()
    {
        var result = _service.GetRoots();
        var names = result.Directories.Select(d => d.Name).ToList();
        var expected = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(expected, names);
    }

    [Fact]
    public void GetRoots_OnLinuxOrMac_ReturnsExactlyOneRootSlashEntry()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = _service.GetRoots();
        var entry = Assert.Single(result.Directories);
        Assert.Equal("/", entry.Name);
        Assert.Equal("/", entry.Path);
    }

    [Fact]
    public void GetRoots_OnWindows_ReturnsFullyQualifiedDrivePaths()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = _service.GetRoots();
        Assert.NotEmpty(result.Directories);
        Assert.All(result.Directories, e => Assert.True(Path.IsPathFullyQualified(e.Path)));
    }

    [Fact]
    public void GetRoots_DisabledLogger_StillWorks()
    {
        var svc = new FolderBrowserService(TestMockFactory.CreateDisabledLogger<FolderBrowserService>().Object);
        var result = svc.GetRoots();
        Assert.Null(result.Error);
        Assert.NotEmpty(result.Directories);
    }

    // ===== ValidatePath: empty / whitespace / null =====

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ValidatePath_EmptyOrWhitespace_ReturnsEmptyError(string path)
        => Assert.Equal("Path must not be empty.", _service.ValidatePath(path));

    [Fact]
    public void ValidatePath_Null_ReturnsEmptyError()
        => Assert.Equal("Path must not be empty.", _service.ValidatePath(null!));

    // ===== ValidatePath: path traversal =====

    [Fact]
    public void ValidatePath_ContainsDotDotSegment_ReturnsTraversalError()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\foo\..\bar" : "/foo/../bar";
        Assert.Equal("Path must not contain '..' sequences.", _service.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_TrailingDotDotSegment_ReturnsTraversalError()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\foo\.." : "/foo/..";
        Assert.Equal("Path must not contain '..' sequences.", _service.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_MultipleDotDotSegments_ReturnsTraversalError()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\a\..\b\..\c" : "/a/../b/../c";
        Assert.Equal("Path must not contain '..' sequences.", _service.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_LeadingDotDot_IsRejected()
    {
        var result = _service.ValidatePath("../foo");
        Assert.NotNull(result);
        Assert.Contains(result!, new[]
        {
            "Path must not contain '..' sequences.",
            "Path must be absolute."
        });
    }

    [Fact]
    public void ValidatePath_NameContainingDotsButNotTraversal_IsAllowed()
    {
        var folder = Path.Combine(_tempRoot, "my..folder");
        Directory.CreateDirectory(folder);
        Assert.Null(_service.ValidatePath(folder));
    }

    [Fact]
    public void ValidatePath_NameStartingWithMultipleDots_IsAllowed()
    {
        var folder = Path.Combine(_tempRoot, "...secret");
        Directory.CreateDirectory(folder);
        Assert.Null(_service.ValidatePath(folder));
    }

    [Fact]
    public void ValidatePath_SingleDotSegment_IsAllowed()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "sub"));
        var path = Path.Combine(_tempRoot, "sub", ".");
        Assert.Null(_service.ValidatePath(path));
    }

    // ===== ValidatePath: null bytes =====

    [Fact]
    public void ValidatePath_ContainsNullByte_ReturnsInvalidCharsError()
    {
        var path = _tempRoot + "\0evil";
        Assert.Equal("Path contains invalid characters.", _service.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_NullByteInMiddle_ReturnsInvalidCharsError()
    {
        var path = _tempRoot.Insert(_tempRoot.Length / 2, "\0");
        Assert.Equal("Path contains invalid characters.", _service.ValidatePath(path));
    }

    // ===== ValidatePath: absolute vs relative =====

    [Theory]
    [InlineData("relative/path")]
    [InlineData("foo")]
    [InlineData("./local")]
    public void ValidatePath_RelativePath_ReturnsAbsoluteError(string path)
        => Assert.Equal("Path must be absolute.", _service.ValidatePath(path));

    [Fact]
    public void ValidatePath_DriveRelativePath_ReturnsAbsoluteError()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("Path must be absolute.", _service.ValidatePath("C:temp"));
    }

    // ===== ValidatePath: sensitive system directories are refused =====

    [Fact]
    public void ValidatePath_SensitiveSystemDir_IsRefused_Posix()
    {
        // On Linux/macOS, well-known system + Jellyfin app dirs must be refused with the
        // protected-folder message (never browsed into or selected).
        if (OperatingSystem.IsWindows()) return;
        foreach (var sensitive in new[] { "/etc", "/config", "/data", "/var", "/proc", "/etc/ssl" })
        {
            Assert.Equal(
                "This is a protected system folder and cannot be browsed.",
                _service.ValidatePath(sensitive));
        }
    }

    [Fact]
    public void ValidatePath_SensitiveSystemDir_IsRefused_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var sensitive in new[] { @"C:\Windows", @"C:\Windows\System32", @"C:\Program Files" })
        {
            Assert.Equal(
                "This is a protected system folder and cannot be browsed.",
                _service.ValidatePath(sensitive));
        }
    }

    [Fact]
    public void ValidatePath_SensitiveCheckPrecedesExistence()
    {
        // The sensitive-path refusal must fire regardless of whether the dir exists on the
        // test host - /config need not exist on a Windows dev box for the guard to apply.
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(
            "This is a protected system folder and cannot be browsed.",
            _service.ValidatePath("/config/data/plugins"));
    }

    // ===== ValidatePath: existence / kind =====

    [Fact]
    public void ValidatePath_NonExistentDirectory_ReturnsDoesNotExistError()
    {
        var path = Path.Combine(_tempRoot, "does-not-exist-" + Guid.NewGuid());
        Assert.Equal("Directory does not exist.", _service.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_DeepNonExistentPath_ReturnsDoesNotExistError()
    {
        var path = Path.Combine(_tempRoot, "a", "b", "c", "d", "gone");
        Assert.Equal("Directory does not exist.", _service.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_ValidExistingDirectory_ReturnsNull()
        => Assert.Null(_service.ValidatePath(_tempRoot));

    [Fact]
    public void ValidatePath_ValidNestedDirectory_ReturnsNull()
    {
        var nested = Path.Combine(_tempRoot, "a", "b", "c");
        Directory.CreateDirectory(nested);
        Assert.Null(_service.ValidatePath(nested));
    }

    [Fact]
    public void ValidatePath_PathPointsToFile_ReturnsMustBeDirectoryError()
    {
        var filePath = Path.Combine(_tempRoot, "file.txt");
        File.WriteAllText(filePath, "hello");
        Assert.Equal("Path must point to a directory.", _service.ValidatePath(filePath));
    }

    [Fact]
    public void ValidatePath_TrailingSeparator_IsAllowed()
    {
        var pathWithSep = _tempRoot + Path.DirectorySeparatorChar;
        Assert.Null(_service.ValidatePath(pathWithSep));
    }

    [Fact]
    public void ValidatePath_UnicodeCharactersInName_IsAllowed()
    {
        var folder = Path.Combine(_tempRoot, "München-测试-🎬");
        Directory.CreateDirectory(folder);
        Assert.Null(_service.ValidatePath(folder));
    }

    [Fact]
    public void ValidatePath_DisabledLogger_ExercisesFalseBranchOnError()
    {
        // Feed a genuinely invalid path shape to exercise the outer catch that logs at Debug.
        // With a disabled logger we should still return "Invalid path." without throwing.
        var svc = new FolderBrowserService(TestMockFactory.CreateDisabledLogger<FolderBrowserService>().Object);
        var bogus = OperatingSystem.IsWindows() ? @"C:\" + new string('a', 32800) : "/" + new string('a', 32800);

        var result = svc.ValidatePath(bogus);
        Assert.NotNull(result);
    }

    // ===== GetChildren: input validation =====

    [Fact]
    public void GetChildren_EmptyPath_ReturnsValidationError()
    {
        var result = _service.GetChildren("");
        Assert.Equal("Path must not be empty.", result.Error);
        Assert.Empty(result.Directories);
        Assert.Null(result.CurrentPath);
    }

    [Fact]
    public void GetChildren_NullPath_ReturnsValidationError()
    {
        var result = _service.GetChildren(null!);
        Assert.Equal("Path must not be empty.", result.Error);
    }

    [Fact]
    public void GetChildren_DotDotInPath_ReturnsTraversalError()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\foo\..\bar" : "/foo/../bar";
        var result = _service.GetChildren(path);
        Assert.Equal("Path must not contain '..' sequences.", result.Error);
    }

    [Fact]
    public void GetChildren_NullBytePath_ReturnsInvalidCharsError()
    {
        var result = _service.GetChildren(_tempRoot + "\0evil");
        Assert.Equal("Path contains invalid characters.", result.Error);
    }

    [Fact]
    public void GetChildren_RelativePath_ReturnsAbsoluteError()
    {
        var result = _service.GetChildren("relative/foo");
        Assert.Equal("Path must be absolute.", result.Error);
    }

    // ===== GetChildren: non-existent =====

    [Fact]
    public void GetChildren_NonExistentPath_ReturnsError()
    {
        var path = Path.Combine(_tempRoot, "missing-" + Guid.NewGuid());
        var result = _service.GetChildren(path);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void GetChildren_PathPointsToFile_ReturnsError()
    {
        var filePath = Path.Combine(_tempRoot, "file.txt");
        File.WriteAllText(filePath, "x");
        var result = _service.GetChildren(filePath);
        Assert.NotNull(result.Error);
    }

    // ===== GetChildren: happy paths =====

    [Fact]
    public void GetChildren_EmptyDirectory_ReturnsEmptyListWithCanGoUp()
    {
        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        Assert.Equal(Path.GetFullPath(_tempRoot), result.CurrentPath);
        Assert.NotNull(result.Directories);
        Assert.Empty(result.Directories);
        Assert.True(result.CanGoUp);
        Assert.NotNull(result.ParentPath);
    }

    [Fact]
    public void GetChildren_WithSubdirectories_ReturnsThemSortedCaseInsensitive()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "zebra"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Alpha"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "middle"));

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Directories.Count);
        Assert.Equal("Alpha", result.Directories[0].Name);
        Assert.Equal("middle", result.Directories[1].Name);
        Assert.Equal("zebra", result.Directories[2].Name);
    }

    [Fact]
    public void GetChildren_SubdirectoryWithChildren_HasChildrenTrue()
    {
        var parent = Path.Combine(_tempRoot, "parent");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, "child"));

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.Equal("parent", entry.Name);
        Assert.True(entry.HasChildren);
    }

    [Fact]
    public void GetChildren_SubdirectoryWithoutChildren_HasChildrenFalse()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "leaf"));

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.False(entry.HasChildren);
    }

    [Fact]
    public void GetChildren_SubdirectoryWithOnlyFiles_HasChildrenFalse()
    {
        var parent = Path.Combine(_tempRoot, "parent");
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "a.txt"), "");
        File.WriteAllText(Path.Combine(parent, "b.txt"), "");

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.False(entry.HasChildren);
    }

    [Fact]
    public void GetChildren_FilesInDirectory_AreNotIncluded()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "a.txt"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "b.txt"), "");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "sub"));

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.Equal("sub", entry.Name);
    }

    [Fact]
    public void GetChildren_ParentPath_IsSetCorrectly()
    {
        var sub = Path.Combine(_tempRoot, "sub");
        Directory.CreateDirectory(sub);

        var result = _service.GetChildren(sub);

        Assert.Equal(Path.GetFullPath(_tempRoot), result.ParentPath);
        Assert.True(result.CanGoUp);
    }

    [Fact]
    public void GetChildren_EntryPathIsAbsoluteAndFull()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "child"));
        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.True(Path.IsPathFullyQualified(entry.Path));
        Assert.EndsWith("child", entry.Path);
    }

    [Fact]
    public void GetChildren_ManySubdirectories_AllReturned()
    {
        var names = Enumerable.Range(0, 50).Select(i => $"dir_{i:D2}").ToArray();
        foreach (var n in names)
        {
            Directory.CreateDirectory(Path.Combine(_tempRoot, n));
        }

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        Assert.Equal(names.Length, result.Directories.Count);
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                     result.Directories.Select(d => d.Name));
    }

    [Fact]
    public void GetChildren_UnicodeSubdirectory_IsReturned()
    {
        var folderName = "München-测试-🎬";
        Directory.CreateDirectory(Path.Combine(_tempRoot, folderName));

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.Equal(folderName, entry.Name);
    }

    [Fact]
    public void GetChildren_HiddenNonSystemDirectory_IsReturned()
    {
        // On Linux/macOS, dot-dirs are filtered unless they match SafeHiddenPrefixes.
        // This test covers the Windows-only path where only Hidden+System dirs are filtered.
        if (!OperatingSystem.IsWindows()) return;

        var hidden = Path.Combine(_tempRoot, ".hidden-normal");
        Directory.CreateDirectory(hidden);

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.Equal(".hidden-normal", entry.Name);
    }

    [Fact]
    public void GetChildren_DisabledLogger_StillReturnsResult()
    {
        var svc = new FolderBrowserService(TestMockFactory.CreateDisabledLogger<FolderBrowserService>().Object);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "child"));

        var result = svc.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        Assert.Single(result.Directories);
    }

    [Fact]
    public void GetChildren_TrailingSeparator_IsHandledCorrectly()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "sub"));
        var pathWithSep = _tempRoot + Path.DirectorySeparatorChar;

        var result = _service.GetChildren(pathWithSep);

        Assert.Null(result.Error);
        Assert.Single(result.Directories);
    }

    // ===== GetChildren: filesystem root behavior =====

    [Fact]
    public void GetChildren_FilesystemRoot_CanGoUpIsFalseAndParentIsNull()
    {
        // On Linux/macOS, "/" is the root; on Windows we use whatever root the temp dir is on.
        var root = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(_tempRoot)!
            : "/";

        var result = _service.GetChildren(root);

        Assert.Null(result.Error);
        Assert.False(result.CanGoUp);
        Assert.Null(result.ParentPath);
    }

    // ===== GetChildren: mixed content =====

    [Fact]
    public void GetChildren_MixOfDirectoriesAndFiles_OnlyDirsReturned()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "d1"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "d2"));
        File.WriteAllText(Path.Combine(_tempRoot, "f1.dat"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "f2.dat"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "f3.dat"), "");

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Directories.Count);
        Assert.All(result.Directories, e => Assert.StartsWith("d", e.Name));
    }

    [Fact]
    public void GetChildren_SubdirectoryContainingOnlyFileAndEmptySubdir_HasChildrenTrue()
    {
        // HasChildren means "there is at least one visible child DIRECTORY", regardless of files.
        var parent = Path.Combine(_tempRoot, "parent");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, "sub"));
        File.WriteAllText(Path.Combine(parent, "somefile.txt"), "");

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.True(entry.HasChildren);
    }

    // ===== GetChildren: symlink handling =====

    [Fact]
    public void GetChildren_SymlinkToDirectory_IsListedIfSupportedByOs()
    {
        // Creating symlinks may require elevation on Windows. Skip gracefully if not supported.
        var target = Path.Combine(_tempRoot, "target");
        var link = Path.Combine(_tempRoot, "link");
        Directory.CreateDirectory(target);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException)
        {
            return; // symlink creation not permitted
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Directories.Count);
        Assert.Contains(result.Directories, d => d.Name == "target");
        Assert.Contains(result.Directories, d => d.Name == "link");
    }

    [Fact]
    public void GetChildren_BrokenSymlink_DoesNotAbortListing()
    {
        // A dangling symlink target should not crash the listing - other siblings must still show up.
        var validSibling = Path.Combine(_tempRoot, "valid");
        var brokenLink = Path.Combine(_tempRoot, "broken-link");
        Directory.CreateDirectory(validSibling);

        try
        {
            Directory.CreateSymbolicLink(brokenLink, Path.Combine(_tempRoot, "missing-target"));
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        Assert.Contains(result.Directories, d => d.Name == "valid");
        // "broken-link" may or may not be listed depending on how the runtime probes it;
        // the invariant we care about is that "valid" is not silently dropped.
    }

    // ===== GetChildren: idempotency =====

    [Fact]
    public void GetChildren_CalledTwice_ReturnsSameContentButDifferentInstances()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "x"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "y"));

        var a = _service.GetChildren(_tempRoot);
        var b = _service.GetChildren(_tempRoot);

        Assert.Equal(a.Directories.Count, b.Directories.Count);
        Assert.Equal(
            a.Directories.Select(d => d.Name),
            b.Directories.Select(d => d.Name));
        Assert.False(ReferenceEquals(a, b));
        Assert.False(ReferenceEquals(a.Directories, b.Directories));
    }

    // ===== GetChildren: normalization =====

    [Fact]
    public void GetChildren_UnnormalizedButValidPath_NormalizesCurrentPath()
    {
        // Path with redundant separators should be normalized in the returned CurrentPath.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "sub"));
        var doubled = _tempRoot + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar;

        var result = _service.GetChildren(doubled);

        Assert.Null(result.Error);
        Assert.Equal(Path.GetFullPath(doubled), result.CurrentPath);
    }

    // ===== ValidatePath: extra edge =====

    [Fact]
    public void ValidatePath_UncStylePathThatDoesNotResolve_ReturnsAnError()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Non-existent UNC path - the exact error message can be one of several depending on
        // network config, but it must never be null.
        var result = _service.ValidatePath(@"\\this-share-does-not-exist-xyz\share");

        Assert.NotNull(result);
    }

    // ===================================================================
    // Cross-platform GetRoots: force the Unix branch on any host by using
    // the internal test-only constructor overload.
    // ===================================================================

    [Fact]
    public void GetRoots_ForcedUnixBranch_ReturnsSingleSlashEntry()
    {
        var svc = new FolderBrowserService(
            TestMockFactory.CreateLogger<FolderBrowserService>().Object,
            isWindows: false);

        var result = svc.GetRoots();

        Assert.Null(result.Error);
        var entry = Assert.Single(result.Directories);
        Assert.Equal("/", entry.Name);
        Assert.Equal("/", entry.Path);
        Assert.False(result.CanGoUp);
        Assert.Null(result.CurrentPath);
        Assert.Null(result.ParentPath);
    }

    [Fact]
    public void GetRoots_ForcedUnixBranch_DisabledLogger_StillWorks()
    {
        var svc = new FolderBrowserService(
            TestMockFactory.CreateDisabledLogger<FolderBrowserService>().Object,
            isWindows: false);

        var result = svc.GetRoots();

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Directories);
    }

    [Fact]
    public void GetRoots_ForcedWindowsBranch_OnAnyHost_ProducesFullyQualifiedDrivePaths()
    {
        // The Windows branch inside FolderBrowserService relies on Win32 semantics of
        // DriveInfo (drive letters, VolumeLabel, drive type). On non-Windows hosts this
        // path can silently degrade to an empty result set, which means Assert.All would
        // trivially pass without ever exercising the branch. Only assert the strong
        // contract on Windows; otherwise still cover the "no throw + shape correct" path.
        var svc = new FolderBrowserService(
            TestMockFactory.CreateLogger<FolderBrowserService>().Object,
            isWindows: true);

        var result = svc.GetRoots();

        // Shape guarantees hold on every OS:
        Assert.Null(result.Error);
        Assert.False(result.CanGoUp);

        if (OperatingSystem.IsWindows())
        {
            // On Windows the enumeration must produce at least one drive entry, and
            // every path must be fully qualified (drive letter form).
            Assert.NotEmpty(result.Directories);
            Assert.All(result.Directories, e => Assert.True(Path.IsPathFullyQualified(e.Path)));
        }
        else
        {
            // On non-Windows the Windows branch can filter everything out. Only assert
            // shape (no drives with malformed paths) - this documents that we still
            // exercise the code without falsely claiming to test drive filtering.
            Assert.All(result.Directories, e => Assert.False(string.IsNullOrEmpty(e.Path)));
        }
    }

    // ===================================================================
    // GetChildren: exercise the "!dirInfo.Exists" recovery block by
    // deleting a directory between ValidatePath and GetChildren.
    //
    // Note: we can't reliably drive this from the outside because there is
    // no delegate hook, but we can force the sequence in a single-threaded
    // way by using a DirectoryInfo whose target vanishes underneath us.
    // The cleanest reproduction is a symlink pointing at a deleted target.
    // ===================================================================

    [Fact]
    public void GetChildren_SymlinkToDeletedTarget_ReturnsDoesNotExistError()
    {
        // Create a symlink, delete its target, then browse the symlink path.
        var target = Path.Combine(_tempRoot, "target");
        var link = Path.Combine(_tempRoot, "link");
        Directory.CreateDirectory(target);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException)
        {
            return; // symlink not permitted on this host
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Directory.Delete(target);

        var result = _service.GetChildren(link);

        // The service must return an error (either "does not exist" or "cannot access"),
        // never a bogus success payload with an empty listing.
        Assert.NotNull(result.Error);
    }

    // ===================================================================
    // Access-denied path - Windows-only using ACL manipulation.
    // Exercises the "Cannot access this directory" branches in both
    // ValidatePath and GetChildren.
    // ===================================================================

    [Fact]
    public void GetChildren_AccessDeniedDirectory_ReturnsAccessError()
    {
        if (!OperatingSystem.IsWindows()) return;

        var restricted = Path.Combine(_tempRoot, "no-access");
        Directory.CreateDirectory(restricted);
        Directory.CreateDirectory(Path.Combine(restricted, "child"));

        if (!TryDenyReadAccess(restricted))
        {
            return; // ACL manipulation not permitted in this environment
        }

        try
        {
            var result = _service.GetChildren(restricted);

            // Either the validation layer rejects it (Cannot access) or the enumeration layer does.
            // Both are acceptable - the invariant is that we never leak a success payload.
            Assert.NotNull(result.Error);
        }
        finally
        {
            RestoreReadAccess(restricted);
        }
    }

    [Fact]
    public void ValidatePath_AccessDeniedDirectory_ReturnsAccessError()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Create a directory the current user cannot enter, then ask ValidatePath about it.
        var parent = Path.Combine(_tempRoot, "locked");
        Directory.CreateDirectory(parent);

        if (!TryDenyReadAccess(parent))
        {
            return;
        }

        try
        {
            var result = _service.ValidatePath(parent);

            // On most Windows environments TryDenyReadAccess blocks the current user and
            // ValidatePath returns the access error. On runners with elevated privileges
            // (e.g. SYSTEM/Administrator) the ACL denial may be bypassed, in which case
            // ValidatePath returns null (no error). Both outcomes are permitted - the
            // assertion ensures we never return a *different* unexpected error string.
            Assert.True(
                result is null or "Cannot access this directory.",
                $"Unexpected validation result: '{result}'");
        }
        finally
        {
            RestoreReadAccess(parent);
        }
    }

    /// <summary>
    /// Windows-only helper that removes the current user's read+list permission on <paramref name="path"/>.
    /// Returns <c>false</c> if the manipulation is not permitted (e.g. running as an unprivileged CI user).
    /// </summary>
    private static bool TryDenyReadAccess(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.ListDirectory |
                System.Security.AccessControl.FileSystemRights.ReadData |
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(acl);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reverses the effect of <see cref="TryDenyReadAccess"/> so the tempdir can be cleaned up.
    /// </summary>
    private static void RestoreReadAccess(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            acl.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.ListDirectory |
                System.Security.AccessControl.FileSystemRights.ReadData |
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or System.Security.SecurityException or PlatformNotSupportedException)
        {
            // Best-effort - the tempdir cleanup will still try to delete.
        }
    }

    // ===== GetChildren: POSIX dot-directory visibility contract =====

    [Fact]
    public void GetChildren_Posix_HidesUnknownDotDirectoriesButKeepsTrashPrefixes()
    {
        // On Linux/macOS unknown dot-dirs (like .ssh/.gnupg/.aws) must be hidden, but the
        // known-safe trash prefixes stay visible so admins can pick them as trash targets.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.Combine(_tempRoot, "movies"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".ssh"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".jellyfin-trash"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".Trash-1000"));

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        var names = result.Directories.Select(d => d.Name).ToList();
        Assert.Contains("movies", names);
        Assert.Contains(".jellyfin-trash", names);
        Assert.Contains(".Trash-1000", names);
        Assert.DoesNotContain(".ssh", names);
    }

    // ===== ValidatePath: enabled-logger debug branch on invalid path shape =====

    [Fact]
    public void ValidatePath_EnabledLogger_InvalidPathShape_LogsAtDebugAndReturnsInvalidPath()
    {
        // The default _service uses an enabled logger, so an invalid path shape drives the
        // outer catch through the IsEnabled(Debug)==true LogDebug branch and must still return
        // a stable error contract without propagating the exception.
        //
        // This is inherently OS-specific. An over-MAX_PATH string is rejected by
        // Path.GetFullPath on Windows (PathTooLongException BEFORE the existence probe) so the
        // outer catch fires and returns "Invalid path.". On Linux the same string is a valid
        // absolute path - GetFullPath accepts it and the length failure only surfaces later as
        // a plain IOException from the filesystem probe, which the inner catch maps to
        // "Cannot access this directory." Both are the correct contract for their platform, so
        // we assert each sharply rather than weakening to a shared, tautological check.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Invalid path.", _service.ValidatePath(@"C:\" + new string('a', 32800)));
        }
        else
        {
            Assert.Equal("Cannot access this directory.", _service.ValidatePath("/" + new string('a', 32800)));
        }
    }

    // ===== POSIX symlink-escape guard: link inside a browsed dir pointing at a sensitive target =====

    [Fact]
    public void GetChildren_Posix_SymlinkPointingAtSensitiveTarget_IsHidden()
    {
        // A directory link whose OWN name is innocuous but that resolves to a sensitive root
        // (/etc) must be filtered out of the listing by IsSystemOrHiddenCritical's reparse-point
        // resolution - otherwise browsing into it would expose /etc's contents. This exercises
        // the "resolvable link -> sensitive target" branch, distinct from the existing
        // broken/deleted-target tests which only hit the unresolvable catch.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.Combine(_tempRoot, "movies"));
        var link = Path.Combine(_tempRoot, "peek");

        try
        {
            Directory.CreateSymbolicLink(link, "/etc");
        }
        catch (IOException)
        {
            return; // symlink creation not permitted on this host
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        var names = result.Directories.Select(d => d.Name).ToList();
        Assert.Contains("movies", names);
        Assert.DoesNotContain("peek", names);
    }

    [Fact]
    public void ValidatePath_Posix_SymlinkPointingAtSensitiveTarget_IsRefused()
    {
        // ValidatePath's lexical IsSensitiveSystemPath check cannot see through a link whose
        // own path is innocuous; the ResolveLinkTarget guard must dereference the final target
        // and refuse a browse INTO a link that lands on /etc.
        if (OperatingSystem.IsWindows()) return;

        var link = Path.Combine(_tempRoot, "innocuous-link");

        try
        {
            Directory.CreateSymbolicLink(link, "/etc");
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(
            "This is a protected system folder and cannot be browsed.",
            _service.ValidatePath(link));
    }

    // ===== POSIX SafeHiddenPrefixes: case-insensitive match keeps an upper-cased trash dir visible =====

    [Fact]
    public void GetChildren_Posix_UpperCaseTrashPrefixDir_StaysVisible()
    {
        // The SafeHiddenPrefixes comparison uses StringComparison.OrdinalIgnoreCase, so an
        // upper-cased ".JELLYFIN-TRASH" must remain visible just like the lower-case form.
        // A purely-ordinal comparison would hide it, so this sharply proves the OrdinalIgnoreCase branch.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.Combine(_tempRoot, ".JELLYFIN-TRASH"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".ssh"));

        var result = _service.GetChildren(_tempRoot);

        Assert.Null(result.Error);
        var names = result.Directories.Select(d => d.Name).ToList();
        Assert.Contains(".JELLYFIN-TRASH", names);
        Assert.DoesNotContain(".ssh", names);
    }

    // ===== POSIX SafeHasSubdirectories: only-child is a hidden dot-dir => HasChildren false =====

    [Fact]
    public void GetChildren_Posix_SubdirectoryWithOnlyHiddenChild_HasChildrenFalse()
    {
        // SafeHasSubdirectories filters children through IsSystemOrHiddenCritical, so a parent
        // whose ONLY child directory is a hidden dot-dir (.ssh) must report HasChildren=false -
        // the child exists on disk but is not a *visible* subdirectory.
        if (OperatingSystem.IsWindows()) return;

        var parent = Path.Combine(_tempRoot, "parent");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, ".ssh"));

        var result = _service.GetChildren(_tempRoot);

        var entry = Assert.Single(result.Directories);
        Assert.Equal("parent", entry.Name);
        Assert.False(entry.HasChildren);
    }
}
