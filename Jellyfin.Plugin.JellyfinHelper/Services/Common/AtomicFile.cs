using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Writes text files atomically (write-to-temp then move-over-target) with a small,
///     bounded retry on transient I/O failures.
///     <para>
///         <b>Threading:</b> The current implementation is synchronous and uses
///         <see cref="Thread.Sleep(int)"/> between retry attempts, so a fully retrying call
///         can block the caller for up to ~200 ms (20 ms + 40 ms + 60 ms + 80 ms with the
///         default 5 attempts). This is safe for the current callers — every one of them
///         runs on a background scheduled-task path (weight / state persistence) — but the
///         method MUST NOT be invoked from an ASP.NET request handler or any other latency-
///         sensitive context. If such a caller ever appears, add a
///         <c>WriteAllTextAsync</c> overload that uses <c>await Task.Delay(...)</c>
///         instead of <see cref="Thread.Sleep(int)"/>.
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

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // A fresh temp name per attempt avoids colliding with a temp file left behind
            // by a prior attempt whose cleanup itself failed transiently.
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Explicit UTF-8 (no BOM) — see the class-level Encoding note.
                File.WriteAllText(tempPath, contents, Utf8NoBom);
                File.Move(tempPath, path, overwrite: true);
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

    /// <summary>Deletes a temp file, swallowing the non-critical I/O errors that cleanup can raise.</summary>
    private static void TryDeleteQuietly(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            // best effort - temp file cleanup is non-critical
        }
        catch (UnauthorizedAccessException)
        {
            // best effort - temp file cleanup is non-critical
        }
    }
}