using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

/// <summary>
///     Exercises WatchHistoryService's audio/subtitle language derivation: the chosen-vs-forced distinction, the "subtitles off" sentinel guard, and the GetMediaStreams cancellation/error contract.
/// </summary>
public sealed class WatchHistoryServiceLanguageProfileTests
{
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IUserManager> _mockUserManager;
    private readonly Mock<IUserDataManager> _mockUserDataManager;
    private readonly Mock<IPluginLogService> _mockPluginLog;
    private readonly Mock<ILogger<WatchHistoryService>> _mockLogger;
    private readonly WatchHistoryService _service;

    public WatchHistoryServiceLanguageProfileTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockUserManager = new Mock<IUserManager>();
        _mockUserDataManager = new Mock<IUserDataManager>();
        _mockPluginLog = new Mock<IPluginLogService>();
        _mockLogger = new Mock<ILogger<WatchHistoryService>>();
        _service = new WatchHistoryService(
            _mockLibraryManager.Object,
            _mockUserManager.Object,
            _mockUserDataManager.Object,
            _mockPluginLog.Object,
            _mockLogger.Object);
    }

    // Video-item list feeds the main loop AND the language pass; the second GetItemList
    // call (series) returns empty so no synthetic favorites interfere.
    private void SetupSingleVideoItem(BaseItem item)
    {
        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { item })
            .Returns(new List<BaseItem>());
    }

    [Fact]
    public void BuildProfile_ItemWithNoMediaStreams_ContributesNoLanguage()
    {
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>());
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Empty(profile!.LanguageProfile);
        Assert.Empty(profile.SubtitleLanguageProfile);
    }

    [Fact]
    public void BuildProfile_ItemWithNullMediaStreams_ContributesNoLanguage()
    {
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns((List<MediaStream>?)null!);
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Empty(profile!.LanguageProfile);
        Assert.Empty(profile.SubtitleLanguageProfile);
    }

    [Fact]
    public void BuildProfile_ItemWithNoInteraction_SkippedFromLanguageProfile()
    {
        // The item exists in the library and has streams, but no play/progress means the
        // language pass must skip it (same guard as the main loop).
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Audio, Language = "eng" }
        });
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = false, PlaybackPositionTicks = 0, IsFavorite = false });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Empty(profile!.LanguageProfile);
        Assert.Empty(profile.SubtitleLanguageProfile);
    }

    [Fact]
    public void BuildProfile_AudioStreamChosenAmongMultipleLanguages_RecordsChosenCount()
    {
        // AudioStreamIndex points at the German track while an English alternative exists,
        // so the language was actively chosen. 'ger' must normalize to 'de'.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = "ger" }
        });
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1, AudioStreamIndex = 2 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.True(profile!.LanguageProfile.ContainsKey("de"));
        Assert.Equal(1, profile.LanguageProfile["de"].ChosenCount);
        Assert.Equal(0, profile.LanguageProfile["de"].ForcedCount);
    }

    [Fact]
    public void BuildProfile_SingleAudioLanguageNoChosenIndex_RecordsForcedCount()
    {
        // No AudioStreamIndex and only one available language: the language was forced,
        // not chosen. usedAudioLanguage falls back to audioStreams[0].Language.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" }
        });
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.True(profile!.LanguageProfile.ContainsKey("en"));
        Assert.Equal(1, profile.LanguageProfile["en"].ForcedCount);
        Assert.Equal(0, profile.LanguageProfile["en"].ChosenCount);
    }

    [Fact]
    public void BuildProfile_SubtitleStreamChosenAmongMultipleLanguages_RecordsChosenCount()
    {
        // SubtitleStreamIndex selects the German subtitle among several languages,
        // so it counts as chosen.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new() { Index = 2, Type = MediaStreamType.Subtitle, Language = "eng" },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Language = "ger" }
        });
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1, SubtitleStreamIndex = 3 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.True(profile!.SubtitleLanguageProfile.ContainsKey("de"));
        Assert.Equal(1, profile.SubtitleLanguageProfile["de"].ChosenCount);
        Assert.Equal(0, profile.SubtitleLanguageProfile["de"].ForcedCount);
    }

    [Fact]
    public void BuildProfile_SingleSubtitleLanguage_RecordsForcedCount()
    {
        // Only one subtitle language available: selecting it is forced, not chosen.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new() { Index = 2, Type = MediaStreamType.Subtitle, Language = "eng" }
        });
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1, SubtitleStreamIndex = 2 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.True(profile!.SubtitleLanguageProfile.ContainsKey("en"));
        Assert.Equal(1, profile.SubtitleLanguageProfile["en"].ForcedCount);
        Assert.Equal(0, profile.SubtitleLanguageProfile["en"].ChosenCount);
    }

    [Fact]
    public void BuildProfile_NegativeSubtitleStreamIndex_RecordsNoSubtitleLanguage()
    {
        // -1 is Jellyfin's "subtitles off" sentinel; the >= 0 guard must skip subtitle
        // accounting even though subtitle streams exist.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new() { Index = 2, Type = MediaStreamType.Subtitle, Language = "eng" }
        });
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1, SubtitleStreamIndex = -1 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Empty(profile!.SubtitleLanguageProfile);
    }

    [Fact]
    public void BuildProfile_GetMediaStreamsCancelled_PropagatesOperationCanceled()
    {
        // Cancellation is a stop signal - it must propagate out, not be swallowed like
        // corrupted-metadata errors.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Id = Guid.NewGuid();
        mockItem.Setup(i => i.GetMediaStreams()).Throws(new OperationCanceledException());
        SetupSingleVideoItem(mockItem.Object);
        _mockUserDataManager.Setup(m => m.GetUserData(user, mockItem.Object))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        Assert.Throws<OperationCanceledException>(() => _service.GetUserWatchProfile(user.Id));
    }

    [Fact]
    public void BuildProfile_GetMediaStreamsThrows_SkipsItemGracefully()
    {
        // A non-fatal stream-lookup failure on one item must not abort the language pass;
        // the healthy item's language is still recorded.
        var user = CreateTestUser("alice");
        _mockUserManager.Setup(m => m.GetUserById(user.Id)).Returns(user);

        var badItem = new Mock<BaseItem>();
        badItem.Object.Id = Guid.NewGuid();
        badItem.Setup(i => i.GetMediaStreams()).Throws(new IOException("corrupted metadata"));

        var goodItem = new Mock<BaseItem>();
        goodItem.Object.Id = Guid.NewGuid();
        goodItem.Setup(i => i.GetMediaStreams()).Returns(new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" }
        });

        _mockLibraryManager
            .SetupSequence(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { badItem.Object, goodItem.Object })
            .Returns(new List<BaseItem>());
        _mockUserDataManager.Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "k", Played = true, PlayCount = 1 });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.True(profile!.LanguageProfile.ContainsKey("en"));
    }

    private static Jellyfin.Database.Implementations.Entities.User CreateTestUser(string username)
    {
        return new Jellyfin.Database.Implementations.Entities.User(username, "default", "default") { Id = Guid.NewGuid() };
    }
}
