using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Writes text via write-to-temp then File.Replace (atomic ReplaceFileW when the destination exists) or File.Move (when new), with bounded retry on transient I/O.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Default number of write-and-move attempts before surfacing the failure.</summary>
    internal const int DefaultMaxAttempts = 5;

    /// <summary>Base backoff in milliseconds; scaled linearly by the attempt number.</summary>
    private const int BaseBackoffMilliseconds = 20;

    /// <summary>
    ///     UTF-8 encoding without a byte-order mark. Cached to avoid re-instantiating the encoder per write; .NET's default File.WriteAllText emits a BOM that some downstream tools (JSON validators, log parsers) reject.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    ///     Atomically writes contents to path. Retries the temp-write-then-move on transient IOException / UnauthorizedAccessException up to maxAttempts times; if all fail, the last transient error propagates so the caller's best-effort try/catch can log it.
    /// </summary>
    /// <param name="path">The destination file path.</param>
    /// <param name="contents">The text to write.</param>
    /// <param name="maxAttempts">Maximum attempts (clamped to at least 1).</param>
    /// <exception cref="ArgumentException">If <paramref name="path"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="contents"/> is null.</exception>
    internal static void WriteAllText(string path, string contents, int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);

        maxAttempts = PrepareWrite(path, maxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Fresh temp name per attempt avoids colliding with a temp file a prior attempt failed to clean up.
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, contents, Utf8NoBom);
                ReplaceOrMove(tempPath, path);

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                // Transient lock (AV/indexer): clean up this attempt's temp file and back off before retrying.
                TryDeleteQuietly(tempPath);
                Thread.Sleep(BaseBackoffMilliseconds * attempt);
            }
            catch
            {
                // Final attempt or non-transient error: remove the orphan temp file and propagate.
                TryDeleteQuietly(tempPath);
                throw;
            }
        }
    }

    /// <summary>
    ///     Asynchronous counterpart of WriteAllText for request-driven callers. Uses Delay(int, CancellationToken) for backoff so the caller's thread is released.
    /// </summary>
    /// <param name="path">The destination file path.</param>
    /// <param name="contents">The text to write.</param>
    /// <param name="maxAttempts">Maximum attempts (clamped to at least 1).</param>
    /// <param name="cancellationToken">A cancellation token honoured between retries and during the write itself.</param>
    /// <returns>A task that completes when the atomic write has succeeded.</returns>
    /// <exception cref="ArgumentException">If <paramref name="path"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="contents"/> is null.</exception>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is signalled.</exception>
    internal static async Task WriteAllTextAsync(
        string path,
        string contents,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);

        maxAttempts = PrepareWrite(path, maxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fresh temp name per attempt avoids colliding with a temp file a prior attempt failed to clean up.
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // The async write honours the cancellation token natively.
                await File.WriteAllTextAsync(tempPath, contents, Utf8NoBom, cancellationToken).ConfigureAwait(false);

                ReplaceOrMove(tempPath, path);

                return;
            }
            catch (OperationCanceledException)
            {
                // Cancellation during the async write: clean up and propagate. We do NOT retry on
                // cancellation - that would defeat cooperative cancellation.
                TryDeleteQuietly(tempPath);
                throw;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                // Transient lock (AV/indexer): clean up this attempt's temp file and back off before retrying.
                TryDeleteQuietly(tempPath);

                // A signalled token surfaces OperationCanceledException here and propagates out of the
                // loop unretried (cancellation between attempts must stop retrying).
                await Task.Delay(BaseBackoffMilliseconds * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Final attempt or non-transient error: remove the orphan temp file and propagate.
                TryDeleteQuietly(tempPath);
                throw;
            }
        }
    }

    /// <summary>
    ///     Clamps to at least 1 and ensures the destination directory exists before the temp file is written (CreateDirectory is idempotent).
    /// </summary>
    /// <param name="path">The destination file path.</param>
    /// <param name="maxAttempts">The requested maximum attempts.</param>
    /// <returns>The clamped maximum attempts.</returns>
    private static int PrepareWrite(string path, int maxAttempts)
    {
        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return maxAttempts;
    }

    /// <summary>
    ///     Moves the freshly written temp file into place: File.Replace (atomic) when the destination already exists, otherwise File.Move.
    /// </summary>
    /// <param name="tempPath">The temp file that was just written.</param>
    /// <param name="path">The final destination path.</param>
    private static void ReplaceOrMove(string tempPath, string path)
    {
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    /// <summary>Deletes a temp file, swallowing the non-critical I/O errors that cleanup can raise.</summary>
    private static void TryDeleteQuietly(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException)
        {
            // best effort - temp file cleanup is non-critical
        }
    }
}