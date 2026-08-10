using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Construction-time contract for <see cref="DiscoveryCacheService"/>: the public two-arg
///     constructor resolves its file path from <c>Plugin.Instance.DataFolderPath</c>, so it must
///     fail fast when the singleton is not initialised rather than silently constructing a service
///     that writes to a bogus path.
/// </summary>
[Collection("ConfigOverride")]
public sealed class DiscoveryCacheServiceConstructionTests
{
    public DiscoveryCacheServiceConstructionTests()
    {
        ControllerTestFactory.InitializePluginInstance();
    }

    [Fact]
    public void Ctor_PluginInstanceNotInitialized_ThrowsInvalidOperationException()
    {
        ControllerTestFactory.TeardownPluginInstance();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new DiscoveryCacheService(
                new Mock<IPluginLogService>().Object,
                new Mock<ILogger<DiscoveryCacheService>>().Object));

            Assert.Contains("Plugin.Instance", ex.Message, StringComparison.Ordinal);
            Assert.Contains("data folder path", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ControllerTestFactory.InitializePluginInstance();
        }
    }
}
