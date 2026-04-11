using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Provides internationalization (i18n) support for the plugin dashboard.
/// Loads translations from embedded JSON resource files in the i18n folder.
/// Supports: en, de, fr, es, pt, zh, tr.
/// </summary>
public static class I18nService
{
    private static readonly Assembly ThisAssembly = typeof(I18nService).Assembly;

    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the list of supported language codes.
    /// </summary>
    public static ReadOnlyCollection<string> SupportedLanguages { get; } = new List<string>
    {
        "en", "de", "fr", "es", "pt", "zh", "tr",
    }.AsReadOnly();

    /// <summary>
    /// Gets all translation strings for the specified language.
    /// Falls back to English for unknown languages.
    /// Returns a new dictionary instance on every call.
    /// </summary>
    /// <param name="languageCode">The ISO 639-1 language code.</param>
    /// <returns>A dictionary of translation keys to translated strings.</returns>
    public static Dictionary<string, string> GetTranslations(string? languageCode)
    {
        var lang = string.IsNullOrWhiteSpace(languageCode)
            ? "en"
            : languageCode
                .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0]
                .ToLowerInvariant();

        if (!SupportedLanguages.Contains(lang))
        {
            lang = "en";
        }

        // Load from cache or parse the embedded JSON resource.
        var cached = Cache.GetOrAdd(lang, static key => LoadFromResource(key));

        // Return a defensive copy so callers cannot mutate the cache.
        return new Dictionary<string, string>(cached, StringComparer.Ordinal);
    }

    /// <summary>
    /// Loads a translation dictionary from the embedded JSON resource for the given language code.
    /// </summary>
    private static Dictionary<string, string> LoadFromResource(string lang)
    {
        // Resource names follow: Jellyfin.Plugin.JellyfinHelper.i18n.<lang>.json
        var resourceName = $"Jellyfin.Plugin.JellyfinHelper.i18n.{lang}.json";

        using var stream = ThisAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded i18n resource '{resourceName}' not found. Available: {string.Join(", ", ThisAssembly.GetManifestResourceNames())}");
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (dict is null)
        {
            throw new InvalidOperationException($"Failed to deserialize i18n resource '{resourceName}'.");
        }

        // Re-create with ordinal comparer for consistent key lookups.
        return new Dictionary<string, string>(dict, StringComparer.Ordinal);
    }
}