using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Security tests for <see cref="LinkRepairService" />.
///     Verifies that link files with malicious content (path traversal,
///     command injection, oversized content) are handled safely.
///     Tests both .strm and symlink handler scenarios.
/// </summary>
public class LinkRepairSecurityTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly LinkRepairService _service;
    private readonly StrmLinkHandler _strmHandler;
    private readonly SymlinkHandler _symlinkHandler;
    private readonly Mock<ISymlinkHelper> _symlinkHelper;

    public LinkRepairSecurityTests()
    {
        _fileSystem = new MockFileSystem();
        _strmHandler = new StrmLinkHandler(_fileSystem);
        _symlinkHelper = new Mock<ISymlinkHelper>();
        _symlinkHandler = new SymlinkHandler(_symlinkHelper.Object);
        _service = new LinkRepairService(
            _fileSystem,
            [_strmHandler, _symlinkHandler],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);
    }

    // ===== .strm: Path Traversal =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_PathTraversal_DetectedAsBroken()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("../../../etc/passwd"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_WindowsPathTraversal_DetectedAsBroken()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(@"..\..\..\..\Windows\System32\config\sam"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== Symlink: Path Traversal =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Symlink_PathTraversal_DetectedAsBroken()
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile))
            .Returns("../../../etc/passwd");

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Symlink_WindowsPathTraversal_DetectedAsBroken()
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile))
            .Returns(@"..\..\..\..\Windows\System32\config\sam");

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== .strm: Command Injection =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("; rm -rf /")]
    [InlineData("| cat /etc/passwd")]
    [InlineData("$(malicious_command)")]
    [InlineData("`whoami`")]
    public void ProcessLinkFile_Strm_CommandInjection_DetectedAsBroken(string maliciousContent)
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(maliciousContent));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== Symlink: Command Injection =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("; rm -rf /")]
    [InlineData("| cat /etc/passwd")]
    [InlineData("$(malicious_command)")]
    [InlineData("`whoami`")]
    public void ProcessLinkFile_Symlink_CommandInjection_DetectedAsBroken(string maliciousTarget)
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(maliciousTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== .strm: URL schemes =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("http://malicious-server.com/payload.exe")]
    [InlineData("https://evil.com/redirect")]
    [InlineData("ftp://attacker.com/data")]
    public void ProcessLinkFile_Strm_StreamingUrlScheme_TreatedAsValid(string urlContent)
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(urlContent));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_FileScheme_TreatedAsBroken()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("file:///etc/passwd"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        // file:// URIs reference local files and must NOT bypass validation
        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== Symlink: URL-like targets =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("http://malicious-server.com/payload.exe")]
    [InlineData("https://evil.com/redirect")]
    public void ProcessLinkFile_Symlink_UrlTarget_TreatedAsTargetMissing(string urlTarget)
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(urlTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        // Symlinks do not support URL targets (SupportsUrlTargets = false),
        // so a URL target is treated as a broken link (path does not exist).
        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== .strm: Empty / Whitespace =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\t")]
    public void ProcessLinkFile_Strm_EmptyOrWhitespace_DetectedAsInvalidContent(string content)
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(content));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    // ===== Symlink: Null target =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Symlink_NullTarget_DetectedAsInvalidContent()
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns((string?)null);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    // ===== .strm: Oversized content =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_OversizedContent_HandledWithoutCrash()
    {
        var longPath = "/" + new string('A', 10_000) + ".mkv";
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(longPath));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== Symlink: Oversized target =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Symlink_OversizedTarget_HandledWithoutCrash()
    {
        var longPath = "/" + new string('A', 10_000) + ".mkv";
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(longPath);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== .strm: Null bytes =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_NullBytes_HandledSafely()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("/movies/Movie1/movie\0.mkv"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        // The service must handle null bytes gracefully without throwing and must report Broken
        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== Symlink: Null bytes in target =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Symlink_NullBytesInTarget_HandledSafely()
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile))
            .Returns("/movies/Movie1/movie\0.mkv");

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        // The service must handle null bytes gracefully without throwing and must report Broken
        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== FindLinkFiles: Edge cases =====

    [Fact]
    [Trait("Category", "Security")]
    public void FindLinkFiles_EmptyLibraryList_ReturnsEmpty()
    {
        var result = _service.FindLinkFiles([]);
        Assert.Empty(result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FindLinkFiles_PathWithSpecialCharacters_HandledSafely()
    {
        var specialDir = _fileSystem.Path.GetFullPath("/series/Show (2024) [HEVC]");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show (2024) [HEVC]/Specials/ep.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("target"));

        var result = _service.FindLinkFiles([specialDir]);

        Assert.Single(result);
        Assert.Contains(result, r => r.FilePath == linkFile);
    }

    // ===== .strm: Unicode and encoding =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_UnicodeContent_HandledSafely()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("/movies/Ünïcödé Mövie/fïlm.mkv"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_MultiLineContent_TreatedAsBroken()
    {
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(movieFile + "\n/etc/passwd\n/etc/shadow"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    // ===== DryRun Safety: .strm =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Strm_DryRunMode_DoesNotModifyFiles()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        const string originalTarget = "/series/Show1/old_target.mkv";
        var newMediaFile = _fileSystem.Path.GetFullPath("/series/Show1/actual_episode.mkv");
        _fileSystem.AddFile(linkFile, new MockFileData(originalTarget));
        _fileSystem.AddFile(newMediaFile, new MockFileData("video content"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Repaired, result.Status); // Dry-run: Repaired signals "would repair"
        Assert.Equal(originalTarget, _fileSystem.File.ReadAllText(linkFile));
    }

    // ===== DryRun Safety: Symlink =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessLinkFile_Symlink_DryRunMode_DoesNotModifySymlinks()
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        var brokenTarget = _fileSystem.Path.GetFullPath("/movies/Movie1/old.mkv");
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var newFile = _fileSystem.Path.Join(movieDir, "new.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Repaired, result.Status); // Dry-run: Repaired signals "would repair"
        _symlinkHelper.Verify(h => h.DeleteSymlink(It.IsAny<string>()), Times.Never);
        _symlinkHelper.Verify(h => h.CreateSymlink(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}