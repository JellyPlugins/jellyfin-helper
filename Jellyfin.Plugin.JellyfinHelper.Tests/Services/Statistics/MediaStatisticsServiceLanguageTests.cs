using System.IO;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class MediaStatisticsServiceLanguageTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly TestableMediaStatisticsService _service;

    public MediaStatisticsServiceLanguageTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        var loggerMock = TestMockFactory.CreateLogger<MediaStatisticsService>();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        _service = new TestableMediaStatisticsService(_libraryManagerMock.Object, _fileSystemMock.Object, TestMockFactory.CreatePluginLogService(), loggerMock.Object, configHelperMock.Object);
    }

    private static string TestPath(params string[] segments) => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    private void SetupLibraryWithVideo(string filePath, List<MediaStream> streams, long fileSize = 1_000_000_000)
    {
        var libraryPath = TestPath("media", "movies");
        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = Path.GetFileName(filePath),
            Length = fileSize,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = filePath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns(streams);
        _service.SetItemLookup(filePath, mockItem.Object);
    }

    [Fact]
    public void AudioLanguage_SingleTrack_RecordsLanguage()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["English"]);
        Assert.Equal(1_000_000_000, stats.AudioLanguageSizes["English"]);
        Assert.Contains(path, stats.AudioLanguagePaths["English"]);
    }

    [Fact]
    public void AudioLanguage_TwoTracksSameLanguage_CountsFileOnce()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng" },
            new() { Type = MediaStreamType.Audio, Codec = "ac3", Language = "eng" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["English"]);
        Assert.Single(stats.AudioLanguagePaths["English"]);
    }

    [Fact]
    public void AudioLanguage_TwoDistinctLanguages_RecordsBoth()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng" },
            new() { Type = MediaStreamType.Audio, Codec = "ac3", Language = "ger" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["English"]);
        Assert.Equal(1, stats.AudioLanguages["German"]);
    }

    [Fact]
    public void AudioLanguage_NullLanguageStream_RecordsUnknown()
    {
        // A present but unreadable audio track keeps the facet totals reconciled
        // instead of silently dropping the file.
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = null }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["Unknown"]);
        Assert.Equal(1_000_000_000, stats.AudioLanguageSizes["Unknown"]);
        Assert.Contains(path, stats.AudioLanguagePaths["Unknown"]);
        Assert.Empty(stats.AudioTrackLabels);
    }

    [Fact]
    public void AudioLanguage_UndLanguage_RecordsUnknown()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "und" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["Unknown"]);
    }

    [Fact]
    public void AudioLanguage_UnknownIso639Code_FoldsIntoUnknown()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "xyz" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["Unknown"]);
    }

    [Fact]
    public void SubtitleLanguage_EmbeddedTrack_IsRecorded()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "srt", Language = "eng", IsExternal = false }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.SubtitleLanguages["English"]);
    }

    [Fact]
    public void SubtitleLanguage_ExternalTrack_IsSkipped()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "srt", Language = "eng", IsExternal = true }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Empty(stats.SubtitleLanguages);
    }

    [Fact]
    public void NormalizeIso639Language_KnownCodes_ReturnDisplayName()
    {
        Assert.Equal("German", MediaStatisticsService.NormalizeIso639Language("deu"));
        Assert.Equal("German", MediaStatisticsService.NormalizeIso639Language("ger"));
        Assert.Equal("German", MediaStatisticsService.NormalizeIso639Language("de"));
        Assert.Equal("English", MediaStatisticsService.NormalizeIso639Language("eng"));
    }

    [Fact]
    public void NormalizeIso639Language_UnknownCode_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language("xyz"));

    [Fact]
    public void NormalizeIso639Language_NullInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language(null));

    [Fact]
    public void NormalizeIso639Language_EmptyInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language(""));

    [Fact]
    public void NormalizeIso639Language_WhitespaceInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language("   "));

    [Fact]
    public void NormalizeIso639Language_UndInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language("und"));

    [Theory]
    [InlineData("mis")]
    [InlineData("mul")]
    [InlineData("zxx")]
    [InlineData("MIS")]
    public void NormalizeIso639Language_UncodedCodes_ReturnNull(string code)
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("en-US", "English")]
    [InlineData("pt-BR", "Portuguese")]
    [InlineData("de-DE", "German")]
    [InlineData("en_us", "English")]
    public void NormalizeIso639Language_RegionSubtag_StrippedToBase(string code, string expected)
        => Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("german", "German")]
    [InlineData("GERMAN", "German")]
    [InlineData("deutsch", "German")]
    [InlineData("Deutsch", "German")]
    [InlineData("english", "English")]
    [InlineData("französisch", "French")]
    [InlineData("svenska", "Swedish")]
    [InlineData("français", "French")]
    [InlineData("francais", "French")]
    [InlineData("español", "Spanish")]
    [InlineData("espanol", "Spanish")]
    [InlineData("italiano", "Italian")]
    [InlineData("日本語", "Japanese")]
    [InlineData("한국어", "Korean")]
    [InlineData("русский", "Russian")]
    [InlineData("中文", "Chinese")]
    [InlineData("português", "Portuguese")]
    [InlineData("portugues", "Portuguese")]
    [InlineData("nederlands", "Dutch")]
    [InlineData("polski", "Polish")]
    [InlineData("türkçe", "Turkish")]
    [InlineData("turkce", "Turkish")]
    [InlineData("العربية", "Arabic")]
    [InlineData("हिन्दी", "Hindi")]
    [InlineData("norsk", "Norwegian")]
    [InlineData("nb", "Norwegian")]
    [InlineData("nn", "Norwegian")]
    [InlineData("iw", "Hebrew")]
    [InlineData("dansk", "Danish")]
    [InlineData("suomi", "Finnish")]
    [InlineData("ελληνικά", "Greek")]
    [InlineData("čeština", "Czech")]
    [InlineData("cestina", "Czech")]
    [InlineData("magyar", "Hungarian")]
    [InlineData("ไทย", "Thai")]
    [InlineData("vietnam", "Vietnamese")]
    [InlineData("українська", "Ukrainian")]
    [InlineData("עברית", "Hebrew")]
    [InlineData("română", "Romanian")]
    [InlineData("romana", "Romanian")]
    [InlineData("fr-FR", "French")]
    [InlineData("sv-SE", "Swedish")]
    [InlineData("zh-Hant", "Chinese")]
    public void NormalizeIso639Language_SpellingVariant_ResolvesLikeCode(string code, string expected)
        => Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("sr", "Serbian")]
    [InlineData("srp", "Serbian")]
    [InlineData("scc", "Serbian")]
    [InlineData("hr", "Croatian")]
    [InlineData("scr", "Serbo-Croatian")]
    [InlineData("SCR", "Serbo-Croatian")]
    [InlineData("hbs", "Serbo-Croatian")]
    [InlineData("fa", "Persian")]
    [InlineData("id", "Indonesian")]
    [InlineData("ca", "Catalan")]
    [InlineData("español (Colombia)", "Spanish")]
    public void NormalizeIso639Language_UnlistedLanguage_ResolvesThroughCultures(string code, string expected)
        => Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("in")]
    [InlineData("ji")]
    [InlineData("mo")]
    [InlineData("xyz")]
    [InlineData("new")]
    [InlineData("NEW")]
    [InlineData("''")]
    [InlineData("\"\"")]
    public void NormalizeIso639Language_UnresolvableCodes_FoldIntoUnknown(string code)
    {
        // Retired or unknown codes that collide with ordinary words fold into
        // Unknown instead of inventing junk facets ("in English" is not
        // Indonesian, "NEW" is not a language, "''" is muxer punctuation).
        Assert.Null(MediaStatisticsService.NormalizeIso639Language(code));
    }

    [Theory]
    [InlineData("zxx - commentary", null)]
    [InlineData("en, und", "English")]
    public void NormalizeIso639Language_UndeterminedCompounds_ResolveRealParts(string code, string? expected)
    {
        // Undetermined markers contribute nothing; the real parts still resolve,
        // and a compound without any language stays silent instead of leaking.
        if (expected == null)
        {
            Assert.Null(MediaStatisticsService.NormalizeIso639Language(code));
        }
        else
        {
            Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(code));
        }
    }

    public static TheoryData<string, string> LanguageExonymCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in LanguageIdentity.LanguageExonyms)
        {
            if (row == null)
            {
                continue;
            }

            var display = row[0];
            if (string.IsNullOrEmpty(display))
            {
                continue;
            }

            foreach (var exonym in row.Skip(1))
            {
                if (!string.IsNullOrEmpty(exonym))
                {
                    data.Add(exonym, display);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LanguageExonymCases))]
    public void NormalizeIso639Language_Exonym_ResolvesToDisplay(string exonym, string expected)
        => Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(exonym));

    [Theory]
    [InlineData("German (Forced)", "German")]
    [InlineData("deutsch [forced]", "German")]
    [InlineData("eng (commentary)", "English")]
    public void NormalizeIso639Language_TrackQualifier_StrippedToBase(string code, string expected)
        => Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("GERMAN Forced SRT by TSCC", "German")]
    [InlineData("eng commentary", "English")]
    [InlineData("swe commentary", "Swedish")]
    [InlineData("allemand", "German")]
    [InlineData("tyska", "German")]
    [InlineData("sueco", "Swedish")]
    public void NormalizeIso639Language_MessyTag_ResolvesFirstToken(string code, string expected)
        => Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("Full SDH (PGS)")]
    [InlineData("SDH Hörgeschädigt")]
    [InlineData("Forced (SRT)")]
    [InlineData("Forced")]
    [InlineData("commentary")]
    [InlineData("malentendant")]
    [InlineData("Erzwungen")]
    public void NormalizeIso639Language_FlagOnlyTag_YieldsNull(string code)
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("German (Forced)", "German")]
    [InlineData("Forced (SRT)", null)]
    [InlineData(null, null)]
    public void LanguageFromTitle_EmptyLanguage_ResolvesHitOnly(string? title, string? expected)
        => Assert.Equal(expected, MediaStatisticsService.LanguageFromTitle(title));

    [Fact]
    public void FormatAudioTrackLabel_PlainTrack_ReturnsLanguage()
        => Assert.Equal("German", MediaStatisticsService.FormatAudioTrackLabel("German", new MediaStream { Type = MediaStreamType.Audio }));

    [Fact]
    public void FormatAudioTrackLabel_ForcedTrack_AppendsFlag()
        => Assert.Equal("German (Forced)", MediaStatisticsService.FormatAudioTrackLabel("German", new MediaStream { Type = MediaStreamType.Audio, IsForced = true }));

    [Theory]
    [InlineData(null, false, false, "German")]
    [InlineData("vorbis", false, false, "German")]
    [InlineData("subrip", false, false, "German (SRT)")]
    [InlineData("pgssub", true, false, "German (PGS, Forced)")]
    [InlineData("ass", false, true, "German (ASS, SDH)")]
    [InlineData("srt", true, true, "German (SRT, Forced, SDH)")]
    public void FormatSubtitleTrackLabel_FormatsAndFlags_RenderedAsTags(string? codec, bool forced, bool sdh, string expected)
    {
        var track = new MediaStream { Type = MediaStreamType.Subtitle, Codec = codec, IsForced = forced, IsHearingImpaired = sdh };
        Assert.Equal(expected, MediaStatisticsService.FormatSubtitleTrackLabel("German", track));
    }

    [Fact]
    public void AudioLanguage_SpellingVariants_CollapseToOneFacetWithTrackLabels()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "ger" },
            new() { Type = MediaStreamType.Audio, Codec = "ac3", Language = "ger", IsForced = true },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "deutsch" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["German"]);
        Assert.Equal(["German", "German (Forced)"], stats.AudioTrackLabels[path]);
    }

    [Fact]
    public void AudioLanguage_NativeNameAndCode_CollapseToOneFacet()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "swe" },
            new() { Type = MediaStreamType.Audio, Codec = "ac3", Language = "svenska" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["Swedish"]);
        Assert.Equal(["Swedish"], stats.AudioTrackLabels[path]);
    }

    [Fact]
    public void SubtitleLanguage_VariantTracks_RecordLabelsWithFormatAndFlags()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "eng", IsExternal = false },
            new() { Type = MediaStreamType.Subtitle, Codec = "pgssub", Language = "ger", IsExternal = false, IsForced = true }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.SubtitleLanguages["English"]);
        Assert.Equal(1, stats.SubtitleLanguages["German"]);
        Assert.Equal(["English (SRT)", "German (PGS, Forced)"], stats.SubtitleTrackLabels[path]);
    }

    [Fact]
    public void SubtitleLanguage_EmptyLanguageWithTitle_FallsBackToTitleHit()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "subrip", Language = null, Title = "German (Forced)", IsExternal = false }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.SubtitleLanguages["German"]);
        Assert.Equal(["German (SRT)"], stats.SubtitleTrackLabels[path]);
    }

    [Fact]
    public void SubtitleLanguage_FlagOnlyTitleWithoutLanguage_RecordsUnknown()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "pgssub", Language = null, Title = "Full SDH (PGS)", IsExternal = false }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.SubtitleLanguages["Unknown"]);
        Assert.Empty(stats.SubtitleTrackLabels);
    }

    [Fact]
    public void SubtitleLanguage_SixGermanVariants_CollapseToOneFacetWithLabels()
    {
        // Six German subtitle tracks in the wild: codes, names, flags, formats and
        // one pure flag tag. The facet counts the file once, the labels keep every
        // variant, and the flag noise vanishes instead of opening stray facets.
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "ger", IsExternal = false },
            new() { Type = MediaStreamType.Subtitle, Codec = "pgssub", Language = "ger", IsExternal = false, IsForced = true },
            new() { Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "German", IsExternal = false },
            new() { Type = MediaStreamType.Subtitle, Codec = "ass", Language = "deutsch", IsExternal = false },
            new() { Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "ger", IsExternal = false, IsHearingImpaired = true },
            new() { Type = MediaStreamType.Subtitle, Codec = "pgssub", Language = "Full SDH", IsExternal = false }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.SubtitleLanguages["German"]);
        Assert.Equal(
            ["German (SRT)", "German (PGS, Forced)", "German (ASS)", "German (SRT, SDH)"],
            stats.SubtitleTrackLabels[path]);
    }

    [Theory]
    [InlineData("''")]
    [InlineData("\"\"")]
    [InlineData("new")]
    [InlineData("NEW")]
    [InlineData("xyz")]
    public void AudioLanguage_JunkTags_FoldIntoUnknown(string junk)
    {
        // Muxer punctuation ("''") and unresolvable codes ("NEW", "xyz") must not
        // invent facets; the file lands in Unknown instead.
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = junk }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["Unknown"]);
        Assert.DoesNotContain(junk, stats.AudioLanguages.Keys);
        Assert.Empty(stats.AudioTrackLabels);
    }

    [Theory]
    [InlineData("''")]
    [InlineData("new")]
    [InlineData("NEW")]
    public void SubtitleLanguage_JunkTags_FoldIntoUnknown(string junk)
    {
        // Same contract for embedded subtitles: junk never opens a facet.
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "subrip", Language = junk, IsExternal = false }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.SubtitleLanguages["Unknown"]);
        Assert.DoesNotContain(junk, stats.SubtitleLanguages.Keys);
        Assert.Empty(stats.SubtitleTrackLabels);
    }

    private sealed class TestableMediaStatisticsService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService pluginLog,
        ILogger<MediaStatisticsService> logger,
        ICleanupConfigHelper configHelper)
        : MediaStatisticsService(libraryManager, fileSystem, pluginLog, logger, configHelper)
    {
        private readonly Dictionary<string, BaseItem> _itemLookup = new(StringComparer.OrdinalIgnoreCase);
        public void SetItemLookup(string filePath, BaseItem item) => _itemLookup[filePath] = item;
        internal override Dictionary<string, BaseItem> BuildItemLookup() => new(_itemLookup, StringComparer.OrdinalIgnoreCase);
    }
}
