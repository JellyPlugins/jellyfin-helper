using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class MediaStatisticsResultLanguageTests
{
    [Fact]
    public void TotalAudioLanguages_SingleLibrary_AggregatesCorrectly()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics { LibraryName = "Movies", CollectionType = "movies" };
        lib.AudioLanguages["English"] = 5;
        lib.AudioLanguages["German"] = 3;
        lib.AudioLanguageSizes["English"] = 1_000;
        result.Libraries.Add(lib);
        result.Movies.Add(lib);

        Assert.Equal(5, result.TotalAudioLanguages["English"]);
        Assert.Equal(3, result.TotalAudioLanguages["German"]);
        Assert.Equal(1_000, result.TotalAudioLanguageSizes["English"]);
    }

    [Fact]
    public void TotalAudioLanguages_MultipleLibraries_SumsCorrectly()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics { LibraryName = "M1", CollectionType = "movies" };
        lib1.AudioLanguages["English"] = 2;
        var lib2 = new LibraryStatistics { LibraryName = "M2", CollectionType = "movies" };
        lib2.AudioLanguages["English"] = 3;
        lib2.AudioLanguages["French"] = 1;
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);
        result.Movies.Add(lib1);
        result.Movies.Add(lib2);

        Assert.Equal(5, result.TotalAudioLanguages["English"]);
        Assert.Equal(1, result.TotalAudioLanguages["French"]);
    }

    [Fact]
    public void TotalSubtitleLanguages_SingleLibrary_AggregatesCorrectly()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics { LibraryName = "Movies", CollectionType = "movies" };
        lib.SubtitleLanguages["English"] = 4;
        lib.SubtitleLanguageSizes["English"] = 2_000;
        result.Libraries.Add(lib);
        result.Movies.Add(lib);

        Assert.Equal(4, result.TotalSubtitleLanguages["English"]);
        Assert.Equal(2_000, result.TotalSubtitleLanguageSizes["English"]);
    }

    [Fact]
    public void TotalWatchedTiers_AcrossAllLibraries_SumsCorrectly()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics { LibraryName = "Movies", CollectionType = "movies" };
        lib1.WatchedTiers["Never watched"] = 2;
        lib1.WatchedTiers["Watched"] = 1;
        var lib2 = new LibraryStatistics { LibraryName = "TV", CollectionType = "tvshows" };
        lib2.WatchedTiers["Never watched"] = 3;
        lib2.WatchedTiers["Watched"] = 4;
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);
        result.Movies.Add(lib1);
        result.TvShows.Add(lib2);

        Assert.Equal(5, result.TotalWatchedTiers["Never watched"]);
        Assert.Equal(5, result.TotalWatchedTiers["Watched"]);
    }

    [Fact]
    public void TotalWatchedTierSizes_AcrossAllLibraries_SumsCorrectly()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics { LibraryName = "Movies", CollectionType = "movies" };
        lib.WatchedTierSizes["Never watched"] = 5_000;
        result.Libraries.Add(lib);
        result.Movies.Add(lib);

        Assert.Equal(5_000, result.TotalWatchedTierSizes["Never watched"]);
    }

    [Fact]
    public void TotalWatchedByUser_AggregatesCorrectly()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics { LibraryName = "M1", CollectionType = "movies" };
        lib1.WatchedByUserPaths["Alice"] = new System.Collections.ObjectModel.Collection<string> { "/a.mkv", "/b.mkv" };
        lib1.WatchedByUserSizes["Alice"] = 2_000;
        var lib2 = new LibraryStatistics { LibraryName = "M2", CollectionType = "movies" };
        lib2.WatchedByUserPaths["Alice"] = new System.Collections.ObjectModel.Collection<string> { "/c.mkv" };
        lib2.WatchedByUserPaths["Bob"] = new System.Collections.ObjectModel.Collection<string> { "/d.mkv" };
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);
        result.Movies.Add(lib1);
        result.Movies.Add(lib2);

        Assert.Equal(3, result.TotalWatchedByUser["Alice"]);
        Assert.Equal(1, result.TotalWatchedByUser["Bob"]);
        Assert.Equal(2_000, result.TotalWatchedByUserSizes["Alice"]);
    }
}
