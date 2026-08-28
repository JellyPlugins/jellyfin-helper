using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
///     Verifies the Instance singleton lifecycle managed by ControllerTestFactory: initialize, teardown, reset, and idempotency.
/// </summary>
[Collection("ConfigOverride")]
public sealed class PluginSingletonLifecycleTests : IDisposable
{
    public void Dispose()
    {
        ControllerTestFactory.TeardownPluginInstance();
    }

    [Fact]
    public void InitializePluginInstance_SetsInstanceNonNull()
    {
        ControllerTestFactory.TeardownPluginInstance();
        Assert.Null(Plugin.Instance);

        ControllerTestFactory.InitializePluginInstance();

        Assert.NotNull(Plugin.Instance);
    }

    [Fact]
    public void TeardownPluginInstance_NullsInstance()
    {
        ControllerTestFactory.InitializePluginInstance();
        Assert.NotNull(Plugin.Instance);

        ControllerTestFactory.TeardownPluginInstance();

        Assert.Null(Plugin.Instance);
    }

    [Fact]
    public void TeardownPluginInstance_WhenAlreadyNull_IsIdempotent()
    {
        ControllerTestFactory.TeardownPluginInstance();
        // Must not throw.
        ControllerTestFactory.TeardownPluginInstance();
        Assert.Null(Plugin.Instance);
    }

    [Fact]
    public void InitializePluginInstance_CalledTwice_DoesNotThrow()
    {
        ControllerTestFactory.TeardownPluginInstance();
        ControllerTestFactory.InitializePluginInstance();
        var first = Plugin.Instance;

        // Second call is a no-op when instance is already set.
        ControllerTestFactory.InitializePluginInstance();

        Assert.Same(first, Plugin.Instance);
    }

    [Fact]
    public void ResetPluginConfiguration_RestoresDefaults()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.DiscoveryUserAccessEnabled = true;
        Plugin.Instance!.Configuration.PluginLogLevel = "ERROR";

        ControllerTestFactory.ResetPluginConfiguration();

        Assert.False(Plugin.Instance!.Configuration.DiscoveryUserAccessEnabled);
        Assert.NotEqual("ERROR", Plugin.Instance!.Configuration.PluginLogLevel);
    }

    [Fact]
    public void TeardownThenInitialize_ProducesFreshConfiguration()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.PluginLogLevel = "TRACE";

        ControllerTestFactory.TeardownPluginInstance();
        ControllerTestFactory.InitializePluginInstance();

        // After a full teardown + re-init, config must be default again.
        Assert.NotEqual("TRACE", Plugin.Instance!.Configuration.PluginLogLevel);
    }
}
