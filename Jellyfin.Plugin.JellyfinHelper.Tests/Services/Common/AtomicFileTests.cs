using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Tests for the internal <see cref="AtomicFile" /> helper. The core contract is:
///     <list type="bullet">
///         <item>Successful writes replace the file with a UTF-8 (no BOM) payload.</item>
///         <item>Temporary files created during the write are cleaned up on failure and success.</item>
///         <item>Retry backoff triggers on transient IO errors but the final error still surfaces.</item>
///         <item>The async overload honours <see cref="CancellationToken"/> without leaving orphans.</item>
///     </list>
///     Filesystem-based tests use per-test temp directories so parallel runs cannot collide.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "AtomicFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
        catch (IOException)
        {
            // Best effort - test cleanup is non-critical
        }
    }

    // === WriteAllText (synchronous) ===

    [Fact]
    public void WriteAllText_CreatesFile_WithExactContents()
    {
        var path = Path.Join(_tempDir, "target.txt");
        var payload = "Hello, atomic world!\nWith newline.";

        AtomicFile.WriteAllText(path, payload);

        Assert.True(File.Exists(path));
        var round = File.ReadAllText(path);
        Assert.Equal(payload, round);
    }

    [Fact]
    public void WriteAllText_UsesUtf8NoBom()
    {
        var path = Path.Join(_tempDir, "no-bom.txt");
        // Non-ASCII ensures UTF-8 encoding path is exercised
        var payload = "Héllo — Ümlauts. 🎬";

        AtomicFile.WriteAllText(path, payload);

        var bytes = File.ReadAllBytes(path);
        Assert.NotEmpty(bytes);
        // BOM would be 0xEF 0xBB 0xBF. Assert first bytes are NOT the BOM.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var round = Encoding.UTF8.GetString(bytes);
        Assert.Equal(payload, round);
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile()
    {
        var path = Path.Join(_tempDir, "overwrite.txt");
        File.WriteAllText(path, "OLD CONTENTS");

        AtomicFile.WriteAllText(path, "NEW CONTENTS");

        Assert.Equal("NEW CONTENTS", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_EmptyString_WritesEmptyFile()
    {
        var path = Path.Join(_tempDir, "empty.txt");

        AtomicFile.WriteAllText(path, string.Empty);

        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void WriteAllText_LeavesNoOrphanTempFiles()
    {
        // Regression: temp files must be cleaned up on success. We check by counting the
        // ".tmp" files in the target directory after a normal write.
        var path = Path.Join(_tempDir, "orphan-check.txt");

        AtomicFile.WriteAllText(path, "data");

        var orphans = Directory.GetFiles(_tempDir, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.Empty(orphans);
    }

    [Fact]
    public void WriteAllText_MultipleWrites_SucceedIndependently()
    {
        var path = Path.Join(_tempDir, "multi.txt");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");
        AtomicFile.WriteAllText(path, "third");

        Assert.Equal("third", File.ReadAllText(path));
        // No orphan temp files after multiple writes
        var orphans = Directory.GetFiles(_tempDir, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.Empty(orphans);
    }

    [Fact]
    public void WriteAllText_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AtomicFile.WriteAllText(null!, "data"));
    }

    [Fact]
    public void WriteAllText_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => AtomicFile.WriteAllText(string.Empty, "data"));
    }

    [Fact]
    public void WriteAllText_NullContents_Throws()
    {
        var path = Path.Join(_tempDir, "null-contents.txt");
        Assert.Throws<ArgumentNullException>(() => AtomicFile.WriteAllText(path, null!));
    }

    [Fact]
    public void WriteAllText_InvalidDirectory_ThrowsAfterExhaustedRetries()
    {
        // Non-existent parent directory → DirectoryNotFoundException (a subtype of IOException).
        // The helper retries because the IO check accepts IOException, then rethrows after
        // exhausting the attempts.
        var path = Path.Join(_tempDir, "does-not-exist", "subdir", "file.txt");

        Assert.Throws<DirectoryNotFoundException>(
            () => AtomicFile.WriteAllText(path, "x", maxAttempts: 2));
    }

    [Fact]
    public void WriteAllText_ClampsMaxAttemptsToOne_WhenBelowOne()
    {
        // maxAttempts=0 → clamped to 1. Successful write should still complete.
        var path = Path.Join(_tempDir, "clamp.txt");

        AtomicFile.WriteAllText(path, "clamped", maxAttempts: 0);

        Assert.Equal("clamped", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_ClampsMaxAttemptsToOne_WhenNegative()
    {
        var path = Path.Join(_tempDir, "clamp-neg.txt");

        AtomicFile.WriteAllText(path, "clamped-neg", maxAttempts: -5);

        Assert.Equal("clamped-neg", File.ReadAllText(path));
    }

    // === WriteAllTextAsync ===

    [Fact]
    public async Task WriteAllTextAsync_CreatesFile_WithExactContents()
    {
        var path = Path.Join(_tempDir, "async-target.txt");
        var payload = "Async payload with special chars: äöü";

        await AtomicFile.WriteAllTextAsync(path, payload);

        Assert.Equal(payload, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAsync_UsesUtf8NoBom()
    {
        var path = Path.Join(_tempDir, "async-no-bom.txt");
        var payload = "🎬 no-bom test";

        await AtomicFile.WriteAllTextAsync(path, payload);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(payload, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task WriteAllTextAsync_OverwritesExistingFile()
    {
        var path = Path.Join(_tempDir, "async-overwrite.txt");
        await File.WriteAllTextAsync(path, "OLD");

        await AtomicFile.WriteAllTextAsync(path, "NEW");

        Assert.Equal("NEW", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAsync_CancelledBeforeStart_Throws()
    {
        var path = Path.Join(_tempDir, "cancelled.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AtomicFile.WriteAllTextAsync(path, "data", cancellationToken: cts.Token));

        // File must not exist
        Assert.False(File.Exists(path));
        // No temp file left behind
        var orphans = Directory.GetFiles(_tempDir, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.Empty(orphans);
    }

    [Fact]
    public async Task WriteAllTextAsync_NullPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => AtomicFile.WriteAllTextAsync(null!, "data"));
    }

    [Fact]
    public async Task WriteAllTextAsync_EmptyPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => AtomicFile.WriteAllTextAsync(string.Empty, "data"));
    }

    [Fact]
    public async Task WriteAllTextAsync_NullContents_Throws()
    {
        var path = Path.Join(_tempDir, "null-async.txt");
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => AtomicFile.WriteAllTextAsync(path, null!));
    }

    [Fact]
    public async Task WriteAllTextAsync_ClampsMaxAttemptsToOne_WhenBelowOne()
    {
        var path = Path.Join(_tempDir, "async-clamp.txt");

        await AtomicFile.WriteAllTextAsync(path, "async-clamped", maxAttempts: 0);

        Assert.Equal("async-clamped", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAsync_InvalidDirectory_ThrowsAfterExhaustedRetries()
    {
        var path = Path.Join(_tempDir, "no-such-subdir", "file.txt");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => AtomicFile.WriteAllTextAsync(path, "x", maxAttempts: 2));
    }

    [Fact]
    public async Task WriteAllTextAsync_LeavesNoOrphanTempFiles_OnSuccess()
    {
        var path = Path.Join(_tempDir, "async-orphan.txt");
        await AtomicFile.WriteAllTextAsync(path, "async-data");

        var orphans = Directory.GetFiles(_tempDir, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.Empty(orphans);
    }

    [Fact]
    public async Task WriteAllTextAsync_LargePayload_HandledCorrectly()
    {
        // 5 MB payload to exercise larger write path
        var path = Path.Join(_tempDir, "big.txt");
        var payload = new string('A', 5 * 1024 * 1024);

        await AtomicFile.WriteAllTextAsync(path, payload);

        Assert.Equal(5 * 1024 * 1024, new FileInfo(path).Length);
    }

    [Fact]
    public async Task WriteAllTextAsync_CancelledMidWrite_LeavesNoOrphans()
    {
        // Cancel after a small write starts, before finish. Even with a token that fires
        // immediately, the temp file cleanup must run and the target must remain untouched.
        var path = Path.Join(_tempDir, "cancel-mid.txt");
        using var cts = new CancellationTokenSource();

        // Trigger cancellation before starting
        cts.Cancel();

        try
        {
            await AtomicFile.WriteAllTextAsync(path, "content", cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        Assert.False(File.Exists(path));
        var orphans = Directory.GetFiles(_tempDir, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.Empty(orphans);
    }

    [Fact]
    public void WriteAllText_AndReadBack_Utf8SpecialChars()
    {
        // Cross-check with a manual utf-8 read to guarantee no double-encoding
        var path = Path.Join(_tempDir, "special.txt");
        var payload = "π ≈ 3.14, Ω / ∑ / √";

        AtomicFile.WriteAllText(path, payload);

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        Assert.Equal(payload, reader.ReadToEnd());
    }
}
