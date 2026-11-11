using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Separates provider genre identities from localized display names. Unknown genres remain usable.</summary>
internal static class GenreNormalizer
{
    private static readonly Dictionary<string, string> Aliases = BuildAliases();
    private static readonly ConcurrentDictionary<string, string> ProviderAliases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CatalogLock = new();
    private static int _catalogVersion;

    /// <summary>Gets the version of provider-supplied aliases used by derived genre analyses.</summary>
    internal static int CatalogVersion => Volatile.Read(ref _catalogVersion);

    /// <summary>Normalizes one label without changing the meaning of compound genres.</summary>
    /// <param name="genre">The original metadata label.</param>
    /// <returns>The canonical label, or an empty string for absent metadata.</returns>
    internal static string Name(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return string.Empty;
        }

        var text = genre.Trim();
        try
        {
            text = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Malformed custom metadata must not abort a whole user's recommendations.
        }

        return Aliases.TryGetValue(text, out var known) ? known : ProviderAliases.GetValueOrDefault(text, text);
    }

    /// <summary>Learns a localized provider label from its stable genre ID without overwriting known meanings.</summary>
    /// <param name="id">A TMDb genre identity, not a title or keyword ID.</param>
    /// <param name="label">The provider's localized display name.</param>
    /// <returns>Whether a new alias was registered.</returns>
    internal static bool RegisterProviderGenre(int id, string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 200)
        {
            return false;
        }

        var canonical = TmdbGenreMap.ToJellyfinGenres([id]).FirstOrDefault();
        if (canonical is null)
        {
            return false;
        }

        var name = Name(label);
        if (Aliases.ContainsKey(name))
        {
            return false;
        }

        lock (CatalogLock)
        {
            if (ProviderAliases.Count >= 4096 || !ProviderAliases.TryAdd(name, canonical))
            {
                return false;
            }

            Interlocked.Increment(ref _catalogVersion);
            return true;
        }
    }

    /// <summary>Builds distinct semantic genres; TV's combined categories share their movie components.</summary>
    /// <param name="genres">Original display labels, which are never modified.</param>
    /// <returns>A case-insensitive set of genre identities.</returns>
    internal static HashSet<string> Set(IEnumerable<string>? genres)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (genres is null)
        {
            return result;
        }

        foreach (var genre in genres)
        {
            Add(result, Name(genre));
        }

        return result;
    }

    /// <summary>Normalizes prebuilt genre sets without allocating again when the input already has canonical identities.</summary>
    /// <param name="genres">A raw or already-normalized set from a training cache.</param>
    /// <returns>The original canonical set or an independent normalized copy.</returns>
    internal static HashSet<string> CanonicalSet(HashSet<string> genres)
    {
        ArgumentNullException.ThrowIfNull(genres);
        return genres.Comparer == StringComparer.OrdinalIgnoreCase && genres.All(name => name.Length > 0 && Name(name) == name && !IsCompound(name))
            ? genres
            : Set(genres);
    }

    /// <summary>Tests overlap without allocating a normalized copy of every watch-history row.</summary>
    /// <param name="canonical">A normalized candidate set.</param>
    /// <param name="genres">Original history labels.</param>
    /// <returns>Whether at least one semantic genre overlaps.</returns>
    internal static bool Overlaps(HashSet<string> canonical, IEnumerable<string>? genres)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (genres is null)
        {
            return false;
        }

        return genres.Select(Name).Any(name => name switch
            {
                "Action & Adventure" => canonical.Contains("Action") || canonical.Contains("Adventure"),
                "Sci-Fi & Fantasy" => canonical.Contains("Science Fiction") || canonical.Contains("Fantasy"),
                "War & Politics" => canonical.Contains("War") || canonical.Contains("Politics"),
                _ => name.Length > 0 && canonical.Contains(name)
            });
    }

    /// <summary>Normalizes legacy preference maps, merging translations and expanding combined TV categories.</summary>
    /// <param name="preferences">Genre weights, including custom labels.</param>
    /// <returns>The same map when already normalized; otherwise an independent normalized map.</returns>
    internal static Dictionary<string, double> Preferences(Dictionary<string, double> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Comparer == StringComparer.OrdinalIgnoreCase && preferences.Keys.All(key => key.Length > 0 && Name(key) == key && !IsCompound(key)))
        {
            return preferences;
        }

        var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (genre, weight) in preferences)
        {
            foreach (var name in Set([genre]))
            {
                normalized[name] = normalized.GetValueOrDefault(name) + weight;
            }
        }

        return normalized;
    }

    /// <summary>Finds an original history label for a canonical reason anchor.</summary>
    /// <param name="canonical">The internal genre identity.</param>
    /// <param name="originals">Display labels from the user's profile.</param>
    /// <returns>A matching original label, falling back to the internal name.</returns>
    internal static string DisplayName(string canonical, IEnumerable<string> originals)
        => originals.FirstOrDefault(label => Overlaps(Set([canonical]), [label])) ?? canonical;

    /// <summary>Finds the strongest positively matched candidate label, preserving its original display spelling.</summary>
    /// <param name="candidates">Only genres actually belonging to the candidate.</param>
    /// <param name="preferences">Raw or canonical personal weights.</param>
    /// <returns>A genuine display anchor, or null when nothing matches.</returns>
    internal static string? BestMatch(IEnumerable<string> candidates, Dictionary<string, double> preferences)
    {
        preferences = Preferences(preferences);
        string? best = null;
        var bestWeight = 0.0;
        foreach (var candidate in candidates)
        {
            var weight = Set([candidate]).Select(genre => preferences.GetValueOrDefault(genre)).DefaultIfEmpty(0.0).Max();
            if (weight > bestWeight)
            {
                best = candidate;
                bestWeight = weight;
            }
        }

        return best;
    }

    private static bool IsCompound(string name)
        => name is "Action & Adventure" or "Sci-Fi & Fantasy" or "War & Politics";

    private static void Add(HashSet<string> result, string name)
    {
        switch (name)
        {
            case "":
                break;
            case "Action & Adventure":
                result.Add("Action");
                result.Add("Adventure");
                break;
            case "Sci-Fi & Fantasy":
                result.Add("Science Fiction");
                result.Add("Fantasy");
                break;
            case "War & Politics":
                result.Add("War");
                result.Add("Politics");
                break;
            default:
                result.Add(name);
                break;
        }
    }

    /// <summary>Reads genre identities and aliases from a normal flat i18n dictionary.</summary>
    /// <param name="aliases">The independent alias catalog being built.</param>
    /// <param name="language">Translation JSON, including unrelated UI keys.</param>
    internal static void AddLanguageCatalog(Dictionary<string, string> aliases, JsonElement language)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        foreach (var entry in language.EnumerateObject())
        {
            if (!entry.Name.StartsWith("genre.", StringComparison.Ordinal) || entry.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var canonical = CatalogGenre(entry.Name);
            if (canonical is null)
            {
                continue;
            }

            foreach (var label in entry.Value.GetString()!.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                AddCatalogAlias(aliases, label, canonical);
            }
        }
    }

    private static string? CatalogGenre(string key)
    {
        var idText = key["genre.".Length..];
        if (idText.EndsWith(".aliases", StringComparison.Ordinal))
        {
            idText = idText[..^".aliases".Length];
        }

        return int.TryParse(idText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? TmdbGenreMap.ToJellyfinGenres([id]).FirstOrDefault()
            : null;
    }

    private static void AddCatalogAlias(Dictionary<string, string> aliases, string label, string canonical)
    {
        var normalized = label.Normalize(NormalizationForm.FormKC);
        if (aliases.TryGetValue(normalized, out var existing) && existing != canonical)
        {
            throw new InvalidDataException($"Conflicting genre alias '{label}' for '{canonical}'.");
        }

        aliases[normalized] = canonical;
    }

    private static Dictionary<string, string> BuildAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(GenreNormalizer).Assembly;
        var prefix = assembly.GetName().Name + ".i18n.";
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Missing genre translation resource.");
            using var document = JsonDocument.Parse(stream);
            AddLanguageCatalog(aliases, document.RootElement);
        }

        return aliases;
    }
}
