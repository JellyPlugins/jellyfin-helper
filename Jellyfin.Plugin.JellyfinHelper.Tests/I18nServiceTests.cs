using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

public class I18nServiceTests
{
    // ===== SupportedLanguages Tests =====

    [Fact]
    public void SupportedLanguages_ContainsExpectedLanguages()
    {
        var languages = I18nService.SupportedLanguages;
        Assert.Contains("en", languages);
        Assert.Contains("de", languages);
        Assert.Contains("fr", languages);
        Assert.Contains("es", languages);
        Assert.Contains("pt", languages);
        Assert.Contains("zh", languages);
        Assert.Contains("tr", languages);
        Assert.Equal(7, languages.Count);
    }

    // ===== GetTranslations Tests =====

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void GetTranslations_SupportedLanguage_ReturnsDictionary(string lang)
    {
        var translations = I18nService.GetTranslations(lang);

        Assert.NotNull(translations);
        Assert.NotEmpty(translations);
        Assert.True(translations.ContainsKey("title"), $"Language '{lang}' is missing 'title' key");
        Assert.True(translations.ContainsKey("scanLibraries"), $"Language '{lang}' is missing 'scanLibraries' key");
    }

    [Fact]
    public void GetTranslations_NullLanguage_FallsBackToEnglish()
    {
        var translations = I18nService.GetTranslations(null);
        var english = I18nService.GetTranslations("en");

        Assert.Equal(english["title"], translations["title"]);
    }

    [Fact]
    public void GetTranslations_UnknownLanguage_FallsBackToEnglish()
    {
        var translations = I18nService.GetTranslations("xx");
        var english = I18nService.GetTranslations("en");

        Assert.Equal(english["title"], translations["title"]);
    }

    [Fact]
    public void GetTranslations_IsCaseInsensitive()
    {
        var lower = I18nService.GetTranslations("de");
        var upper = I18nService.GetTranslations("DE");

        Assert.Equal(lower["title"], upper["title"]);
    }

    [Fact]
    public void GetTranslations_EnglishHasAllExpectedKeys()
    {
        var translations = I18nService.GetTranslations("en");

        var expectedKeys = new[]
        {
            "title", "scanLibraries", "scanning", "scanDescription", "scanPlaceholder", "error",
            "tabOverview", "tabCodecs", "tabHealth", "tabTrends", "tabSettings", "tabArr",
            "movieVideoData", "tvVideoData", "musicAudio", "trickplayData", "subtitles", "totalFiles",
            "storageDistribution", "perLibrary",
            "cleanupStatistics", "totalBytesFreed", "totalItemsDeleted", "lastCleanup", "never",
            "videoCodecs", "audioCodecs", "containerFormats", "resolutions", "noData",
            "healthChecks", "noSubtitles", "noImages", "noNfo", "orphanedDirs",
            "growthTrend", "trendEmpty", "trendLoading", "trendError",
            "settingsTitle", "includedLibraries", "excludedLibraries",
            "orphanMinAge", "dryRunDefault", "enableSubtitleCleaner",
            "useTrash", "trashFolder", "trashRetention", "language",
            "radarrUrl", "radarrApiKey", "sonarrUrl", "sonarrApiKey",
            "saveSettings", "settingsSaved", "settingsError",
            "arrTitle", "compareRadarr", "compareSonarr",
            "inBoth", "inArrOnly", "inArrOnlyMissing", "inJellyfinOnly",
            "arrNotConfigured", "comparing",
            "exportJson", "exportCsv",
        };

        foreach (var key in expectedKeys)
        {
            Assert.True(translations.ContainsKey(key), $"English translations missing key: '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(translations[key]), $"English translation for '{key}' is empty");
        }
    }

    [Fact]
    public void GetTranslations_GermanHasTitleTranslation()
    {
        var de = I18nService.GetTranslations("de");
        Assert.Equal("Jellyfin Helper — Medienstatistiken", de["title"]);
    }

    [Fact]
    public void GetTranslations_EachLanguageReturnsDistinctInstance()
    {
        var en1 = I18nService.GetTranslations("en");
        var en2 = I18nService.GetTranslations("en");

        // Should be separate instances (not the same reference)
        Assert.NotSame(en1, en2);
    }

    // ===== configPage.html ↔ I18nService sync tests =====

    private static readonly Regex TCallRegex = new(@"T\(\s*'([^']+)'", RegexOptions.Compiled);

    private static string LoadConfigPageHtml()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("configPage.html", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static HashSet<string> ExtractKeysFromHtml()
    {
        var html = LoadConfigPageHtml();
        var matches = TCallRegex.Matches(html);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in matches)
        {
            keys.Add(match.Groups[1].Value);
        }

        return keys;
    }

    [Fact]
    public void ConfigPage_AllTKeys_ExistInEnglishTranslations()
    {
        var htmlKeys = ExtractKeysFromHtml();
        var english = I18nService.GetTranslations("en");

        var missing = htmlKeys.Where(k => !english.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0,
            $"The following T() keys from configPage.html are missing in English translations: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void ConfigPage_AllTKeys_ExistInLanguageTranslations(string lang)
    {
        var htmlKeys = ExtractKeysFromHtml();
        var translations = I18nService.GetTranslations(lang);

        var missing = htmlKeys.Where(k => !translations.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0,
            $"The following T() keys from configPage.html are missing in '{lang}' translations: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void AllLanguages_HaveSameKeysAsEnglish(string lang)
    {
        var english = I18nService.GetTranslations("en");
        var translations = I18nService.GetTranslations(lang);

        var missingInLang = english.Keys.Where(k => !translations.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(missingInLang.Count == 0,
            $"Language '{lang}' is missing the following keys present in English: {string.Join(", ", missingInLang)}");
    }
}
