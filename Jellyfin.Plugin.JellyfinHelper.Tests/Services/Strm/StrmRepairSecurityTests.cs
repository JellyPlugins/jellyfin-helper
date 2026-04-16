using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Strm;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Strm;

/// <summary>
///     Security tests for <see cref="StrmRepairService" />.
///     Verifies that STRM files with malicious content (path traversal,
///     command injection, oversized content) are handled safely.
/// </summary>
public class StrmRepairSecurityTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly StrmRepairService _service;

    public StrmRepairSecurityTests()
    {
        _fileSystem = new MockFileSystem();
        _service = new StrmRepairService(
            _fileSystem,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<StrmRepairService>().Object);
    }

    // ===== STRM Content: Path Traversal =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessStrmFile_PathTraversalInContent_DetectedAsBroken()
    {
        // STRM file points to a path traversal target
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("../../../etc/passwd"));

        var result = _service.ProcessStrmFile(strmFile, true);

        // The target file doesn't exist, so it should be detected as broken
        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessStrmFile_WindowsPathTraversalInContent_DetectedAsBroken()
    {
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(@"..\..\..\..\Windows\System32\config\sam"));

        var result = _service.ProcessStrmFile(strmFile, true);

        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    // ===== STRM Content: Command Injection =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("; rm -rf /")]
    [InlineData("| cat /etc/passwd")]
    [InlineData("$(malicious_command)")]
    [InlineData("`whoami`")]
    public void ProcessStrmFile_CommandInjectionInContent_DetectedAsBroken(string maliciousContent)
    {
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(maliciousContent));

        var result = _service.ProcessStrmFile(strmFile, true);

        // These are not valid file paths, so target won't exist
        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    // ===== STRM Content: URL schemes =====
    // Note: STRM files are specifically designed to point to URLs (HTTP streams, etc.)
    // so URL content is intentionally treated as Valid by the implementation.

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("http://malicious-server.com/payload.exe")]
    [InlineData("https://evil.com/redirect")]
    [InlineData("ftp://attacker.com/data")]
    [InlineData("file:///etc/passwd")]
    public void ProcessStrmFile_UrlSchemeContent_TreatedAsValid(string urlContent)
    {
        // STRM files are designed to contain URLs — this is expected/valid behavior.
        // The repair service intentionally does NOT break URL-based STRM files.
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(urlContent));

        var result = _service.ProcessStrmFile(strmFile, true);

        Assert.Equal(StrmFileStatus.Valid, result.Status);
    }

    // ===== STRM Content: Empty / Whitespace =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\t")]
    public void ProcessStrmFile_EmptyOrWhitespaceContent_DetectedAsInvalidContent(string content)
    {
        // The implementation correctly distinguishes between empty/whitespace content
        // (InvalidContent) and a path that doesn't resolve (Broken).
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(content));

        var result = _service.ProcessStrmFile(strmFile, true);

        Assert.Equal(StrmFileStatus.InvalidContent, result.Status);
    }

    // ===== STRM Content: Oversized content =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessStrmFile_OversizedContent_HandledWithoutCrash()
    {
        // Create a STRM file with an extremely long path (potential buffer overflow attack)
        var longPath = "/" + new string('A', 10_000) + ".mkv";
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(longPath));

        var result = _service.ProcessStrmFile(strmFile, true);

        // Should not crash, target doesn't exist
        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    // ===== STRM Content: Null bytes =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessStrmFile_NullBytesInContent_HandledSafely()
    {
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("/movies/Movie1/movie\0.mkv"));

        // Should not crash regardless of how null bytes are handled
        var exception = Record.Exception(() => _service.ProcessStrmFile(strmFile, true));

        // Either it completes without exception or throws a controlled exception
        // The important thing is no uncontrolled crash
        if (exception != null)
        {
            Assert.True(
                exception is ArgumentException or IOException or NotSupportedException,
                $"Unexpected exception type: {exception.GetType().Name}");
        }
    }

    // ===== FindStrmFiles: Non-existent paths =====

    [Fact]
    [Trait("Category", "Security")]
    public void FindStrmFiles_EmptyLibraryList_ReturnsEmpty()
    {
        var result = _service.FindStrmFiles([]);
        Assert.Empty(result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FindStrmFiles_PathWithSpecialCharacters_HandledSafely()
    {
        var specialDir = _fileSystem.Path.GetFullPath("/series/Show (2024) [HEVC]");
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show (2024) [HEVC]/Specials/ep.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("target"));

        var result = _service.FindStrmFiles([specialDir]);

        Assert.Single(result);
        Assert.Contains(strmFile, result);
    }

    // ===== STRM Content: Unicode and encoding edge cases =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessStrmFile_UnicodeContent_HandledSafely()
    {
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("/movies/Ünïcödé Mövie/fïlm.mkv"));

        var result = _service.ProcessStrmFile(strmFile, true);

        // Target doesn't exist, should be broken but not crash
        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessStrmFile_MultiLineContent_TreatedAsBroken()
    {
        // A STRM file with multiple lines results in a path containing newlines,
        // which is not a valid file path. The implementation correctly treats this
        // as Broken because the full content (with newlines) doesn't resolve to a file.
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(movieFile + "\n/etc/passwd\n/etc/shadow"));

        var result = _service.ProcessStrmFile(strmFile, true);

        // The full multi-line content is used as path, which doesn't exist → Broken
        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    // ===== DryRun vs. Execute: Safety =====

    [Fact]
    [Trait("Category", "Security")]
    public void ProcessStrmFile_DryRunMode_DoesNotModifyFiles()
    {
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("/nonexistent/target.mkv"));

        // Process in dry-run mode
        var result = _service.ProcessStrmFile(strmFile, true);

        Assert.Equal(StrmFileStatus.Broken, result.Status);

        // Verify the STRM file was not modified
        var content = _fileSystem.File.ReadAllText(strmFile);
        Assert.Equal("/nonexistent/target.mkv", content);
    }
}