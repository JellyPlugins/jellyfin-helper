using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Writes text files via write-to-temp then File.Replace/Move, with bounded retry on transient
///     I/O failures. <c>File.Replace</c> when the destination exists (atomic via <c>ReplaceFileW</c>
///     on Windows), <c>File.Move</c> when new. File.Move-with-overwrite is NOT atomic on Windows
///     (deletes then renames), hence File.Replace is preferred.
///     <para>
///         <b>Threading model:</b> Two entry points share one retry contract:
///         <list type="bullet">
///             <item>
///                 <description>
///                     <see cref="WriteAllText"/> - synchronous, backs off via <see cref="Thread.Sleep(int)"/>.
///                     Blocks up to ~200 ms with the default 5 attempts (sleeps 20 + 40 + 60 + 80 ms; the
///                     final attempt propagates without sleeping). For background scheduled-task paths.
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     <see cref="WriteAllTextAsync"/> - asynchronous, backs off via
///                     <see cref="Task.Delay(int, CancellationToken)"/> so the request thread is released
///                     during backoff. For latency-sensitive ASP.NET request handlers. Honours the
///                     <see cref="CancellationToken"/> so a cancelled request stops retrying.
///                 </description>
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Encoding:</b> UTF-8 <i>without</i> BOM, matching what <c>System.Text.Json</c> expects
///         on read and avoiding a leading BOM that some log/JSON tooling rejects.
///     </para>
/// </summary>
internal static class AtomicFile
{
    /// <summary>Default number of write-and-move attempts before surfacing the failure.</summary>
    internal const int DefaultMaxAttempts = 5;

    /// <summary>Base backoff in milliseconds; scaled linearly by the attempt number.</summary>
    private const int BaseBackoffMilliseconds = 20;

    /// <summary>
    ///     UTF-8 encoding without a byte-order mark. Cached to avoid re-instantiating the encoder
    ///     per write; .NET's default <c>File.WriteAllText</c> emits a BOM that some downstream tools
    ///     (JSON validators, log parsers) reject.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    ///     Atomically writes <paramref name="contents"/> to <paramref name="path"/>.
    ///     Retries the temp-write-then-move on transient <see cref="IOException"/> /
    ///     <see cref="UnauthorizedAccessException"/> up to <paramref name="maxAttempts"/> times; if all
    ///     fail, the last transient error propagates so the caller's best-effort <c>try/catch</c> can log it.
    ///     <para>
    ///         Blocks the calling thread up to ~200 ms across all retries at the default attempt count.
    ///     </para>
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

        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }

        // Ensure the target directory exists before writing the temp file (CreateDirectory is idempotent).
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Fresh temp name per attempt avoids colliding with a temp file a prior attempt failed to clean up.
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, contents, Utf8NoBom);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, path);
                }

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
    ///     Asynchronous counterpart of <see cref="WriteAllText"/> for request-driven callers. Uses
    ///     <see cref="Task.Delay(int, CancellationToken)"/> for backoff so the caller's thread is released.
    ///     <para>
    ///         <b>Cancellation:</b> a signalled <paramref name="cancellationToken"/> stops further retries
    ///         and propagates <see cref="OperationCanceledException"/>, cleaning up any orphaned temp file
    ///         first (same "no orphans on disk" invariant as the sync overload).
    ///     </para>
    ///     <para>
    ///         <b>Semantic parity:</b> retry count, backoff schedule (20 / 40 / 60 / 80 ms across 5 attempts),
    ///         UTF-8 no-BOM encoding, and the temp-then-move atomicity rule are identical to
    ///         <see cref="WriteAllText"/>; the only difference is the yield point during backoff.
    ///     </para>
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

        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }

        // Ensure the target directory exists before writing the temp file.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fresh temp name per attempt avoids colliding with a temp file a prior attempt failed to clean up.
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // The async write honours the cancellation token natively.
                await File.WriteAllTextAsync(tempPath, contents, Utf8NoBom, cancellationToken).ConfigureAwait(false);

                // File.Replace/Move has no async overload; it's a fast same-directory metadata rename,
                // so blocking here for its sub-millisecond duration matches the sync overload's contract.
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, path);
                }

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

                try
                {
                    await Task.Delay(BaseBackoffMilliseconds * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation between attempts: stop retrying, propagate.
                    throw;
                }
            }
            catch
            {
                // Final attempt or non-transient error: remove the orphan temp file and propagate.
                TryDeleteQuietly(tempPath);
                throw;
            }
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