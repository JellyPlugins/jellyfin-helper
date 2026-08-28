using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     A read-only, forward-only Stream wrapper that throws ResponseTooLargeException once more than a fixed number of bytes have been read from the inner stream.
/// </summary>
internal sealed class LimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly bool _leaveOpen;
    private long _bytesRead;

    /// <summary>Initializes a new instance of the <see cref="LimitedStream" /> class.</summary>
    /// <param name="inner">The underlying stream to read from.</param>
    /// <param name="maxBytes">The maximum number of bytes that may be read before throwing.</param>
    /// <param name="leaveOpen">
    ///     <see langword="true" /> to leave <paramref name="inner" /> open when this instance is
    ///     disposed (the caller owns its lifetime); <see langword="false" /> (the default) to dispose it.
    /// </param>
    public LimitedStream(Stream inner, long maxBytes, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        _inner = inner;
        _maxBytes = maxBytes;
        _leaveOpen = leaveOpen;
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

        // At the limit we must still allow the reader's final EOF probe: a response of EXACTLY _maxBytes bytes is valid.
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

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // Satisfies CA2213 without suppression: the inner disposable field IS disposed here, unless the caller opted to retain ownership via leaveOpen (mirroring StreamReader / CryptoStream / GZipStream semantics), which avoids a double-dispose.
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
