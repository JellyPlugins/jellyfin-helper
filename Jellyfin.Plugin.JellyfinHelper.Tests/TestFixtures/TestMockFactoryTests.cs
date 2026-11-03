using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>Tests the configuration fixture's argument and callback contracts.</summary>
public sealed class TestMockFactoryTests
{
    [Fact]
    public void SetupReadAndMutate_NullMock_ThrowsWithParameterName()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            TestMockFactory.SetupReadAndMutate(null!, new PluginConfiguration()));

        Assert.Equal("mock", exception.ParamName);
    }

    [Fact]
    public void SetupReadAndMutate_NullConfig_ThrowsWithParameterName()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            TestMockFactory.SetupReadAndMutate(new Mock<IPluginConfigurationService>(), null!));

        Assert.Equal("cfg", exception.ParamName);
    }

    [Fact]
    public void SetupReadAndMutate_CallbackMutatesProvidedConfiguration()
    {
        var config = new PluginConfiguration();
        var mock = new Mock<IPluginConfigurationService>();
        TestMockFactory.SetupReadAndMutate(mock, config);

        mock.Object.ReadAndMutate(current => current.PluginLogLevel = "DEBUG");

        Assert.Equal("DEBUG", config.PluginLogLevel);
    }
}
