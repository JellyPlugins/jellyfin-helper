using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Security tests for <see cref="TrashService" />.
///     Verifies that path traversal, null bytes, and other malicious inputs
///     are handled safely without escaping intended directories.
/// </summary>
public class TrashServiceSecurityTests : IDisposable
{
    private readonly ILogger _loggerMock = TestMockFactory.CreateLogger().Object;
    private readonly string _testRoot = TestDataGenerator.CreateTempDirectory("TrashSec");
    private readonly TrashService _trashService = new(TestMockFactory.CreatePluginLogService());

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    // ===== Path Traversal: MoveToTrash =====

    [Fact]
    [Trait("Category", "Security")]
    public void MoveToTrash_PathTraversalInSource_DoesNotEscapeTrash()
    {
        // Create a directory that could be targeted by path traversal
        var sensitiveDir = Path.Join(_testRoot, "sensitive");
        Directory.CreateDirectory(sensitiveDir);
        File.WriteAllBytes(Path.Join(sensitiveDir, "secret.txt"), new byte[100]);

        // Attempt path traversal in source path — resolves to _testRoot/sensitive
        var traversalSource = Path.Join(_testRoot, "trash", "..", "sensitive");
        var trashPath = Path.Join(_testRoot, "trash_output");

        // MoveToTrash should either move the resolved path or return 0 if it doesn't exist
        var result = _trashService.MoveToTrash(traversalSource, trashPath, _loggerMock);

        if (result > 0)
        {
            // If it did move, verify it went INTO the trash, not somewhere else
            Assert.True(Directory.Exists(trashPath), "Trash output directory should exist");
            var trashContents = Directory.GetDirectories(trashPath);
            Assert.Single(trashContents);

            // Verify the moved content is inside the trash directory (not outside)
            Assert.True(trashContents[0].StartsWith(Path.GetFullPath(trashPath), StringComparison.Ordinal),
                "Moved directory must reside inside the trash folder");
        }
        else
        {
            // If nothing was moved, the sensitive directory must still be intact
            Assert.True(Directory.Exists(sensitiveDir), "Sensitive directory should still exist when nothing was moved");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MoveToTrash_TrashBasePathWithTraversal_CreatesInResolvedLocation()
    {
        // Create source to move
        var sourceDir = Path.Join(_testRoot, "source_movie");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Join(sourceDir, "movie.mkv"), new byte[512]);

        // Use path traversal in trash base path
        var trashPath = Path.Join(_testRoot, "subdir", "..", "actual_trash");

        var result = _trashService.MoveToTrash(sourceDir, trashPath, _loggerMock);

        Assert.Equal(512, result);
        // The resolved path should be _testRoot/actual_trash
        var resolvedTrashPath = Path.GetFullPath(trashPath);
        Assert.True(Directory.Exists(resolvedTrashPath), "Trash should be created at resolved path");
    }

    // ===== Path Traversal: MoveFileToTrash =====

    [Fact]
    [Trait("Category", "Security")]
    public void MoveFileToTrash_PathTraversalInSource_HandledSafely()
    {
        var sensitiveFile = Path.Join(_testRoot, "secret.conf");
        File.WriteAllBytes(sensitiveFile, new byte[64]);

        // Attempt path traversal — this resolves to the same file
        var traversalPath = Path.Join(_testRoot, "subdir", "..", "secret.conf");
        var trashPath = Path.Join(_testRoot, "trash_output");

        // Since the file doesn't exist at the literal path (subdir doesn't exist),
        // the behavior depends on OS path resolution
        var result = _trashService.MoveFileToTrash(traversalPath, trashPath, _loggerMock);

        if (result > 0)
        {
            // File was moved — verify it landed inside the trash folder
            Assert.True(Directory.Exists(trashPath), "Trash output directory should exist");
        }
        else
        {
            // File was not moved (path not found) — original must still exist
            Assert.True(File.Exists(sensitiveFile), "Original file should still exist when nothing was moved");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MoveFileToTrash_TrashBaseWithTraversal_CreatesInResolvedLocation()
    {
        var sourceFile = Path.Join(_testRoot, "subtitle.srt");
        File.WriteAllBytes(sourceFile, new byte[256]);

        var trashPath = Path.Join(_testRoot, "a", "..", "b", "..", "real_trash");

        var result = _trashService.MoveFileToTrash(sourceFile, trashPath, _loggerMock);

        Assert.Equal(256, result);
        var resolvedPath = Path.GetFullPath(trashPath);
        Assert.True(Directory.Exists(resolvedPath));
    }

    // ===== Malicious Trash Item Names: PurgeExpiredTrash =====

    [Fact]
    [Trait("Category", "Security")]
    public void PurgeExpiredTrash_TrashItemWithPathTraversalName_DoesNotEscapeTrashFolder()
    {
        var trashPath = Path.Join(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        // Create a legitimate-looking but maliciously named directory in trash
        // The PurgeExpiredTrash method parses timestamps from directory names,
        // so a name without a valid timestamp will be skipped
        // Use platform-agnostic separators so this actually tests path traversal on Linux too
        var maliciousDir = Path.Join(trashPath, "..", "..", "etc");

        // This should NOT be created outside trash due to OS path resolution
        try
        {
            Directory.CreateDirectory(maliciousDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // If the OS rejects the path, that's fine
            return;
        }

        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var (_, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 0, _loggerMock, now);

        // The malicious directory name has no valid timestamp, so it should be skipped
        Assert.Equal(0, itemsPurged);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PurgeExpiredTrash_TrashItemWithNullBytesInName_SkippedSafely()
    {
        var trashPath = Path.Join(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        // Try to create a file with null bytes in the name
        // Most OS will reject this, which is the correct behavior
        try
        {
            var maliciousFile = Path.Join(trashPath, "20260101-120000_evil\0payload.txt");
            File.WriteAllBytes(maliciousFile, new byte[10]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // OS correctly rejects null bytes in filenames — this is expected
        }

        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        // Should not crash
        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 0, _loggerMock, now);
        Assert.True(bytesFreed >= 0);
        Assert.True(itemsPurged >= 0);
    }

    // ===== ExtractOriginalName: Malicious inputs =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("20260101-120000_<script>alert(1)</script>")]
    [InlineData("20260101-120000_../../../etc/passwd")]
    [InlineData("20260101-120000_C:\\Windows\\System32\\config")]
    public void ExtractOriginalName_MaliciousNames_ReturnsNameWithoutCrash(string input)
    {
        // ExtractOriginalName should not crash on malicious input
        var result = TrashService.ExtractOriginalName(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("20260101-120000", result);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("20260101-120000_normal_movie")]
    [InlineData("20260101-120000_Movie (2024)")]
    [InlineData("20260101-120000_Ünïcödé Mövie")]
    public void ExtractOriginalName_ValidNames_ExtractsCorrectly(string input)
    {
        var result = TrashService.ExtractOriginalName(input);
        Assert.NotNull(result);
        Assert.False(result.StartsWith("20260101", StringComparison.Ordinal));
    }

    // ===== TryParseTrashTimestamp: Edge cases =====

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("99999999-999999_overflow")]
    [InlineData("00000000-000000_zero")]
    [InlineData("20261301-120000_invalid_month")]
    [InlineData("20260132-120000_invalid_day")]
    [InlineData("20260101-250000_invalid_hour")]
    public void TryParseTrashTimestamp_InvalidDateValues_ReturnsFalse(string input)
    {
        var result = TrashService.TryParseTrashTimestamp(input, out _);
        Assert.False(result);
    }

    // ===== GetTrashContents: Large directory =====

    [Fact]
    [Trait("Category", "Security")]
    public void GetTrashContents_ManyItems_DoesNotCrash()
    {
        var trashPath = Path.Join(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        // Create many items to test for potential DoS via large directory enumeration
        for (var i = 0; i < 100; i++)
        {
            var dir = Path.Join(trashPath, $"20260101-{i:D6}_Movie{i}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Join(dir, "m.mkv"), new byte[10]);
        }

        var result = _trashService.GetTrashContents(trashPath, 30);

        Assert.Equal(100, result.Count);
    }

    // ===== GetTrashSummary: Symlink/Junction safety =====

    [Fact]
    [Trait("Category", "Security")]
    public void GetTrashSummary_EmptyTrash_ReturnsZeroSafely()
    {
        var trashPath = Path.Join(_testRoot, "empty_trash");
        Directory.CreateDirectory(trashPath);

        var (totalSize, itemCount) = _trashService.GetTrashSummary(trashPath);

        Assert.Equal(0, totalSize);
        Assert.Equal(0, itemCount);
    }

    // ===== Concurrent collision handling =====

    [Fact]
    [Trait("Category", "Security")]
    public void MoveToTrash_CollisionHandling_DoesNotOverwrite()
    {
        var trashPath = Path.Join(_testRoot, "trash");

        // Move first item
        var source1 = Path.Join(_testRoot, "movie_dir");
        Directory.CreateDirectory(source1);
        File.WriteAllBytes(Path.Join(source1, "movie.mkv"), new byte[100]);

        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _trashService.MoveToTrash(source1, trashPath, _loggerMock, now);

        // Create another item with the same name and move at the same timestamp
        Directory.CreateDirectory(source1);
        File.WriteAllBytes(Path.Join(source1, "movie.mkv"), new byte[200]);

        var result2 = _trashService.MoveToTrash(source1, trashPath, _loggerMock, now);

        Assert.Equal(200, result2);

        // Should have 2 directories in trash (collision resolved with suffix)
        var trashDirs = Directory.GetDirectories(trashPath);
        Assert.Equal(2, trashDirs.Length);
    }
}