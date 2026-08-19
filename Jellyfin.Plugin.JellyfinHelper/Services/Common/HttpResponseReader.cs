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
    ///     Reads the response body as a string, throwing <see cref="InvalidOperationException"/>
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
            throw new InvalidOperationException("Response too large");
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new LimitedStream(stream, maxBytes);
        using var reader = new StreamReader(limited);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     A read-only stream wrapper that throws once more than a fixed number of bytes
    ///     have been read from the inner stream.
    /// </summary>
    private sealed class LimitedStream : Stream
    {
        // CA2213 suppressed: _inner is a borrowed reference - the caller's `using var stream`
        // owns the lifetime. Disposing here would cause a double-dispose.
#pragma warning disable CA2213
        private readonly Stream _inner;
#pragma warning restore CA2213
        private readonly long _maxBytes;
        private long _bytesRead;

        public LimitedStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _maxBytes - _bytesRead;
            if (remaining <= 0)
            {
                throw new InvalidOperationException("Response too large");
            }

            var toRead = (int)Math.Min(count, remaining);
            var n = _inner.Read(buffer, offset, toRead);
            _bytesRead += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = _maxBytes - _bytesRead;
            if (remaining <= 0)
            {
                throw new InvalidOperationException("Response too large");
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            var n = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _bytesRead += n;
            return n;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
