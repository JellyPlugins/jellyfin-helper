using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FolderBrowser;

/// <summary>
///     Security tests for <see cref="FolderBrowserService" />. Verifies the symlink-escape
///     guards: a link whose own lexical path is innocuous but which dereferences to a
///     sensitive system directory must never be browsable or listed, and an unresolvable
///     (broken/cyclic) reparse point must not abort a listing.
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
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ValidatePath_SymlinkToSensitiveSystemDir_IsRefusedAsProtected()
    {
        // A directory link whose own path is innocuous but that resolves to /etc must be
        // refused: Path.GetFullPath does not dereference the link, so the guard has to
        // resolve the final target and re-apply the sensitive-path check.
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
        // Windows counterpart of the POSIX symlink-escape test: the link's own path is under
        // the temp dir (lexically innocuous), but it resolves to C:\Windows. The guard must
        // dereference the reparse point and re-apply the sensitive-path check.
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
        // Two mutually-referencing directory links cannot be resolved; ResolveLinkTarget throws
        // and the entry is treated as unlistable/critical. The unresolvable links must be filtered
        // out without aborting the listing, and the normal sibling must remain visible.
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
}
