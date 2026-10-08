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
///     Verifies the personal-source lifecycle: strict own-link precedence (an own-linked user never falls back to
///     the official plugin, even with a dead own token), the official-plugin fallback for users without an own
///     link, the cheap link check (no token refresh), and the merged warm population. No network is touched and
///     no Plugin singleton is needed: the configuration is passed in as a parameter.
/// </summary>
public sealed class TraktPersonalSourceServiceTests
{
    private static readonly DateTimeOffset FarFuture = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<ITraktUserStore> _store = new();
    private readonly Mock<ITraktAuthService> _auth = new();
    private readonly Mock<IOfficialTraktPluginReader> _officialPlugin = new();

    private static PluginConfiguration ConfigWithOwnCreds()
        => new() { TraktEnabled = true, TraktClientId = "client-id" };

    private TraktPersonalSourceService CreateService()
        => new(_store.Object, _auth.Object, _officialPlugin.Object, () => FarFuture);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Ctor_NullDependency_Throws(int index)
    {
        var store = new Mock<ITraktUserStore>().Object;
        var auth = new Mock<ITraktAuthService>().Object;
        var official = new Mock<IOfficialTraktPluginReader>().Object;

        Assert.Throws<ArgumentNullException>(() => index switch
        {
            0 => new TraktPersonalSourceService(null!, auth, official),
            1 => new TraktPersonalSourceService(store, null!, official),
            _ => new TraktPersonalSourceService(store, auth, null!),
        });
    }

    [Fact]
    public async Task ResolveAsync_OwnLinked_ReturnsOwnSourceWithoutConsultingOfficial()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns(new TraktUserToken { AccessToken = "a", RefreshToken = "b" });
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync("own-tok");
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        var sut = CreateService();

        var source = await sut.ResolveAsync(userId, ConfigWithOwnCreds(), CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal("client-id", source!.ClientId);
        Assert.Equal("own-tok", source.AccessToken);
        Assert.True(source.IsOwnFlow);
        _officialPlugin.Verify(p => p.TryGetToken(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_OwnLinkedButTokenUnavailable_ReturnsNullWithoutOfficialFallback()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns(new TraktUserToken { AccessToken = "a", RefreshToken = "b" });
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()))
            .Returns(new OfficialTraktToken("official-tok", FarFuture));
        var sut = CreateService();

        Assert.Null(await sut.ResolveAsync(userId, ConfigWithOwnCreds(), CancellationToken.None));

        _officialPlugin.Verify(p => p.TryGetToken(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_NoOwnLink_OfficialPresent_ReturnsOfficialSource()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns((TraktUserToken?)null);
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new OfficialTraktToken("official-tok", FarFuture));
        var sut = CreateService();

        var source = await sut.ResolveAsync(userId, ConfigWithOwnCreds(), CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal(OfficialTraktPluginReader.OfficialTraktClientId, source!.ClientId);
        Assert.Equal("official-tok", source.AccessToken);
        Assert.False(source.IsOwnFlow);
        _auth.Verify(a => a.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_OwnLinkedButClientIdBlank_FallsThroughToOfficial()
    {
        // No own client id configured means the own link cannot be attributed to an app, so the user is
        // treated as having no usable own link and the official fallback applies.
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns(new TraktUserToken { AccessToken = "a", RefreshToken = "b" });
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new OfficialTraktToken("official-tok", FarFuture));
        var sut = CreateService();

        var source = await sut.ResolveAsync(userId, new PluginConfiguration { TraktClientId = string.Empty }, CancellationToken.None);

        Assert.NotNull(source);
        Assert.False(source!.IsOwnFlow);
        _auth.Verify(a => a.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_NeitherSource_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns((TraktUserToken?)null);
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        Assert.Null(await sut.ResolveAsync(userId, ConfigWithOwnCreds(), CancellationToken.None));
    }

    [Fact]
    public void IsLinked_OwnLinked_ReturnsTrueWithoutRefresh()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns(new TraktUserToken { AccessToken = "a", RefreshToken = "b" });
        var sut = CreateService();

        Assert.True(sut.IsLinked(userId, ConfigWithOwnCreds()));

        _auth.Verify(a => a.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void IsLinked_OnlyOfficialHasToken_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns((TraktUserToken?)null);
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new OfficialTraktToken("official-tok", FarFuture));
        var sut = CreateService();

        Assert.True(sut.IsLinked(userId, ConfigWithOwnCreds()));
    }

    [Fact]
    public void IsLinked_NeitherSource_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetToken(userId)).Returns((TraktUserToken?)null);
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        Assert.False(sut.IsLinked(userId, ConfigWithOwnCreds()));
    }

    [Fact]
    public void IsAvailable_OwnCredsOrOfficialPresent_ReturnsTrue()
    {
        var sut = CreateService();

        Assert.True(sut.IsAvailable(new PluginConfiguration { TraktEnabled = true, TraktClientId = "client-id" }));

        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        Assert.True(sut.IsAvailable(new PluginConfiguration { TraktEnabled = false }));
    }

    [Fact]
    public void IsAvailable_NeitherSource_ReturnsFalse()
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        Assert.False(sut.IsAvailable(new PluginConfiguration { TraktEnabled = false }));
    }

    [Fact]
    public void GetLinkedUserIds_MergesOwnAndOfficial_Deduped()
    {
        var ownOnly = Guid.NewGuid();
        var both = Guid.NewGuid();
        var officialOnly = Guid.NewGuid();
        _store.Setup(s => s.GetLinkedUserIds()).Returns(new List<Guid> { ownOnly, both });
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>())).Returns(new[] { both, officialOnly });
        var sut = CreateService();

        var ids = sut.GetLinkedUserIds();

        Assert.Equal(3, ids.Count);
        Assert.Contains(ownOnly, ids);
        Assert.Contains(both, ids);
        Assert.Contains(officialOnly, ids);
    }

    [Fact]
    public void GetLinkedUserIds_OfficialAbsent_ReturnsOnlyOwn()
    {
        var own = Guid.NewGuid();
        _store.Setup(s => s.GetLinkedUserIds()).Returns(new List<Guid> { own });
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var sut = CreateService();

        var ids = sut.GetLinkedUserIds();

        Assert.Single(ids, own);
        _officialPlugin.Verify(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task RefreshOwnTokenAsync_DelegatesToAuthService()
    {
        _auth.Setup(a => a.RefreshAccessTokenAsync(It.IsAny<Guid>(), "rejected", It.IsAny<CancellationToken>())).ReturnsAsync("renewed");
        var sut = CreateService();
        var userId = Guid.NewGuid();

        Assert.Equal("renewed", await sut.RefreshOwnTokenAsync(userId, "rejected", CancellationToken.None));
    }

    [Fact]
    public async Task UnlinkOwnAsync_DelegatesToStore()
    {
        var sut = CreateService();
        var userId = Guid.NewGuid();

        await sut.UnlinkOwnAsync(userId, CancellationToken.None);

        _store.Verify(s => s.RemoveAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
