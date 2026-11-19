using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Evidence-based content families. Unknown metadata never establishes a negative preference.</summary>
internal sealed record ContentDescriptor(string? Family, string? Representation = null, string? Narrative = null, IReadOnlySet<string>? Topics = null, string? ProbableRepresentation = null)
{
    internal static ContentDescriptor Create(IEnumerable<string>? genres, IEnumerable<string>? tags, TitleMetadataSnapshot? metadata = null)
    {
        var canonical = GenreNormalizer.Set(genres);
        var keywords = ContentFitProfile.Keywords(tags);
        if (metadata?.Keywords is { } providerKeywords)
        {
            keywords.UnionWith(ContentFitProfile.Keywords(providerKeywords));
        }

        if (metadata?.GenreIds is { } ids)
        {
            canonical.UnionWith(GenreNormalizer.Set(Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery.TmdbGenreMap.ToJellyfinGenres(ids)));
        }

        var narrative = NarrativeFamily(canonical, metadata?.SeriesType);
        var representation = RepresentationFamily(canonical, keywords);
        var probable = representation is null ? ProbableAnimationFamily(canonical, keywords, metadata) : null;
        var topics = DocumentaryTopics(narrative, keywords);
        if (narrative is not null)
        {
            var family = topics.Contains("true crime") ? "true crime" : narrative;
            return new(narrative == "scripted" ? representation : family, representation, narrative, topics, probable);
        }

        return new(representation, representation, Topics: topics, ProbableRepresentation: probable);
    }

    private static string? ProbableAnimationFamily(HashSet<string> genres, HashSet<string> keywords, TitleMetadataSnapshot? metadata)
    {
        if (!genres.Contains("Animation") || keywords.Overlaps(["live action", "live action and animation"]) || metadata is null)
        {
            return null;
        }

        var countries = (metadata.OriginCountries ?? []).Concat(metadata.ProductionCountries ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Combined origin/language/animation hints are probabilistic only. They never become
        // confirmed style evidence or a country-based selection ban.
        if (string.Equals(metadata.OriginalLanguage?.Trim(), "ja", StringComparison.OrdinalIgnoreCase) && countries.Contains("JP"))
        {
            return "anime";
        }

        return string.Equals(metadata.OriginalLanguage?.Trim(), "en", StringComparison.OrdinalIgnoreCase) && countries.Contains("US") ? "animation" : null;
    }

    private static HashSet<string> DocumentaryTopics(string? narrative, HashSet<string> keywords)
    {
        var topics = new HashSet<string>(StringComparer.Ordinal);
        if (narrative != "documentary")
        {
            return topics;
        }

        if (keywords.Contains("true crime"))
        {
            topics.Add("true crime");
        }

        if (keywords.Overlaps(["nature documentary", "wildlife documentary"]))
        {
            topics.Add("nature");
        }

        if (keywords.Contains("historical documentary"))
        {
            topics.Add("history");
        }

        if (keywords.Contains("sports documentary"))
        {
            topics.Add("sports");
        }

        return topics;
    }

    private static string? RepresentationFamily(HashSet<string> canonical, HashSet<string> keywords)
    {
        var liveAction = keywords.Contains("live action");
        var anime = keywords.Contains("anime") || keywords.Contains("japanese animation");
        if (keywords.Contains("live action and animation") || (liveAction && (anime || canonical.Contains("Animation"))))
        {
            return null;
        }

        if (liveAction)
        {
            return "live action";
        }

        if (anime)
        {
            return "anime";
        }

        // Animation without an Anime keyword does not prove non-Anime; language, country and
        // manga adaptation are supporting metadata, never sufficient classification evidence.
        return canonical.Contains("Animation") && keywords.Overlaps(["western animation", "american animation"])
            ? "animation"
            : null;
    }

    private static string? NarrativeFamily(HashSet<string> genres, string? seriesType)
    {
        var declared = seriesType?.Trim().ToUpperInvariant() switch
        {
            "DOCUMENTARY" => "documentary",
            "REALITY" => "reality",
            "NEWS" => "news",
            "TALK SHOW" => "talk show",
            "SCRIPTED" => "scripted",
            _ => null
        };
        if (declared is not null)
        {
            return declared;
        }

        if (genres.Contains("Documentary"))
        {
            return "documentary";
        }

        if (genres.Contains("Reality"))
        {
            return "reality";
        }

        if (genres.Contains("News"))
        {
            return "news";
        }

        return genres.Contains("Talk") ? "talk show" : null;
    }
}
