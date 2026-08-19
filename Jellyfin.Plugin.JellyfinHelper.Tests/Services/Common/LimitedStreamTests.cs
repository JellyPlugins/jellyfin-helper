using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Direct tests for <see cref="LimitedStream" /> covering every member: capability flags, the
///     synchronous and asynchronous read paths (including the exactly-at-limit EOF probe and the
///     over-limit throw), and the write/seek members that must throw <see cref="NotSupportedException" />.
/// </summary>
public sealed class LimitedStreamTests
{
    private static MemoryStream Bytes(int count) => new(Encoding.ASCII.GetBytes(new string('x', count)));

    [Fact]
    public void CapabilityFlags_ReadOnlyForwardOnly()
    {
        using var inner = Bytes(4);
        using var sut = new LimitedStream(inner, 16);

        Assert.True(sut.CanRead);
        Assert.False(sut.CanSeek);
        Assert.False(sut.CanWrite);
    }

    [Fact]
    public void Read_UnderLimit_ReturnsBytes()
    {
        using var inner = Bytes(4);
        using var sut = new LimitedStream(inner, 16);
        var buffer = new byte[8];

        var total = 0;
        int n;
        while ((n = sut.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += n;
        }

        Assert.Equal(4, total);
    }

    [Fact]
    public void Read_ZeroCount_ReturnsZeroWithoutTouchingInner()
    {
        using var inner = Bytes(4);
        using var sut = new LimitedStream(inner, 16);

        Assert.Equal(0, sut.Read(new byte[4], 0, 0));
    }

    [Fact]
    public void Read_OverLimit_Throws()
    {
        using var inner = Bytes(33);
        using var sut = new LimitedStream(inner, 32);
        var buffer = new byte[64];

        // Drain until the limit is exceeded; the 33rd byte trips the guard.
        Assert.Throws<ResponseTooLargeException>(() =>
        {
            int n;
            do
            {
                n = sut.Read(buffer, 0, buffer.Length);
            }
            while (n > 0);
        });
    }

    [Fact]
    public void Read_ExactlyAtLimit_SucceedsAtEofProbe()
    {
        using var inner = Bytes(32);
        using var sut = new LimitedStream(inner, 32);
        var buffer = new byte[64];

        var total = 0;
        int n;
        while ((n = sut.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
        }

        Assert.Equal(32, total); // final read returns 0 (EOF), not a throw
    }

    [Fact]
    public async Task ReadAsync_ByteArrayOverload_UnderLimit_ReturnsBytes()
    {
        using var inner = Bytes(4);
        using var sut = new LimitedStream(inner, 16);
        var buffer = new byte[8];

        var total = 0;
        int n;
        while ((n = await sut.ReadAsync(buffer, total, buffer.Length - total, CancellationToken.None)) > 0)
        {
            total += n;
        }

        Assert.Equal(4, total);
    }

    [Fact]
    public async Task ReadAsync_ByteArrayOverload_OverLimit_Throws()
    {
        using var inner = Bytes(33);
        using var sut = new LimitedStream(inner, 32);
        var buffer = new byte[64];

        await Assert.ThrowsAsync<ResponseTooLargeException>(async () =>
        {
            int n;
            do
            {
                n = await sut.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            }
            while (n > 0);
        });
    }

    [Fact]
    public async Task ReadAsync_ZeroLength_ReturnsZero()
    {
        using var inner = Bytes(4);
        using var sut = new LimitedStream(inner, 16);

        var read = await sut.ReadAsync(Array.Empty<byte>().AsMemory(), CancellationToken.None);
        Assert.Equal(0, read);
    }

    [Fact]
    public void Length_Throws()
    {
        using var inner = Bytes(1);
        using var sut = new LimitedStream(inner, 16);
        Assert.Throws<NotSupportedException>(() => _ = sut.Length);
    }

    [Fact]
    public void Position_GetAndSet_Throw()
    {
        using var inner = Bytes(1);
        using var sut = new LimitedStream(inner, 16);
        Assert.Throws<NotSupportedException>(() => _ = sut.Position);
        Assert.Throws<NotSupportedException>(() => sut.Position = 0);
    }

    [Fact]
    public void Flush_Seek_SetLength_Write_Throw()
    {
        using var inner = Bytes(1);
        using var sut = new LimitedStream(inner, 16);
        Assert.Throws<NotSupportedException>(() => sut.Flush());
        Assert.Throws<NotSupportedException>(() => sut.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => sut.SetLength(0));
        Assert.Throws<NotSupportedException>(() => sut.Write(new byte[1], 0, 1));
    }

    [Fact]
    public void ResponseTooLargeException_Constructors_SetMessageAndInner()
    {
        Assert.Equal("Response too large", new ResponseTooLargeException().Message);
        Assert.Equal("custom", new ResponseTooLargeException("custom").Message);

        var inner = new InvalidOperationException("root cause");
        var wrapped = new ResponseTooLargeException("wrapped", inner);
        Assert.Equal("wrapped", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }
}
