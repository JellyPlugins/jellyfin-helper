using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.ConfigAccess;

/// <summary>
///     Tests for <see cref="PluginConfigurationService"/>. Because <c>Plugin.Instance</c> is a
///     process-wide singleton that other tests may or may not have initialised, every assertion
///     tolerates both states (null → fallback, non-null → delegation) so the outcome is not order-dependent.
/// </summary>
public class PluginConfigurationServiceTests
{
    private readonly PluginConfigurationService _sut = new();

    [Fact]
    public void IsInitialized_MatchesPluginInstancePresence()
    {
        Assert.Equal(Plugin.Instance is not null, _sut.IsInitialized);
    }

    [Fact]
    public void PluginVersion_ReturnsUnknownOrRealVersion()
    {
        var version = _sut.PluginVersion;
        Assert.False(string.IsNullOrWhiteSpace(version));

        if (Plugin.Instance is null)
        {
            Assert.Equal("unknown", version);
        }
        else
        {
            // With a real plugin, the version is the singleton's version string.
            Assert.Equal(Plugin.Instance.Version.ToString(), version);
        }
    }

    [Fact]
    public void GetConfiguration_NeverReturnsNull()
    {
        var cfg = _sut.GetConfiguration();
        Assert.NotNull(cfg);
        Assert.IsType<PluginConfiguration>(cfg);
    }

    [Fact]
    public void GetConfiguration_MatchesPluginInstanceWhenAvailable()
    {
        // With a real plugin the service returns the singleton's own config (same reference).
        // Without one, it must fall back to a fresh default instance.
        var cfg = _sut.GetConfiguration();

        if (Plugin.Instance is not null)
        {
            Assert.Same(Plugin.Instance.Configuration, cfg);
        }
    }

    [Fact]
    public void SaveConfiguration_DoesNotThrow()
    {
        // Both branches (null-safe no-op and delegate to Plugin.Instance.SaveConfiguration)
        // must complete without exception in a test host.
        var ex = Record.Exception(() => _sut.SaveConfiguration());
        Assert.Null(ex);
    }
}