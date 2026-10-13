using Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt.External;

/// <summary>
///     Pins the official Trakt plugin identifiers the Helper couples to. These are a contract with the external
///     plugin: the GUID is its stable <c>Plugin.Id</c> and the file name is where Jellyfin persists its config.
///     A silent change to either would break presence detection or token reading, so they are asserted explicitly.
/// </summary>
public sealed class OfficialTraktPluginGuidsTests
{
    [Fact]
    public void PluginId_MatchesOfficialTraktPluginGuid()
    {
        Assert.Equal("4fe3201e-d6ae-4f2e-8917-e12bda571281", OfficialTraktPluginGuids.PluginId.ToString());
    }

    [Fact]
    public void ConfigFileName_IsTraktXml()
    {
        Assert.Equal("Trakt.xml", OfficialTraktPluginGuids.ConfigFileName);
    }
}
