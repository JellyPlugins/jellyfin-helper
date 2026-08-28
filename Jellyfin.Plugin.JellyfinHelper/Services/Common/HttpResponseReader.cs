using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Reads HTTP response bodies with a hard upper bound on the number of bytes buffered.
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
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        if (content.Headers.ContentLength is long len && len > maxBytes)
        {
            throw new ResponseTooLargeException();
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // leaveOpen: true - the response stream is owned by the enclosing `using` above, so the
        // wrapper must not dispose it a second time.
        using var limited = new LimitedStream(stream, maxBytes, leaveOpen: true);

        // Honor the charset declared by the upstream. A bare `new StreamReader(stream)` assumes UTF-8 when there is no BOM, so a non-BOM UTF-16 (or other-charset) response would decode to garbage and fail JSON parsing.
        var encoding = ResolveEncoding(content);
        using var reader = new StreamReader(limited, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves the response body encoding from the Content-Type charset, defaulting to UTF-8 when the charset is absent or not a recognized encoding name.
    /// </summary>
    /// <param name="content">The HTTP content whose declared charset is inspected.</param>
    /// <returns>The resolved <see cref="Encoding"/>, or <see cref="Encoding.UTF8"/> as a fallback.</returns>
    private static Encoding ResolveEncoding(HttpContent content)
    {
        var charSet = content.Headers.ContentType?.CharSet;
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return Encoding.UTF8;
        }

        // Some servers wrap the charset value in quotes (e.g. charset="utf-16"); trim them.
        charSet = charSet.Trim().Trim('"');

        try
        {
            return Encoding.GetEncoding(charSet);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
