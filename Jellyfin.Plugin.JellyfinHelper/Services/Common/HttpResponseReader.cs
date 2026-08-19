using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Reads HTTP response bodies with a hard upper bound on the number of bytes buffered.
///     Prevents a hostile or misbehaving upstream (Seerr/Arr, or an SSRF-reachable target)
///     from causing an out-of-memory condition by returning an unbounded response body.
///     Enforces the limit both via the declared <c>Content-Length</c> header (fast reject)
///     and via a streaming byte counter (defeats chunked-encoding / lying-length responses).
/// </summary>
internal static class HttpResponseReader
{
    /// <summary>
    ///     Default maximum response size (100 MiB), matching the Arr integration cap.
    /// </summary>
    public const int DefaultMaxBytes = 100 * 1024 * 1024;

    /// <summary>
    ///     Reads the response body as a string, throwing <see cref="ResponseTooLargeException"/>
    ///     if it exceeds <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="content">The HTTP content to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="maxBytes">The maximum number of bytes to buffer. Defaults to <see cref="DefaultMaxBytes"/>.</param>
    /// <returns>The decoded response body.</returns>
    public static async Task<string> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Headers.ContentLength is long len && len > maxBytes)
        {
            throw new ResponseTooLargeException();
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // leaveOpen: true - the response stream is owned by the enclosing `using` above, so the
        // wrapper must not dispose it a second time.
        using var limited = new LimitedStream(stream, maxBytes, leaveOpen: true);
        using var reader = new StreamReader(limited);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
