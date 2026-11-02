using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.WebInjection;

/// <summary>
///     Exercises injection through the default file and static file middleware used to serve the web shell.
/// </summary>
public sealed class ScriptInjectionStaticFileTests
{
    private const string Shell = "<!doctype html><html><body>Jellyfin \u00e4\u4e2d</body></html>";

    [Theory]
    [InlineData("/web/")]
    [InlineData("/web/index.html")]
    [InlineData("/jellyfin/web/")]
    [InlineData("/jellyfin/web/index.html")]
    public async Task StaticFiles_InjectsTheShellAndResolvesTheScriptUnderTheBasePath(string path)
    {
        using var fixture = new StaticFileFixture(Shell);
        var (context, body) = await fixture.GetAsync(path);
        using (body)
        {
            var html = Encoding.UTF8.GetString(body.ToArray());
            var script = DiscoveryScriptTag.Build("1.2.3");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Contains(script, html, StringComparison.Ordinal);
            Assert.Contains("Jellyfin \u00e4\u4e2d", html, StringComparison.Ordinal);
            Assert.Equal(body.Length, context.Response.ContentLength);
            Assert.Equal(0, context.Response.Headers.ETag.Count);
            var url = new Uri(new Uri("http://localhost" + path), DiscoveryScriptTag.ScriptBaseUrl);
            var prefix = path.StartsWith("/jellyfin/", StringComparison.Ordinal) ? "/jellyfin" : string.Empty;
            Assert.Equal(prefix + "/JellyfinHelper/Discovery/My/script", url.AbsolutePath);
            Assert.Equal(Shell, await File.ReadAllTextAsync(fixture.IndexPath, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("/web")]
    [InlineData("/jellyfin/web")]
    public async Task DefaultFiles_RedirectsTheBareDirectoryBeforeRelativeScriptResolution(string path)
    {
        using var fixture = new StaticFileFixture(Shell);
        var (context, body) = await fixture.GetAsync(path);
        using (body)
        {
            Assert.InRange(context.Response.StatusCode, 300, 399);
            Assert.EndsWith(path + "/", context.Response.Headers.Location.ToString(), StringComparison.Ordinal);
            Assert.Empty(body.ToArray());
        }
    }

    [Fact]
    public async Task StaticFiles_IgnoresPartialAndConditionalHeadersWhenRewriting()
    {
        using var fixture = new StaticFileFixture(Shell);
        fixture.Enabled = false;
        var (cached, cachedBody) = await fixture.GetAsync("/web/index.html");
        using (cachedBody)
        {
            var etag = cached.Response.Headers.ETag.ToString();
            Assert.NotEmpty(etag);
            fixture.Enabled = true;
            var (context, body) = await fixture.GetAsync("/web/index.html", request =>
            {
                request.Headers.IfNoneMatch = etag;
                request.Headers.IfModifiedSince = cached.Response.Headers.LastModified;
                request.Headers.Range = "bytes=0-10";
                request.Headers.IfRange = etag;
                request.Headers.AcceptEncoding = "gzip, br";
            });
            using (body)
            {
                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
                Assert.Contains(DiscoveryScriptTag.Build("1.2.3"), Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
                Assert.Equal(0, context.Response.Headers.ContentEncoding.Count);
                Assert.Equal(0, context.Response.Headers.AcceptRanges.Count);
                Assert.Equal(0, context.Response.Headers.ETag.Count);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaticFiles_HandlesReadOnlyLegacyShellsWithoutChangingDisk(bool enabled)
    {
        var legacy = Shell.Replace("</body>", DiscoveryScriptTag.Build("0.9") + "\n" + DiscoveryScriptTag.Build("0.8") + "\n</body>", StringComparison.Ordinal);
        using var fixture = new StaticFileFixture(legacy);
        fixture.Enabled = enabled;
        File.SetAttributes(fixture.IndexPath, FileAttributes.ReadOnly);
        var (context, body) = await fixture.GetAsync("/web/index.html");
        using (body)
        {
            var html = Encoding.UTF8.GetString(body.ToArray());
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.DoesNotContain("v=0.9", html, StringComparison.Ordinal);
            Assert.DoesNotContain("v=0.8", html, StringComparison.Ordinal);
            Assert.Equal(enabled ? 1 : 0, html.Split("plugin=\"Jellyfin Helper\"", StringSplitOptions.None).Length - 1);
            Assert.Equal(legacy, await File.ReadAllTextAsync(fixture.IndexPath, TestContext.Current.CancellationToken));
        }
    }

    private sealed class StaticFileFixture : IDisposable
    {
        private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-static-files-" + Guid.NewGuid().ToString("N"));
        private readonly PhysicalFileProvider _files;
        private readonly ServiceProvider _services;
        private readonly RequestDelegate _pipeline;
        private readonly PluginConfiguration _configuration = new() { DiscoveryUserAccessEnabled = true };

        public StaticFileFixture(string html)
        {
            Directory.CreateDirectory(_directory);
            IndexPath = Path.Join(_directory, "index.html");
            File.WriteAllText(IndexPath, html, new UTF8Encoding(false));
            _files = new PhysicalFileProvider(_directory);
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(value => value.WebRootFileProvider).Returns(_files);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddSingleton(environment.Object);
            _services = services.BuildServiceProvider();
            var config = new Mock<IPluginConfigurationService>();
            config.SetupGet(value => value.IsInitialized).Returns(true);
            config.SetupGet(value => value.PluginVersion).Returns("1.2.3");
            config.Setup(value => value.GetConfiguration()).Returns(_configuration);
            var filter = new ScriptInjectionStartupFilter(NullLogger<ScriptInjectionStartupFilter>.Instance, config.Object, Mock.Of<IPluginLogService>());
            var app = new ApplicationBuilder(_services);
            filter.Configure(next =>
            {
                next.UsePathBase("/jellyfin");
                next.UseDefaultFiles(new DefaultFilesOptions { FileProvider = _files, RequestPath = "/web" });
                next.UseStaticFiles(new StaticFileOptions { FileProvider = _files, RequestPath = "/web" });
            })(app);
            _pipeline = app.Build();
        }

        public string IndexPath { get; }

        public bool Enabled
        {
            set => _configuration.DiscoveryUserAccessEnabled = value;
        }

        public async Task<(HttpContext Context, MemoryStream Body)> GetAsync(string path, Action<HttpRequest>? configure = null)
        {
            var context = new DefaultHttpContext { RequestServices = _services, RequestAborted = TestContext.Current.CancellationToken };
            var body = new MemoryStream();
            context.Response.Body = body;
            context.Request.Method = "GET";
            context.Request.Path = path;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            configure?.Invoke(context.Request);
            await _pipeline(context);
            return (context, body);
        }

        public void Dispose()
        {
            _services.Dispose();
            _files.Dispose();
            File.SetAttributes(IndexPath, FileAttributes.Normal);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
