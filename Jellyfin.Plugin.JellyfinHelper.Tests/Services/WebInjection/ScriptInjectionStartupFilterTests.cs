using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.WebInjection;

/// <summary>
///     Tests for the request-time script injector that replaces the former File Transformation dependency.
/// </summary>
public class ScriptInjectionStartupFilterTests
{
    private const string Shell = "<html><body><h1>Jellyfin</h1></body></html>";

    [Fact]
    public void Configure_RegistersMiddlewareAndCallsNext()
    {
        var filter = CreateFilter();
        var nextCalled = false;
        var builder = new Mock<IApplicationBuilder>();
        builder.Setup(b => b.Use(It.IsAny<Func<RequestDelegate, RequestDelegate>>())).Returns(builder.Object);

        var pipeline = filter.Configure(_ => nextCalled = true);
        pipeline(builder.Object);

        builder.Verify(b => b.Use(It.IsAny<Func<RequestDelegate, RequestDelegate>>()), Times.Once);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Configure_WiresTheMiddlewareIntoThePipeline()
    {
        var filter = CreateFilter();
        var builder = new Mock<IApplicationBuilder>();
        RequestDelegate? composed = null;
        builder
            .Setup(b => b.Use(It.IsAny<Func<RequestDelegate, RequestDelegate>>()))
            .Callback<Func<RequestDelegate, RequestDelegate>>(middleware => composed = middleware(ctx => WriteAsync(ctx, Shell)))
            .Returns(builder.Object);

        filter.Configure(_ => { })(builder.Object);

        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        await composed!(context);

        Assert.Contains("plugin=\"Jellyfin Helper\"", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_InjectsTheScriptAndStripsValidatorsAndRangeHeaders()
    {
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        context.Response.Headers.ETag = "\"abc\"";
        context.Response.Headers.LastModified = "Wed, 21 Oct 2026 07:28:00 GMT";
        context.Response.Headers.AcceptRanges = "bytes";
        context.Request.Headers.AcceptEncoding = "gzip";
        context.Request.Headers.Range = "bytes=0-10";
        context.Request.Headers.IfRange = "\"abc\"";

        await CreateFilter().InvokeAsync(context, () => WriteAsync(context, Shell));

        var html = ReadBody(body);
        Assert.Contains("src=\"../JellyfinHelper/Discovery/My/script?v=1.2.3\"", html, StringComparison.Ordinal);
        Assert.Contains("</body>", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal)
                < html.IndexOf("</body>", StringComparison.Ordinal),
            "the tag must be inserted before the closing body tag");
        Assert.Equal("text/html;charset=utf-8", context.Response.ContentType);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(html), context.Response.ContentLength);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.False(context.Response.Headers.ContainsKey("Accept-Ranges"));
        Assert.False(context.Request.Headers.ContainsKey("Accept-Encoding"));
        Assert.False(context.Request.Headers.ContainsKey("Range"));
        Assert.False(context.Request.Headers.ContainsKey("If-Range"));
    }

    [Fact]
    public async Task Invoke_IsIdempotentWhenTheTagIsAlreadyPresent()
    {
        var html = "<html><body><script plugin=\"Jellyfin Helper\" version=\"0.9\" src=\"../JellyfinHelper/Discovery/My/script?v=0.9\"></script>\n</body></html>";
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        await CreateFilter().InvokeAsync(context, () => WriteAsync(context, html));

        var served = ReadBody(body);
        Assert.Equal(html, served);
        Assert.Equal(1, CountOccurrences(served, "plugin=\"Jellyfin Helper\""));
    }

    [Fact]
    public async Task Invoke_ServesHtmlWithoutClosingBodyTagUnchanged()
    {
        var html = "<html><body><h1>no close</h1></html>";
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        await CreateFilter().InvokeAsync(context, () => WriteAsync(context, html));

        Assert.Equal(html, ReadBody(body));
    }

    [Fact]
    public async Task Invoke_ServesOriginalHtmlWhenInjectionFails()
    {
        // A broken plugin version lookup must not break the shell; the middleware falls
        // back to the untouched document.
        var config = CreateConfig();
        config.SetupGet(c => c.PluginVersion).Throws<InvalidOperationException>();
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        await CreateFilter(config).InvokeAsync(context, () => WriteAsync(context, Shell));

        Assert.Equal(Shell, ReadBody(body));
    }

    [Fact]
    public async Task Invoke_PassesThroughNonHtmlResponsesUnchanged()
    {
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.Headers.ETag = "\"abc\"";

        await CreateFilter().InvokeAsync(context, () => WriteAsync(context, "{\"ok\":true}"));

        Assert.Equal("{\"ok\":true}", ReadBody(body));
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("\"abc\"", context.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Invoke_PassesThroughNonSuccessResponsesUnchanged()
    {
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status304NotModified;
        context.Response.ContentType = "text/html";

        await CreateFilter().InvokeAsync(context, () => WriteAsync(context, string.Empty));

        Assert.Equal(string.Empty, ReadBody(body));
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_PassesThroughCompressedResponsesUnchanged()
    {
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        context.Response.Headers.ContentEncoding = "gzip";

        await CreateFilter().InvokeAsync(context, () => WriteAsync(context, Shell));

        Assert.Equal(Shell, ReadBody(body));
    }

    [Theory]
    [InlineData("POST", "/web/")]
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/JellyfinHelper/Discovery/My/script")]
    [InlineData("HEAD", "/web/index.html")]
    public async Task Invoke_PassesThroughRequestsThatCannotBeRewritten(string method, string path)
    {
        var (context, body) = CreateContext(path, method);
        context.Request.Headers.AcceptEncoding = "gzip";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        await CreateFilter().InvokeAsync(context, () => WriteAsync(context, Shell));

        Assert.Equal(Shell, ReadBody(body));
        Assert.Equal("gzip", context.Request.Headers.AcceptEncoding.ToString());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Invoke_PassesThroughWhenDiscoveryIsUnavailable(bool initialized, bool enabled)
    {
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        await CreateFilter(CreateConfig(enabled, initialized)).InvokeAsync(context, () => WriteAsync(context, Shell));

        Assert.Equal(Shell, ReadBody(body));
    }

    [Fact]
    public async Task Invoke_PassesThroughWhenConfigurationCannotBeRead()
    {
        var config = CreateConfig();
        config.Setup(c => c.GetConfiguration()).Throws<InvalidOperationException>();
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        await CreateFilter(config).InvokeAsync(context, () => WriteAsync(context, Shell));

        Assert.Equal(Shell, ReadBody(body));
    }

    [Fact]
    public async Task Invoke_RestoresTheOriginalBodyAndRethrowsWhenDownstreamFails()
    {
        var (context, body) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateFilter().InvokeAsync(context, () => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
        Assert.Same(body, context.Response.Body);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task Invoke_LogsTheSuccessfulInjectionOnlyOnce()
    {
        var pluginLog = new Mock<IPluginLogService>();
        var filter = CreateFilter(pluginLog: pluginLog);

        for (var i = 0; i < 2; i++)
        {
            var (context, _) = CreateContext();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            await filter.InvokeAsync(context, () => WriteAsync(context, Shell));
        }

        pluginLog.Verify(
            l => l.LogInfo(
                "Discovery",
                It.Is<string>(message => message.Contains("Script injection active", StringComparison.Ordinal)),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task Invoke_LogsAnInjectionFailureOnceWithTheReason()
    {
        var config = CreateConfig();
        config.SetupGet(c => c.PluginVersion).Throws(new InvalidOperationException("version unavailable"));
        var pluginLog = new Mock<IPluginLogService>();
        var filter = CreateFilter(config, pluginLog);

        for (var i = 0; i < 2; i++)
        {
            var (context, _) = CreateContext();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            await filter.InvokeAsync(context, () => WriteAsync(context, Shell));
        }

        pluginLog.Verify(
            l => l.LogWarning(
                "Discovery",
                It.Is<string>(message => message.Contains("version unavailable", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task Invoke_LogsAMissingClosingBodyTagOnce()
    {
        var pluginLog = new Mock<IPluginLogService>();
        var filter = CreateFilter(pluginLog: pluginLog);
        var html = "<html><body><h1>no close</h1></html>";

        for (var i = 0; i < 2; i++)
        {
            var (context, _) = CreateContext();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            await filter.InvokeAsync(context, () => WriteAsync(context, html));
        }

        pluginLog.Verify(
            l => l.LogDebug(
                "Discovery",
                It.Is<string>(message => message.Contains("no closing body tag", StringComparison.Ordinal)),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task Invoke_LogsWhenTheConfigurationCannotBeRead()
    {
        var config = CreateConfig();
        config.Setup(c => c.GetConfiguration()).Throws(new InvalidOperationException("not ready"));
        var pluginLog = new Mock<IPluginLogService>();
        var (context, _) = CreateContext();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";

        await CreateFilter(config, pluginLog).InvokeAsync(context, () => WriteAsync(context, Shell));

        pluginLog.Verify(
            l => l.LogWarning(
                "Discovery",
                It.Is<string>(message => message.Contains("not ready", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task Invoke_InjectsForTheBareWebPathAndIndexHtml()
    {
        foreach (var path in new[] { "/web", "/web/", "/web/index.html", "/jellyfin/web/" })
        {
            var (context, body) = CreateContext(path);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";

            await CreateFilter().InvokeAsync(context, () => WriteAsync(context, Shell));

            Assert.Contains("plugin=\"Jellyfin Helper\"", ReadBody(body), StringComparison.Ordinal);
        }
    }

    private static ScriptInjectionStartupFilter CreateFilter(
        Mock<IPluginConfigurationService>? config = null,
        Mock<IPluginLogService>? pluginLog = null)
        => new(
            NullLogger<ScriptInjectionStartupFilter>.Instance,
            (config ?? CreateConfig()).Object,
            (pluginLog ?? new Mock<IPluginLogService>()).Object);

    private static Mock<IPluginConfigurationService> CreateConfig(bool enabled = true, bool initialized = true)
    {
        var mock = new Mock<IPluginConfigurationService>();
        mock.SetupGet(c => c.IsInitialized).Returns(initialized);
        mock.SetupGet(c => c.PluginVersion).Returns("1.2.3");
        mock.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration { DiscoveryUserAccessEnabled = enabled });
        return mock;
    }

    private static (HttpContext Context, MemoryStream Body) CreateContext(string path = "/web/", string method = "GET")
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = method;
        context.Request.Path = path;
        return (context, body);
    }

    private static Task WriteAsync(HttpContext context, string content)
        => context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(content)).AsTask();

    private static string ReadBody(MemoryStream body)
    {
        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
