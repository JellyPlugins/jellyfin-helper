using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Resilience tests for <see cref="FileSystemHelper.CalculateDirectorySize" />: per-file and
///     per-directory access failures must be swallowed so a single broken entry never aborts the
///     whole traversal or double-counts. Exercised against a real on-disk tree because the SUT
///     calls the static <see cref="Directory" /> / <see cref="FileInfo" /> APIs directly.
/// </summary>
public sealed class FileSystemHelperErrorHandlingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public FileSystemHelperErrorHandlingTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    private static void WriteBytes(string filePath, int length)
    {
        File.WriteAllBytes(filePath, new byte[length]);
    }

    [Fact]
    public void CalculateDirectorySize_FileEntryLengthThrowsIOException_SkipsFileButCountsRest()
    {
        WriteBytes(Path.Combine(_root, "real.mkv"), 1000);

        // A dangling symlink is returned by GetFiles, but reading its Length must throw an
        // IOException so the SUT skips it. Windows stat-fails the missing target and throws
        // FileNotFoundException; Linux instead reports the symlink's own byte size (the stored
        // target path length) without throwing, so the inner catch is never exercised there.
        // This throw-on-Length behavior is genuinely OS-specific, so assert only where it holds.
        var link = Path.Combine(_root, "broken.link");
        var missingTarget = Path.Combine(_root, "does_not_exist.mkv");
        try
        {
            File.CreateSymbolicLink(link, missingTarget);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Symlink creation needs elevated privileges on some Windows setups; early-return
            // keeps the test green there and asserts on runners that permit symlinks.
            _ = ex;
            return;
        }

        // Guard against a false positive: only assert the skip contract when reading the link's
        // Length actually throws an IOException on this OS (Windows). If it returns a size instead
        // (Linux), the entry doesn't hit the SUT's inner catch, so there is nothing to assert here.
        try
        {
            _ = new FileInfo(link).Length;
            return;
        }
        catch (IOException)
        {
            // Expected on Windows: the broken symlink's Length genuinely throws.
        }

        var result = FileSystemHelper.CalculateDirectorySize(_root);

        // Only the accessible file counts; the broken entry contributes 0 without aborting the loop.
        Assert.Equal(1000, result);
    }

    [Fact]
    public void CalculateDirectorySize_SubdirectoryEnumerationThrowsUnauthorizedAccess_SkipsItButCountsSibling()
    {
        // chmod is a no-op on Windows and forcing UAE there needs flaky ACL edits.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var accessible = Directory.CreateDirectory(Path.Combine(_root, "readable")).FullName;
        WriteBytes(Path.Combine(accessible, "file.mkv"), 700);

        var denied = Directory.CreateDirectory(Path.Combine(_root, "denied")).FullName;
        WriteBytes(Path.Combine(denied, "secret.mkv"), 500);

        try
        {
            File.SetUnixFileMode(denied, UnixFileMode.None);

            // Running as root ignores permission bits, so enumeration would still succeed and the
            // outer catch wouldn't be hit; skip rather than assert a false positive.
            try
            {
                _ = Directory.GetFiles(denied);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: the denied subdir genuinely blocks enumeration.
            }

            var result = FileSystemHelper.CalculateDirectorySize(_root);

            // The denied subdir contributes 0 without throwing; the readable sibling is still summed.
            Assert.Equal(700, result);
        }
        finally
        {
            // Restore permissions so Dispose can delete the tree.
            File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
