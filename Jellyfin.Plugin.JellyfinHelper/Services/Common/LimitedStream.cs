using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     A read-only, forward-only <see cref="Stream" /> wrapper that throws
///     <see cref="ResponseTooLargeException" /> once more than a fixed number of bytes have been
///     read from the inner stream. Used by <see cref="HttpResponseReader" /> to cap HTTP response
///     bodies. The inner stream's lifetime is owned by the caller, not by this wrapper.
/// </summary>
internal sealed class LimitedStream : Stream
{
    // CA2213 suppressed: _inner is a borrowed reference - the caller owns its lifetime.
    // Disposing here would cause a double-dispose.
#pragma warning disable CA2213
    private readonly Stream _inner;
#pragma warning restore CA2213
    private readonly long _maxBytes;
    private long _bytesRead;

    /// <summary>Initializes a new instance of the <see cref="LimitedStream" /> class.</summary>
    /// <param name="inner">The underlying stream to read from (not owned by this instance).</param>
    /// <param name="maxBytes">The maximum number of bytes that may be read before throwing.</param>
    public LimitedStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _maxBytes = maxBytes;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var remaining = _maxBytes - _bytesRead;

        // At the limit we must still allow the reader's final EOF probe: a response of EXACTLY
        // _maxBytes bytes is valid. Read a single byte — if it returns data the response is
        // genuinely over the limit; if it returns 0 (EOF) the read is fine.
        var toRead = remaining <= 0 ? 1 : (int)Math.Min(count, remaining);
        var n = _inner.Read(buffer, offset, toRead);
        if (remaining <= 0 && n > 0)
        {
            throw new ResponseTooLargeException();
        }

        _bytesRead += n;
        return n;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var remaining = _maxBytes - _bytesRead;

        // See Read(): allow a single-byte EOF probe at the limit so an exactly-_maxBytes response
        // is accepted, and only throw if that probe actually returns data.
        var toRead = remaining <= 0 ? 1 : (int)Math.Min(buffer.Length, remaining);
        var n = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        if (remaining <= 0 && n > 0)
        {
            throw new ResponseTooLargeException();
        }

        _bytesRead += n;
        return n;
    }

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
