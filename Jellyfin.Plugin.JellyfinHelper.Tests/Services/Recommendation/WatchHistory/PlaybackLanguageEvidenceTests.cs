using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

/// <summary>Availability is not a fabricated multilingual playback choice; saved selections remain detached and dated.</summary>
public sealed class PlaybackLanguageEvidenceTests
{
    private static readonly DateTime Playback = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(int.MinValue, null)]
    [InlineData(int.MaxValue, null)]
    [InlineData(9, null)]
    [InlineData(0, "en")]
    [InlineData(2, "de")]
    public void MultilingualAudio_RequiresAValidSavedIndex(int? index, string? expected)
    {
        var row = Row();
        PlaybackLanguageEvidence.Capture(row, new UserItemData { Key = "synthetic", AudioStreamIndex = index, LastPlayedDate = Playback },
            [Stream(0, MediaStreamType.Audio, "eng"), Stream(1, MediaStreamType.Audio, "eng"), Stream(2, MediaStreamType.Audio, "ger"), Stream(3, MediaStreamType.Audio, "deu")]);
        Assert.Equal(new[] { "de", "en" }, row.AudioLanguages);
        Assert.Equal(expected, row.PlaybackAudioLanguage);
        Assert.Equal(Playback, row.LanguagePlaybackDate);
        var profile = new UserWatchProfile();
        PlaybackLanguageEvidence.Accumulate(profile, row);
        if (expected is null) Assert.Empty(profile.LanguageProfile);
        else Assert.Equal(1, profile.LanguageProfile[expected].ChosenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APartlyUnlabelledFile_CannotProveTheSingleAudioFallback(bool unknownTrack)
    {
        var row = Row();
        List<MediaStream> streams = [Stream(1, MediaStreamType.Audio, "eng")];
        if (unknownTrack) streams.Add(Stream(2, MediaStreamType.Audio, null));
        PlaybackLanguageEvidence.Capture(row, new UserItemData { Key = "synthetic", LastPlayedDate = Playback }, streams);
        Assert.Equal(unknownTrack ? null : "en", row.PlaybackAudioLanguage);
        Assert.Equal(!unknownTrack, row.HasAudioLanguageMetadata);
        var profile = new UserWatchProfile();
        PlaybackLanguageEvidence.Accumulate(profile, row);
        if (unknownTrack) Assert.Empty(profile.LanguageProfile);
        else Assert.Equal(1, profile.LanguageProfile["en"].ForcedCount);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(int.MinValue, null)]
    [InlineData(int.MaxValue, null)]
    [InlineData(99, null)]
    [InlineData(0, "en")]
    [InlineData(4, "de")]
    public void SubtitlesOffAbsentAndInvalid_AreNotAChoice(int? index, string? expected)
    {
        var row = Row();
        PlaybackLanguageEvidence.Capture(row, new UserItemData { Key = "synthetic", LastPlayedDate = Playback, SubtitleStreamIndex = index },
            [Stream(0, MediaStreamType.Subtitle, "eng"), Stream(3, MediaStreamType.Subtitle, "eng"), Stream(4, MediaStreamType.Subtitle, "deu")]);
        Assert.Equal(expected, row.PlaybackSubtitleLanguage);
        Assert.Equal(new[] { "de", "en" }, row.SubtitleLanguages);
    }

    [Fact]
    public void UnknownKnownEmptyAndCapturedLists_SurviveJsonAndDetachFromCopies()
    {
        var unknown = JsonSerializer.Deserialize<WatchedItemInfo>("{}")!;
        Assert.Null(unknown.AudioLanguages);
        Assert.Null(unknown.SubtitleLanguages);
        var empty = Row();
        PlaybackLanguageEvidence.Capture(empty, new UserItemData { Key = "synthetic" }, []);
        var restored = JsonSerializer.Deserialize<WatchedItemInfo>(JsonSerializer.Serialize(empty))!;
        Assert.Empty(restored.AudioLanguages!);
        Assert.Empty(restored.SubtitleLanguages!);
        string[] languages = ["de", "en"];
        empty.AudioLanguages = languages;
        empty.PlaybackAudioLanguage = "de";
        empty.LanguagePlaybackDate = Playback;
        var copy = empty.CopyPlaybackHistory();
        languages[0] = "ja";
        Assert.Equal("de", copy.AudioLanguages![0]);
        Assert.Equal("de", copy.PlaybackAudioLanguage);
        Assert.Equal("de", JsonSerializer.Deserialize<WatchedItemInfo>(JsonSerializer.Serialize(copy))!.PlaybackAudioLanguage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(1)]
    public void HistoricalCopy_DropsUndatedOrMismatchedSelectionProvenance(int? dayOffset)
    {
        var row = Row();
        row.AudioLanguages = ["de"];
        row.PlaybackAudioLanguage = "de";
        row.LanguagePlaybackDate = dayOffset.HasValue ? Playback.AddDays(dayOffset.Value) : null;
        var copy = row.CopyPlaybackHistory();
        Assert.Null(copy.PlaybackAudioLanguage);
        Assert.Null(copy.LanguagePlaybackDate);
        Assert.Equal(new[] { "de" }, copy.AudioLanguages);
        Assert.Equal("de", row.PlaybackAudioLanguage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void UndefinedLanguage_IsNotGenuinePlaybackPreference(int? savedIndex)
    {
        var row = Row();
        PlaybackLanguageEvidence.Capture(row, new UserItemData { Key = "synthetic", AudioStreamIndex = savedIndex, LastPlayedDate = Playback },
            [Stream(1, MediaStreamType.Audio, "und")]);
        Assert.Null(row.PlaybackAudioLanguage);
        Assert.False(row.HasAudioLanguageMetadata);
        var profile = new UserWatchProfile();
        PlaybackLanguageEvidence.Accumulate(profile, row);
        Assert.Empty(profile.LanguageProfile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedKnownLanguageWithUnknownAlternativesIsNeitherProvenForcedNorChosen(bool subtitle)
    {
        var row = Row();
        var data = new UserItemData { Key = "synthetic", LastPlayedDate = Playback, AudioStreamIndex = 1, SubtitleStreamIndex = 1 };
        var type = subtitle ? MediaStreamType.Subtitle : MediaStreamType.Audio;
        PlaybackLanguageEvidence.Capture(row, data, [Stream(1, type, "eng"), Stream(2, type, null)]);
        Assert.Equal("en", subtitle ? row.PlaybackSubtitleLanguage : row.PlaybackAudioLanguage);
        Assert.False(subtitle ? row.HasSubtitleLanguageMetadata : row.HasAudioLanguageMetadata);
        var profile = new UserWatchProfile();
        PlaybackLanguageEvidence.Accumulate(profile, row);
        Assert.Empty(subtitle ? profile.SubtitleLanguageProfile : profile.LanguageProfile);
    }

    [Fact]
    public void TwoKnownAlternativesProveAChoiceEvenWithAnotherUnlabelledTrack()
    {
        var row = Row();
        PlaybackLanguageEvidence.Capture(row, new UserItemData { Key = "synthetic", LastPlayedDate = Playback, AudioStreamIndex = 1 },
            [Stream(1, MediaStreamType.Audio, "eng"), Stream(2, MediaStreamType.Audio, "ger"), Stream(3, MediaStreamType.Audio, null)]);
        var profile = new UserWatchProfile();
        PlaybackLanguageEvidence.Accumulate(profile, row);
        var entry = Assert.Single(profile.LanguageProfile).Value;
        Assert.Equal(1, entry.ChosenCount);
        Assert.Equal(0, entry.ForcedCount);
    }

    private static WatchedItemInfo Row() => new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = Playback };
    private static MediaStream Stream(int index, MediaStreamType type, string? language) => new() { Index = index, Type = type, Language = language };
}
