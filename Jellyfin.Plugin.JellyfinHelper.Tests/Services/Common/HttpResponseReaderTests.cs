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
///     Tests for ReadLimitedAsync and its internal size-bounded stream. Contract: A body under the limit is returned verbatim.
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

    // HttpContent that reports a caller-supplied length while streaming a different (larger) payload,
    // modelling an upstream whose Content-Length header understates the real body.
    private sealed class LyingLengthContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly long _declaredLength;

        public LyingLengthContent(byte[] payload, long declaredLength)
        {
            _payload = payload;
            _declaredLength = declaredLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_payload, 0, _payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength;
            return true;
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
        // No Content-Length -> the streaming counter must catch the overflow (one byte past the limit).
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

    [Fact]
    public async Task ReadLimitedAsync_NonBomUtf16Charset_DecodesUsingDeclaredCharset()
    {
        // Regression guard: a UTF-16 response with NO byte-order mark would decode as garbage under the StreamReader UTF-8 default.
        const string body = "{\"title\":\"Ünïcödé ✓\"}";
        var payload = Encoding.Unicode.GetBytes(body);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-16",
        };

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 1024);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task ReadLimitedAsync_UnknownCharset_FallsBackToUtf8()
    {
        // An unrecognized charset name must not throw; the reader falls back to UTF-8 (with BOM
        // detection still enabled), so a plain UTF-8 body is returned verbatim.
        const string body = "{\"ok\":true}";
        var payload = Encoding.UTF8.GetBytes(body);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "not-a-real-charset",
        };

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 1024);

        Assert.Equal(body, result);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("utf-7")]
    [InlineData("UTF-7")]
    [InlineData("utf7")]
    public async Task ReadLimitedAsync_Utf7Charset_DecodedAsUtf8(string charset)
    {
        // utf-7 can smuggle markup past downstream filters; it is treated as unknown.
        const string body = "{\"ok\":true}";
        var payload = Encoding.UTF8.GetBytes(body);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = charset,
        };

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 1024);

        Assert.Equal(body, result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadLimitedAsync_LyingContentLength_StillBoundedByStream()
    {
        // Content-Length (16) passes the header fast-reject at the 32-byte limit, but the real body is 64
        // bytes. The streaming counter must still reject it rather than trusting the understated header.
        var payload = Encoding.ASCII.GetBytes(new string('c', 64));
        using var content = new LyingLengthContent(payload, declaredLength: 16);

        var ex = await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 32));
        Assert.Equal("Response too large", ex.Message);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadLimitedAsync_QuotedCharset_TrimmedAndHonored()
    {
        const string body = "{\"ok\":true}";
        var payload = Encoding.UTF8.GetBytes(body);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "\"utf-8\"",
        };

        var result = await HttpResponseReader.ReadLimitedAsync(content, CancellationToken.None, maxBytes: 1024);

        Assert.Equal(body, result);
    }
}
