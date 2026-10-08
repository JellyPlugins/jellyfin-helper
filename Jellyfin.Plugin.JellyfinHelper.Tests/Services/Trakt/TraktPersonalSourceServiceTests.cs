using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the personal-source service sourcing Trakt exclusively through the official Trakt plugin's token:
///     resolution, the cheap link check, availability, and the warmable-user enumeration. No network is touched
///     and no Plugin singleton is needed: the configuration is passed in as a parameter.
/// </summary>
public sealed class TraktPersonalSourceServiceTests
{
    private static readonly DateTimeOffset FarFuture = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IOfficialTraktPluginReader> _officialPlugin = new();

    private TraktPersonalSourceService CreateService()
        => new(_officialPlugin.Object, () => FarFuture);

    [Fact]
    public void Ctor_NullDependency_Throws()
        => Assert.Throws<ArgumentNullException>(() => new TraktPersonalSourceService(null!));

    [Fact]
    public async Task ResolveAsync_NullConfig_Throws()
    {
        var sut = CreateService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.ResolveAsync(Guid.NewGuid(), null!, CancellationToken.None));
    }

    [Fact]
    public void IsLinked_NullConfig_Throws()
    {
        var sut = CreateService();

        Assert.Throws<ArgumentNullException>(() => sut.IsLinked(Guid.NewGuid(), null!));
    }

    [Fact]
    public void IsAvailable_NullConfig_Throws()
    {
        var sut = CreateService();

        Assert.Throws<ArgumentNullException>(() => sut.IsAvailable(null!));
    }

    [Fact]
    public async Task ResolveAsync_OfficialPresent_ReturnsOfficialSource()
    {
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new OfficialTraktToken("official-tok", FarFuture));
        var sut = CreateService();

        var source = await sut.ResolveAsync(userId, new PluginConfiguration(), CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal(OfficialTraktPluginReader.OfficialTraktClientId, source!.ClientId);
        Assert.Equal("official-tok", source.AccessToken);
    }

    [Fact]
    public async Task ResolveAsync_OfficialAbsent_ReturnsNull()
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        Assert.Null(await sut.ResolveAsync(Guid.NewGuid(), new PluginConfiguration(), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_OfficialPresentButNoToken_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns((OfficialTraktToken?)null);
        var sut = CreateService();

        Assert.Null(await sut.ResolveAsync(userId, new PluginConfiguration(), CancellationToken.None));
    }

    [Fact]
    public void IsLinked_OfficialHasToken_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new OfficialTraktToken("official-tok", FarFuture));
        var sut = CreateService();

        Assert.True(sut.IsLinked(userId, new PluginConfiguration()));
    }

    [Fact]
    public void IsLinked_OfficialAbsent_ReturnsFalse()
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        Assert.False(sut.IsLinked(Guid.NewGuid(), new PluginConfiguration()));
    }

    [Fact]
    public void IsAvailable_OfficialPresent_ReturnsTrue()
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        var sut = CreateService();

        Assert.True(sut.IsAvailable(new PluginConfiguration()));
    }

    [Fact]
    public void IsAvailable_OfficialAbsent_ReturnsFalse()
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        Assert.False(sut.IsAvailable(new PluginConfiguration()));
    }

    [Fact]
    public void GetLinkedUserIds_OfficialPresent_ReturnsDedupedUsers()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>())).Returns(new[] { a, b, a });
        var sut = CreateService();

        var ids = sut.GetLinkedUserIds();

        Assert.Equal(2, ids.Count);
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
    }

    [Fact]
    public void GetLinkedUserIds_OfficialAbsent_ReturnsEmpty()
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        Assert.Empty(sut.GetLinkedUserIds());
        _officialPlugin.Verify(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>()), Times.Never);
    }
}
