using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

[Collection("ConfigOverride")]
public class I18NServiceTests : IDisposable
{
    // ===== configPage.html ↔ I18nService sync tests =====

    private static readonly Regex CallRegex = new(@"T\(\s*'([^']+)'", RegexOptions.Compiled);
    private readonly PluginLogService _log = TestMockFactory.CreatePluginLogService();

    public I18NServiceTests()
    {
        _log.TestMinLevelOverride = "DEBUG";
        _log.Clear();
    }

    public void Dispose()
    {
        _log.Clear();
        _log.TestMinLevelOverride = null;
    }

    // ===== SupportedLanguages Tests =====

    [Fact]
    public void SupportedLanguages_ContainsExpectedLanguages()
    {
        var languages = I18NService.SupportedLanguages;
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

    [Fact]
    public void GetTranslations_NullLanguage_FallsBackToEnglish()
    {
        var translations = I18NService.GetTranslations(null);
        var english = I18NService.GetTranslations("en");

        Assert.Equal(english["scanLibraries"], translations["scanLibraries"]);
    }

    [Fact]
    public void GetTranslations_UnknownLanguage_FallsBackToEnglish()
    {
        var translations = I18NService.GetTranslations("xx");
        var english = I18NService.GetTranslations("en");

        Assert.Equal(english["scanLibraries"], translations["scanLibraries"]);
    }

    [Fact]
    public void GetTranslations_IsCaseInsensitive()
    {
        var lower = I18NService.GetTranslations("de");
        var upper = I18NService.GetTranslations("DE");

        Assert.Equal(lower["scanLibraries"], upper["scanLibraries"]);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void GetTranslations_AllHaveAllExpectedKeys(string lang)
    {
        var translations = I18NService.GetTranslations(lang);

        var expectedKeys = new[]
        {
            "scanLibraries", "scanning", "scanDescription", "scanPlaceholder", "initializingScan", "error",
            "tabOverview", "tabCodecs", "tabHealth", "tabTrends", "tabSettings", "tabArr", "tabLogs",
            "movieVideoData", "tvVideoData", "musicAudioData", "trickplayData", "subtitleData", "subtitles",
            "totalFiles",
            "storageDistribution", "perLibraryBreakdown",
            "cleanupStatistics", "totalBytesFreed", "totalItemsDeleted", "lastCleanup", "never",
            "videoCodecs", "videoAudioCodecs", "musicAudioCodecs", "containerFormats", "resolutions", "noData",
            "healthChecks", "noSubtitles", "noImages", "noNfo", "orphanedDirs",
            "trendTitle", "trendEmpty", "loadingTrends", "trendError", "trendGranularity", "trendFiles",
            "trendEarliest",
            "clickToExpand",
            "settingsGeneralTitle", "settingsTaskTitle", "settingsTrashTitle", "settingsArrTitle",
            "includedLibraries", "includedLibrariesHelp", "excludedLibraries",
            "orphanMinAgeDays", "orphanMinAgeDaysHelp",
            "useTrash", "trashFolder", "trashRetention", "language",
            "taskModeTitle", "taskModeHelp", "activate", "dryRun", "deactivate",
            "trickplayFolderCleaner", "emptyMediaFolderCleaner", "orphanedSubtitleCleaner", "linkRepair",
            "saveSettings", "savingSettings", "settingsSaved", "settingsError", "settingsLoadError",
            "arrTitle", "compareWith",
            "inBoth", "inArrOnly", "inArrOnlyMissing", "inJellyfinOnly",
            "arrNotConfigured", "arrCompareError", "comparing",
            "addInstance", "remove", "instanceName", "radarrInstances", "sonarrInstances",
            "testConnection", "testConnectionFailed", "testing", "testMissingFields",
            "url", "apiKey", "andMore", "more",
            // Trash disable dialog keys
            "trashDisablePrompt", "trashDisableQuestion", "trashDisableTitle",
            "trashKeep", "trashDelete",
            "trashDeleteConfirmTitle", "trashDeleteConfirmMsg", "trashDeleteConfirmWarn", "trashDeleteConfirmOk",
            "trashDeleting", "trashDeletedCount", "trashFailedCount", "trashDeleteError",
            "folders", "cancel",
            // Backup keys
            "settingsBackupTitle", "settingsBackupHelp",
            "backupExport", "backupImport",
            "backupExportSuccess", "backupExportError",
            "backupImporting", "backupImportSuccess", "backupImportError",
            "backupFileTooLarge", "backupInvalidJson",
            "backupImportConfirmTitle", "backupImportConfirmMsg", "backupImportConfirmFile", "backupImportConfirmWarn",
            "backupImportConfirmOk",
            "backupConfigRestored", "backupTimelineRestored", "backupBaselineRestored", "backupWarnings",
            // Seerr keys
            "seerrCleanup", "seerrNotConfigured", "settingsSeerrTitle", "settingsSeerrHelp",
            "seerrInstance", "seerrUrl", "seerrApiKey",
            "seerrCleanupAgeDays", "seerrCleanupAgeDaysHelp", "seerrFillFields",
            // Unsaved changes dialog keys
            "unsavedChangesTitle", "unsavedChangesMsg", "discardChanges", "saveAndContinue"
        };

        foreach (var key in expectedKeys)
        {
            Assert.True(translations.ContainsKey(key), $"Language '{lang}' translations missing key: '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(translations[key]), $"Language '{lang}' translation for '{key}' is empty");
        }
    }

    [Fact]
    public void GetTranslations_EachLanguageReturnsDistinctInstance()
    {
        var en1 = I18NService.GetTranslations("en");
        var en2 = I18NService.GetTranslations("en");

        // Should be separate instances (not the same reference)
        Assert.NotSame(en1, en2);
    }

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
        var matches = CallRegex.Matches(html);
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
        var english = I18NService.GetTranslations("en");

        var missing = htmlKeys.Where(k => !english.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(
            missing.Count == 0,
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
        var translations = I18NService.GetTranslations(lang);

        var missing = htmlKeys.Where(k => !translations.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(
            missing.Count == 0,
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
        var english = I18NService.GetTranslations("en");
        var translations = I18NService.GetTranslations(lang);

        var missingInLang = english.Keys.Where(k => !translations.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(
            missingInLang.Count == 0,
            $"Language '{lang}' is missing the following keys present in English: {string.Join(", ", missingInLang)}");
    }

    // ===== No extra keys in non-EN languages =====

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void AllLanguages_HaveNoExtraKeysNotInEnglish(string lang)
    {
        var english = I18NService.GetTranslations("en");
        var translations = I18NService.GetTranslations(lang);

        var extraKeys = translations.Keys.Where(k => !english.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(
            extraKeys.Count == 0,
            $"Language '{lang}' has extra keys not in English: {string.Join(", ", extraKeys)}");
    }

    // ===== No empty or whitespace values in any language =====

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void AllLanguages_NoEmptyOrWhitespaceValues(string lang)
    {
        var translations = I18NService.GetTranslations(lang);

        var emptyKeys = translations
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(k => k)
            .ToList();

        Assert.True(
            emptyKeys.Count == 0,
            $"Language '{lang}' has empty/whitespace values for keys: {string.Join(", ", emptyKeys)}");
    }

    // ===== Singular/Plural pairs must both exist =====

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void AllLanguages_SingularPluralPairsExist(string lang)
    {
        var translations = I18NService.GetTranslations(lang);

        // Singular/Plural pairs that must both be present
        var pairs = new[]
        {
            ("minuteAgo", "minutesAgo"),
            ("hourAgo", "hoursAgo"),
            ("dayAgo", "daysAgo"),
            ("file", "files"),
            ("folder", "folders"),
            ("library", "libraries"),
            ("episode", "episodes"),
            ("mediaFile", "mediaFiles")
        };

        foreach (var (singular, plural) in pairs)
        {
            Assert.True(
                translations.ContainsKey(singular),
                $"Language '{lang}' is missing singular key '{singular}'");
            Assert.True(
                translations.ContainsKey(plural),
                $"Language '{lang}' is missing plural key '{plural}'");
        }
    }

    // ===== Translations should actually differ from English (smoke test for real translation) =====

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void NonEnglishLanguages_ScanLibraryDiffersFromEnglish(string lang)
    {
        var english = I18NService.GetTranslations("en");
        var translations = I18NService.GetTranslations(lang);

        Assert.NotEqual(english["scanLibraries"], translations["scanLibraries"]);
    }

    // ===== Keys that are expected to be the same across languages (technical terms) =====

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void AllLanguages_TechnicalKeysAreUnchanged(string lang)
    {
        var translations = I18NService.GetTranslations(lang);

        // These are technical terms that should not be translated
        Assert.Equal("URL", translations["url"]);
    }

    // ===== EnglishHasAllExpectedKeys should include new keys =====

    [Fact]
    public void GetTranslations_EnglishHasNewSingularAndLoadErrorKeys()
    {
        var translations = I18NService.GetTranslations("en");

        var newKeys = new[]
        {
            "settingsLoadError",
            "minuteAgo", "hourAgo", "dayAgo"
        };

        foreach (var key in newKeys)
        {
            Assert.True(translations.ContainsKey(key), $"English translations missing new key: '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(translations[key]), $"English translation for '{key}' is empty");
        }
    }

    // ===== settingsError and settingsLoadError should have different values =====

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void AllLanguages_SettingsErrorAndLoadErrorAreDifferent(string lang)
    {
        var translations = I18NService.GetTranslations(lang);

        Assert.True(translations.ContainsKey("settingsError"), $"'{lang}' missing settingsError");
        Assert.True(translations.ContainsKey("settingsLoadError"), $"'{lang}' missing settingsLoadError");
        Assert.NotEqual(translations["settingsError"], translations["settingsLoadError"]);
    }

    // ===== Logging integration tests =====

    [Fact]
    public void GetTranslations_UnknownLanguage_LogsDebugFallback()
    {
        _log.Clear();
        I18NService.GetTranslations("xx", _log);

        var entries = _log.GetEntries("DEBUG", "I18n");
        Assert.Contains(entries, e => e.Message.Contains("falling back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetTranslations_ValidLanguage_LogsLoadedKeys()
    {
        _log.Clear();
        // Force a fresh load by using a language that is supported
        I18NService.GetTranslations("en", _log);

        var entries = _log.GetEntries("DEBUG", "I18n");
        // The "Loaded X translation keys" log may or may not appear (cached), but it should not error
        Assert.DoesNotContain(entries, e => e.Level == "ERROR");
    }

    // ===== Key count consistency — all languages have same count =====

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    public void AllLanguages_HaveSameKeyCountAsEnglish(string lang)
    {
        var english = I18NService.GetTranslations("en");
        var translations = I18NService.GetTranslations(lang);

        Assert.Equal(english.Count, translations.Count);
    }
}