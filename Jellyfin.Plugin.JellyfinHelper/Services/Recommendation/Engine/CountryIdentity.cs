using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Recognizes explicit country codes/English country labels without geocoding arbitrary locations or reading language metadata.</summary>
internal static class CountryIdentity
{
    private static readonly Dictionary<string, string> Identities = BuildIdentities();

    /// <summary>Returns a canonical country code only for a recognized, unambiguous country fact.</summary>
    /// <param name="value">An unchanged captured country/location token.</param>
    /// <returns>A country code, or null for blank, city, ambiguous or unknown labels.</returns>
    internal static string? Canonical(string? value)
        => value is null || !Identities.TryGetValue(value.Trim(), out var code) ? null : code;

    /// <summary>Deduplicates only recognized country identities; raw source/provenance lists remain unchanged.</summary>
    /// <param name="values">Captured facts from one source field, never a union with origin countries or languages.</param>
    /// <returns>Recognized distinct countries.</returns>
    internal static HashSet<string> Set(IEnumerable<string>? values)
        => (values ?? []).Select(Canonical).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>Coalesces code/name aliases without adding their weights twice.</summary>
    /// <param name="weights">A current canonical map or a legacy name-keyed map.</param>
    /// <returns>Finite bounded preference weights by country code.</returns>
    internal static Dictionary<string, double> Weights(IReadOnlyDictionary<string, double> weights)
    {
        var canonical = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, weight) in weights)
        {
            if (Canonical(name) is not { } code || !double.IsFinite(weight) || weight <= 0)
            {
                continue;
            }

            canonical[code] = Math.Max(canonical.GetValueOrDefault(code), Math.Min(1, weight));
        }

        return canonical;
    }

    /// <summary>Finds a genuinely matched country while preserving the original source label for display.</summary>
    /// <param name="values">Candidate production-country/location labels.</param>
    /// <param name="preferred">User production-country preferences.</param>
    /// <returns>The original matched label, or null.</returns>
    internal static string? FirstPreferred(IReadOnlyList<string> values, IReadOnlyDictionary<string, double> preferred)
    {
        var countries = Weights(preferred);
        return values.FirstOrDefault(value => Canonical(value) is { } code && countries.ContainsKey(code));
    }

    private static Dictionary<string, string> BuildIdentities()
    {
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var regions = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Where(culture => culture.Name.Length > 0)
            .Select(culture => new RegionInfo(culture.Name)).Where(region => region.TwoLetterISORegionName.Length == 2)
            .DistinctBy(region => region.TwoLetterISORegionName).ToArray();
        foreach (var region in regions)
        {
            identities[region.TwoLetterISORegionName] = region.TwoLetterISORegionName;
            identities[region.ThreeLetterISORegionName] = region.TwoLetterISORegionName;
        }

        foreach (var names in regions.GroupBy(region => region.EnglishName, StringComparer.OrdinalIgnoreCase))
        {
            // These bare labels do not distinguish the country/region intended by arbitrary location metadata.
            if (names.Count() != 1 || names.Key is "Congo" or "Korea" or "Georgia")
            {
                continue;
            }

            identities[names.Key] = names.First().TwoLetterISORegionName;
        }

        identities["United States of America"] = "US";
        identities["United Kingdom of Great Britain and Northern Ireland"] = "GB";
        identities["Czech Republic"] = "CZ";
        identities["Russian Federation"] = "RU";
        identities["Republic of Korea"] = "KR";
        identities["Democratic People's Republic of Korea"] = "KP";
        return identities;
    }
}
