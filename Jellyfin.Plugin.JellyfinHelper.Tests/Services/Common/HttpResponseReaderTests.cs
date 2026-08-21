using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Tests for <see cref="HttpResponseReader.ReadLimitedAsync" /> and its internal size-bounded
///     stream. Contract:
///     <list type="bullet">
///         <item>A body under the limit is returned verbatim.</item>
///         <item>A body of EXACTLY the limit is returned (the reader's final EOF probe must not throw).</item>
///         <item>A body over the limit throws <see cref="ResponseTooLargeException" />, whether detected
///               via the declared <c>Content-Length</c> (fast reject) or the streaming byte counter
///               (chunked / lying-length responses).</item>
///         <item>Null content and a cancelled token are surfaced as the expected exception types.</item>
///     </list>
/// </summary>
public sealed class HttpResponseReaderTests
{
    private const string TooLarge = "Response too large";

    // HttpContent whose stream deliberately does NOT expose a Content-Length, so the size limit
    // must be enforced by the streaming byte counter rather than the header fast-path.
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _payload;

        public UnknownLengthContent(byte[] payload) => _payload = payload;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_payload, 0, _payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            // Report "unknown" so ContentLength is null and the header fast-reject is skipped.
            length = 0;
            return false;
        }
    }

    private static HttpContent KnownLengthContent(byte[] payload) => new ByteArrayContent(payload);

    [Fact]
    public async Task ReadLimitedAsync_BodyUnderLimit_ReturnsBody()
    {
        using var content = new StringContent("hello world", Encoding.UTF8);

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 1024);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task ReadLimitedAsync_EmptyBody_ReturnsEmptyString()
    {
        using var content = KnownLengthContent([]);

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 16);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task ReadLimitedAsync_BodyExactlyAtLimit_ReturnsBody()
    {
        // Regression guard: a response of EXACTLY maxBytes must succeed. The reader's final
        // read at the boundary must be treated as an EOF probe, not an over-limit condition.
        var payload = Encoding.ASCII.GetBytes(new string('a', 32));
        using var content = new UnknownLengthContent(payload);

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 32);

        Assert.Equal(new string('a', 32), result);
    }

    [Fact]
    public async Task ReadLimitedAsync_StreamedBodyOverLimit_ThrowsViaByteCounter()
    {
        // No Content-Length → the streaming counter must catch the overflow (one byte past the limit).
        var payload = Encoding.ASCII.GetBytes(new string('b', 33));
        using var content = new UnknownLengthContent(payload);

        var ex = await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 32));
        Assert.Equal(TooLarge, ex.Message);
    }

    [Fact]
    public async Task ReadLimitedAsync_DeclaredContentLengthOverLimit_ThrowsFastReject()
    {
        // ByteArrayContent sets Content-Length, so the header fast-reject fires before any read.
        var payload = new byte[64];
        using var content = KnownLengthContent(payload);

        var ex = await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 32));
        Assert.Equal(TooLarge, ex.Message);
    }

    [Fact]
    public async Task ReadLimitedAsync_NullContent_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => HttpResponseReader.ReadLimitedAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ReadLimitedAsync_NegativeMaxBytes_ThrowsArgumentOutOfRange()
    {
        // A negative limit is invalid and must be rejected up front, before any header check or
        // read, so behaviour is consistent regardless of whether Content-Length is present.
        using var content = new StringContent("payload", Encoding.UTF8);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: -1));
    }

    [Fact]
    public async Task ReadLimitedAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var payload = Encoding.ASCII.GetBytes("some payload");
        using var content = new UnknownLengthContent(payload);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpResponseReader.ReadLimitedAsync(content, cts.Token, maxBytes: 1024));
    }

    [Fact]
    public async Task ReadLimitedAsync_DefaultMaxBytes_AcceptsTypicalBody()
    {
        // Sanity: the default 100 MiB cap comfortably admits a normal API payload.
        using var content = new StringContent("{\"ok\":true}", Encoding.UTF8);

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None);

        Assert.Equal("{\"ok\":true}", result);
    }

    [Fact]
    public void DefaultMaxBytes_Is100MiB()
        => Assert.Equal(100 * 1024 * 1024, HttpResponseReader.DefaultMaxBytes);
}
