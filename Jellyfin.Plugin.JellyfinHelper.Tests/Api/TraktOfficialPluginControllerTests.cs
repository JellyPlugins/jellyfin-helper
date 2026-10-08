using Jellyfin.Plugin.JellyfinHelper.Api;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Verifies the official-Trakt-plugin status endpoint reports the reader's presence probe to admins.
///     Lives in its own controller (split from <see cref="TraktController"/>) so each admin Trakt concern has
///     exactly one controller.
/// </summary>
public class TraktOfficialPluginControllerTests
{
    private readonly Mock<Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.IOfficialTraktPluginReader> _officialPlugin = new();
    private readonly TraktOfficialPluginController _controller;

    public TraktOfficialPluginControllerTests()
    {
        _controller = new TraktOfficialPluginController(_officialPlugin.Object);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetOfficialPluginStatus_ReflectsReaderPresence(bool present)
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(present);

        var result = _controller.GetOfficialPluginStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<OfficialTraktPluginStatusResponse>(ok.Value);
        Assert.Equal(present, body.Present);
    }
}
