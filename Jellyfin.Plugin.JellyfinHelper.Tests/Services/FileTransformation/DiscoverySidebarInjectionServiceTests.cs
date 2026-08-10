using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FileTransformation;

/// <summary>
///     Tests for <see cref="DiscoverySidebarInjectionService"/> - the startup hosted service that
///     re-runs the Discovery sidebar injection at a robust point in the Jellyfin boot sequence
///     (after DI is built and the web root is mounted), self-healing the disk-write fallback after
///     a web update.
///     <para>
///         These tests use a real temp filesystem because injection goes through
///         <see cref="Plugin.UpdateIndexHtml"/> → <c>AtomicFile</c>. Each test uses a unique temp
///         directory cleaned up in <see cref="IDisposable.Dispose"/>. The service reads
///         <see cref="Plugin.Instance"/>, so a fresh Plugin is constructed per test.
///     </para>
/// </summary>
[Collection("ConfigOverride")]
public sealed class DiscoverySidebarInjectionServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _webPath;
    private readonly string _dataPath;

    public DiscoverySidebarInjectionServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "JfhInjectionServiceTests_" + Guid.NewGuid().ToString("N"));
        _webPath = Path.Combine(_tempRoot, "web");
        _dataPath = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(_webPath);
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private Plugin CreatePlugin()
    {
        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: _dataPath, configPath: _dataPath);
        appPathsMock.Setup(p => p.WebPath).Returns(_webPath);
        var xmlSerializerMock = new Mock<IXmlSerializer>();
        var loggerMock = new Mock<ILogger<Plugin>>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return new Plugin(appPathsMock.Object, xmlSerializerMock.Object, loggerMock.Object);
    }

    private static DiscoverySidebarInjectionService CreateService()
    {
        var logger = new Mock<ILogger<DiscoverySidebarInjectionService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return new DiscoverySidebarInjectionService(logger.Object);
    }

    [Fact]
    public async Task StartAsync_ReInjectsScriptTagIntoIndexHtml()
    {
        // The hosted service must re-run injection at startup. On a writable web dir with the
        // File Transformation plugin absent, that means the fallback tag is (re)written to disk -
        // even if it had been stripped after the constructor ran (simulating a web update).
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><body></body></html>");

        var plugin = CreatePlugin(); // sets Plugin.Instance; ctor already injected once
        plugin.UpdateIndexHtml(false); // simulate a web update wiping our tag
        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", File.ReadAllText(indexPath), StringComparison.Ordinal);

        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var content = File.ReadAllText(indexPath);
        Assert.Contains("plugin=\"Jellyfin Helper\"", content, StringComparison.Ordinal);
        Assert.Contains("../JellyfinHelper/Discovery/My/script", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent_DoesNotStackTags()
    {
        // Running the constructor injection plus the hosted service (and a second start) must not
        // stack multiple <script> tags - the injection is idempotent via RemovalRegex.
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><body></body></html>");

        _ = CreatePlugin();
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var content = File.ReadAllText(indexPath);
        var occurrences = System.Text.RegularExpressions.Regex.Matches(content, "plugin=\"Jellyfin Helper\"").Count;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task StartAsync_WithNoPluginInstance_DoesNotThrow()
    {
        // Defensive: if Plugin.Instance is somehow null, the service must be a quiet no-op.
        // Force the instance to null via reflection so this test does not depend on construction order.
        var instanceProp = typeof(Plugin).GetProperty(
            nameof(Plugin.Instance),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(instanceProp);
        var previous = Plugin.Instance;
        try
        {
            instanceProp!.SetValue(null, null);
            var service = CreateService();

            var ex = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

            Assert.Null(ex);
        }
        finally
        {
            // Restore whatever instance was there so sibling tests are unaffected.
            instanceProp!.SetValue(null, previous);
        }
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutThrowing()
    {
        var service = CreateService();
        var ex = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));
        Assert.Null(ex);
    }
}
