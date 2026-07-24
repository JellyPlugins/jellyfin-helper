using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Writes text files using write-to-temp then File.Replace/Move with a small,
///     bounded retry on transient I/O failures.
///     When the destination already exists, <c>File.Replace</c> is used (atomic via
///     <c>ReplaceFileW</c> on Windows); when the destination is new, <c>File.Move</c>
///     is used. Note that on Windows, File.Move with overwrite is NOT atomic when the
///     destination exists (it deletes then renames), which is why File.Replace is preferred.
///     <para>
///         <b>Threading model:</b> Two entry points share the same retry contract:
///         <list type="bullet">
///             <item>
///                 <description>
///                     <see cref="WriteAllText"/> — synchronous, uses <see cref="Thread.Sleep(int)"/> for
///                     retry backoff. A fully retrying call blocks the caller for up to ~200 ms with the
///                     default 5 attempts (4 sleeps: 20 + 40 + 60 + 80 ms; the final attempt
///                     propagates immediately without sleeping). Intended for background scheduled-task
///                     paths where thread blocking is acceptable.
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     <see cref="WriteAllTextAsync"/> — asynchronous, uses
///                     <see cref="Task.Delay(int, CancellationToken)"/> for backoff so the caller's request
///                     thread is released while the retry sleeps. Required for ASP.NET request handlers
///                     (e.g. discovery dismissal / requested-state persistence) which invoke this from
///                     latency-sensitive contexts. Honours the passed <see cref="CancellationToken"/> so a
///                     cancelled request stops retrying instead of blocking the thread pool.
///                 </description>
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Encoding:</b> Files are written as UTF-8 <i>without</i> a byte-order mark, matching
///         what <c>System.Text.Json</c> expects on read and staying compatible with external
///         log/JSON tooling that treats a BOM as unexpected input.
///     </para>
/// </summary>
internal static class AtomicFile
{
    /// <summary>Default number of write-and-move attempts before surfacing the failure.</summary>
    internal const int DefaultMaxAttempts = 5;

    /// <summary>Base backoff in milliseconds; scaled linearly by the attempt number.</summary>
    private const int BaseBackoffMilliseconds = 20;

    /// <summary>
    ///     UTF-8 encoding without a byte-order mark. Cached to avoid re-instantiating the
    ///     encoder on every write; .NET's default <c>File.WriteAllText(path, contents)</c>
    ///     uses UTF-8 <i>with</i> BOM which some downstream tools (JSON validators, log
    ///     parsers) treat as an unexpected first character.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    ///     Atomically writes <paramref name="contents"/> to <paramref name="path"/>.
    ///     Retries the temp-write-then-move on transient <see cref="IOException"/> /
    ///     <see cref="UnauthorizedAccessException"/> up to <paramref name="maxAttempts"/> times.
    ///     If every attempt fails, the last transient error propagates so the caller's existing
    ///     best-effort <c>try/catch</c> can log it — behavior is never worse than a single attempt.
    ///     <para>
    ///         Blocks the calling thread for up to ~200 ms across all retries with the default
    ///         attempt count. See the class-level remarks for the constraint on caller contexts.
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

        // Ensure the target directory exists before attempting to write the temp file.
        // CreateDirectory is idempotent — it does nothing if the directory already exists.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // A fresh temp name per attempt avoids colliding with a temp file left behind
            // by a prior attempt whose cleanup itself failed transiently.
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Explicit UTF-8 (no BOM) — see the class-level Encoding note.
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
                // Transient lock (AV/indexer). Clean up this attempt's temp file and back off
                // briefly before retrying; the lock almost always clears within tens of ms.
                TryDeleteQuietly(tempPath);
                Thread.Sleep(BaseBackoffMilliseconds * attempt);
            }
            catch
            {
                // Final attempt, or a non-transient error: remove the orphan temp file and
                // let the exception propagate to the caller's diagnostic handler.
                TryDeleteQuietly(tempPath);
                throw;
            }
        }
    }

    /// <summary>
    ///     Asynchronous counterpart of <see cref="WriteAllText"/> for request-driven callers.
    ///     Uses <see cref="File.WriteAllTextAsync(string, string, System.Text.Encoding, CancellationToken)"/>
    ///     for the actual write and <see cref="Task.Delay(int, CancellationToken)"/> for retry backoff
    ///     so the caller's thread is released while the retry sleeps.
    ///     <para>
    ///         <b>Cancellation behaviour:</b> a signalled <paramref name="cancellationToken"/> stops
    ///         further retries and propagates <see cref="OperationCanceledException"/>. Any orphaned
    ///         temp file from the last attempt is cleaned up before propagation, mirroring the
    ///         synchronous overload's "no orphans on the disk" invariant.
    ///     </para>
    ///     <para>
    ///         <b>Semantic parity:</b> retry count, backoff schedule (4 sleeps: 20 / 40 / 60 / 80 ms across 5 attempts), UTF-8
    ///         no-BOM encoding, and the temp-then-move atomicity rule are identical to
    ///         <see cref="WriteAllText"/>; the only difference is the yield point during backoff.
    ///         A caller that already handles exceptions from the sync overload can switch to this
    ///         one without changing its error-handling contract, only adding <c>await</c>.
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

        // Ensure the target directory exists before attempting to write the temp file.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A fresh temp name per attempt avoids colliding with a temp file left behind
            // by a prior attempt whose cleanup itself failed transiently.
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Explicit UTF-8 (no BOM) — see the class-level Encoding note. The async write
                // also honours the cancellation token natively, so a cancellation while writing
                // the temp file will propagate through here without needing the outer check.
                await File.WriteAllTextAsync(tempPath, contents, Utf8NoBom, cancellationToken).ConfigureAwait(false);

                // File.Replace/Move has no async overload in .NET 8/9. It's a fast metadata
                // operation (rename within the same directory), so blocking here for the
                // sub-millisecond duration is acceptable and identical to the sync overload's
                // contract. File.Replace is used when the destination exists (atomic via
                // ReplaceFileW on Windows); File.Move is used for new files.
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
                // Cancellation during the async write: clean up the temp file and propagate.
                // We do NOT retry on cancellation — that would defeat cooperative cancellation.
                TryDeleteQuietly(tempPath);
                throw;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                // Transient lock (AV/indexer). Clean up this attempt's temp file and back off
                // briefly before retrying; the lock almost always clears within tens of ms.
                TryDeleteQuietly(tempPath);

                try
                {
                    await Task.Delay(BaseBackoffMilliseconds * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation between attempts is honoured: stop retrying, propagate.
                    throw;
                }
            }
            catch
            {
                // Final attempt, or a non-transient error: remove the orphan temp file and
                // let the exception propagate to the caller's diagnostic handler.
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