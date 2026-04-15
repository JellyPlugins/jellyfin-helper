using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

public class CleanupConfigHelperTests
{
    [Fact]
    public void GetConfig_ReturnsDefaultConfig_WhenPluginNotInitialized()
    {
        // When the config service reports not initialized, the helper should still
        // return a usable config (the service returns a default PluginConfiguration).
        var configServiceMock = new Mock<IPluginConfigurationService>();
        configServiceMock.Setup(s => s.IsInitialized).Returns(false);
        configServiceMock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration());

        var helper = new CleanupConfigHelper(configServiceMock.Object);
        var config = helper.GetConfig();
        Assert.NotNull(config);
    }

    [Fact]
    public void ParseCommaSeparated_EmptyInput_ReturnsEmpty()
    {
        var result = CleanupConfigHelper.ParseCommaSeparated(null);
        Assert.Empty(result);

        result = CleanupConfigHelper.ParseCommaSeparated("");
        Assert.Empty(result);

        result = CleanupConfigHelper.ParseCommaSeparated("   ");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseCommaSeparated_ValidInput_ReturnsParsedValues()
    {
        var result = CleanupConfigHelper.ParseCommaSeparated("Movies, TV Shows , Music");
        Assert.Equal(3, result.Count);
        Assert.Contains("Movies", result);
        Assert.Contains("TV Shows", result);
        Assert.Contains("Music", result);
    }

    [Fact]
    public void IsDryRun_ActivateMode_ReturnsFalse()
    {
        Assert.False(CleanupConfigHelper.IsDryRun(TaskMode.Activate));
    }

    [Fact]
    public void IsDryRun_DryRunMode_ReturnsTrue()
    {
        Assert.True(CleanupConfigHelper.IsDryRun(TaskMode.DryRun));
    }

    [Fact]
    public void IsDryRun_DeactivateMode_ReturnsTrue()
    {
        Assert.True(CleanupConfigHelper.IsDryRun(TaskMode.Deactivate));
    }
}