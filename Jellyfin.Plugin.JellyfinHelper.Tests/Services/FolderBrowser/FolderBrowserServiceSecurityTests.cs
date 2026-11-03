using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FolderBrowser;

/// <summary>
///     Security tests for FolderBrowserService.
/// </summary>
public sealed class FolderBrowserServiceSecurityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FolderBrowserService _service;

    public FolderBrowserServiceSecurityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "jfh-fb-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _service = new FolderBrowserService(TestMockFactory.CreateLogger<FolderBrowserService>().Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                // Unlink junctions without recursive mount-point cleanup or following their targets.
                foreach (var entry in new DirectoryInfo(_tempRoot).EnumerateDirectories().Where(entry => entry.LinkTarget is not null))
                {
                    Directory.Delete(entry.FullName);
                }

                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not remove test folder {0}: {1}", _tempRoot, ex.Message);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ValidatePath_SymlinkToSensitiveSystemDir_IsRefusedAsProtected()
    {
        // A directory link whose own path is innocuous but that resolves to /etc must be refused: Path.GetFullPath does not dereference the link, so the guard has to resolve the final target and re-apply the sensitive-path check.
        if (OperatingSystem.IsWindows()) return;

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

        Assert.Equal(
            "This is a protected system folder and cannot be browsed.",
            _service.ValidatePath(link));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetChildren_SymlinkToSensitiveTarget_IsHiddenFromListing()
    {
        // A child directory link pointing at a sensitive system path must be filtered out
        // of the listing while normal siblings remain visible.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.Combine(_tempRoot, "ok"));
        var sneak = Path.Combine(_tempRoot, "sneak");
        try
        {
            Directory.CreateSymbolicLink(sneak, "/etc");
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
        var names = result.Directories.Select(d => d.Name).ToList();
        Assert.Contains("ok", names);
        Assert.DoesNotContain("sneak", names);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetChildren_CyclicSymlink_IsTreatedAsUnlistableAndHidden()
    {
        // A cyclic link cannot have its target resolved; the resolve throws and the entry is
        // treated as unlistable/critical. The broken link must not abort the whole listing.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.Combine(_tempRoot, "ok"));
        var linkA = Path.Combine(_tempRoot, "linkA");
        var linkB = Path.Combine(_tempRoot, "linkB");
        try
        {
            Directory.CreateSymbolicLink(linkA, linkB);
            Directory.CreateSymbolicLink(linkB, linkA);
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
        Assert.Contains(result.Directories, d => d.Name == "ok");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ValidatePath_WindowsSymlinkToSystemRoot_IsRefusedAsProtected()
    {
        // Windows counterpart of the POSIX symlink-escape test: the link's own path is under the temp dir (lexically innocuous), but it resolves to C:\Windows.
        if (!OperatingSystem.IsWindows()) return;

        var link = Path.Combine(_tempRoot, "peek");
        try
        {
            Directory.CreateSymbolicLink(link, @"C:\Windows");
        }
        catch (IOException)
        {
            return; // creating symlinks requires privilege/developer mode; skip when forbidden
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(
            "This is a protected system folder and cannot be browsed.",
            _service.ValidatePath(link));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetChildren_WindowsCyclicSymlink_IsHiddenAndDoesNotAbortListing()
    {
        // Two mutually-referencing directory links cannot be resolved; ResolveLinkTarget throws and the entry is treated as unlistable/critical.
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.Combine(_tempRoot, "ok"));
        var linkA = Path.Combine(_tempRoot, "linkA");
        var linkB = Path.Combine(_tempRoot, "linkB");
        try
        {
            Directory.CreateSymbolicLink(linkA, linkB);
            Directory.CreateSymbolicLink(linkB, linkA);
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
        Assert.Contains(result.Directories, d => d.Name == "ok");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("/tmp/../etc")]
    [InlineData("/tmp/a/../../etc")]
    public void ValidatePath_TraversalSegments_Rejected(string path)
    {
        Assert.Equal("Path must not contain '..' sequences.", _service.ValidatePath(path));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ValidatePath_NullByte_Rejected()
    {
        Assert.Equal("Path contains invalid characters.", _service.ValidatePath("/tmp/a\0b"));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    public void ValidatePath_EmptyOrRelative_Rejected(string path)
    {
        Assert.NotNull(_service.ValidatePath(path));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetChildren_NonExistentPath_ReturnsErrorWithoutThrow()
    {
        var missing = $"{_tempRoot}{Path.DirectorySeparatorChar}does-not-exist-{Guid.NewGuid():N}";

        var result = _service.GetChildren(missing);

        Assert.NotNull(result.Error);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("existing")]
    [InlineData("existing-file")]
    [InlineData("does-not-exist")]
    [InlineData("does-not-exist/child")]
    public void GetChildren_ProtectedAncestorLink_RejectsExistingAndMissingDescendants(string suffix)
    {
        var protectedRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/etc";
        var link = Path.Join(_tempRoot, "protected-link");
        DirectoryLinkTestHelper.Create(link, protectedRoot);

        var descendant = suffix switch
        {
            "existing" => OperatingSystem.IsWindows() ? "System32" : "ssl",
            "existing-file" => OperatingSystem.IsWindows() ? "win.ini" : "passwd",
            _ => suffix,
        };
        var path = Path.Join(link, descendant);
        if (suffix == "existing")
        {
            Assert.True(Directory.Exists(path));
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        }
        else if (suffix == "existing-file")
        {
            Assert.True(File.Exists(path));
        }

        var result = _service.GetChildren(path);

        Assert.Equal("This is a protected system folder and cannot be browsed.", result.Error);
        Assert.Equal(result.Error, _service.ValidatePath(path));
        Assert.Empty(result.Directories);
        Assert.Null(result.CurrentPath);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetChildren_LinkTargetWithProtectedAncestor_IsRejectedAndHidden()
    {
        var protectedRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/etc";
        var ancestor = Path.Join(_tempRoot, "ancestor");
        var link = Path.Join(_tempRoot, "indirect");
        DirectoryLinkTestHelper.Create(ancestor, protectedRoot);
        DirectoryLinkTestHelper.Create(link, Path.Join(ancestor, OperatingSystem.IsWindows() ? "System32" : "ssl"));
        Directory.CreateDirectory(Path.Join(_tempRoot, "ok"));

        Assert.Equal("This is a protected system folder and cannot be browsed.", _service.GetChildren(link).Error);
        var listing = _service.GetChildren(_tempRoot);
        Assert.Null(listing.Error);
        Assert.DoesNotContain(listing.Directories, entry => entry.Name is "ancestor" or "indirect");
        Assert.Contains(listing.Directories, entry => entry.Name == "ok");
    }

    [Fact]
    public void GetChildren_SafeAncestorLink_PreservesBrowsePathsAndHasChildren()
    {
        var target = Path.Join(_tempRoot, "target");
        Directory.CreateDirectory(Path.Join(target, "child", "grandchild", "leaf"));
        var link = Path.Join(_tempRoot, "alias");
        DirectoryLinkTestHelper.Create(link, target);
        var requested = Path.Join(link, "child");

        var result = _service.GetChildren(requested);

        Assert.Null(result.Error);
        Assert.Equal(requested, result.CurrentPath);
        Assert.Equal(link, result.ParentPath);
        var entry = Assert.Single(result.Directories);
        Assert.Equal(Path.Join(requested, "grandchild"), entry.Path);
        Assert.True(entry.HasChildren);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetChildren_CyclicAncestorLink_FailsClosed()
    {
        var link = Path.Join(_tempRoot, "cycle");
        DirectoryLinkTestHelper.Create(link, link);

        var result = _service.GetChildren(Path.Join(link, "child"));

        Assert.Equal("Cannot access this directory.", result.Error);
        Assert.Empty(result.Directories);
    }
}
