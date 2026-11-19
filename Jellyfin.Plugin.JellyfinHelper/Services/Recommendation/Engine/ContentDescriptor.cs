using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Evidence-based content families. Unknown metadata never establishes a negative preference.</summary>
internal sealed record ContentDescriptor(string? Family, string? Representation = null, string? Narrative = null, string? ProbableRepresentation = null)
{
    private const string TrueCrime = "true crime";
    private const string Anime = "anime";
    private const string WesternAnimation = "western animation";
    private const string AmericanAnimation = "american animation";

    /// <summary>Gets verified documentary topics; absent topic evidence is an empty set.</summary>
    internal IReadOnlySet<string> Topics { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    internal static ContentDescriptor Create(IEnumerable<string>? genres, IEnumerable<string>? tags, TitleMetadataSnapshot? metadata = null)
    {
        var canonical = GenreNormalizer.Set(genres);
        canonical.UnionWith(GenreNormalizer.Set(metadata?.SourceGenres));
        var keywords = ContentFitProfile.Keywords(tags);
        if (metadata?.Keywords is { } providerKeywords)
        {
            keywords.UnionWith(ContentFitProfile.Keywords(providerKeywords));
        }

        if (metadata?.GenreIds is { } ids)
        {
            canonical.UnionWith(GenreNormalizer.Set(Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery.TmdbGenreMap.ToJellyfinGenres(ids)));
        }

        var narrative = metadata?.ConflictingValues?.ContainsKey(nameof(TitleMetadataSnapshot.SeriesType)) == true
            ? null
            : NarrativeFamily(canonical, metadata?.SeriesType);
        var representation = RepresentationFamily(canonical, keywords);
        var probable = representation is null ? ProbableAnimationFamily(canonical, keywords, metadata) : null;
        var topics = DocumentaryTopics(narrative, keywords);
        if (narrative is not null)
        {
            var family = topics.Contains(TrueCrime) ? TrueCrime : narrative;
            return new(narrative == "scripted" ? representation : family, representation, narrative, probable) { Topics = topics };
        }

        return new(representation, representation, ProbableRepresentation: probable) { Topics = topics };
    }

    private static string? ProbableAnimationFamily(HashSet<string> genres, HashSet<string> keywords, TitleMetadataSnapshot? metadata)
    {
        if ((!genres.Contains("Animation") && metadata?.AnimeHint != true) || keywords.Overlaps(["live action", "live action and animation"])
            || HasConflictingAnimationLabels(genres, keywords) || metadata is null
            || metadata.ConflictingValues?.ContainsKey(nameof(TitleMetadataSnapshot.OriginalLanguage)) == true)
        {
            return null;
        }

        if (metadata.AnimeHint == true)
        {
            // Sonarr's anime parsing mode is useful provider evidence, not verified style
            // or age permission. It cannot override explicit contradictory representation tags.
            return keywords.Overlaps([WesternAnimation, AmericanAnimation]) ? null : Anime;
        }

        var countries = (metadata.OriginCountries ?? []).Concat(metadata.ProductionCountries ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Combined origin/language/animation hints are probabilistic only. They never become
        // confirmed style evidence or a country-based selection ban.
        var language = NormalizeOriginalLanguage(metadata.OriginalLanguage);
        if (language == "ja" && countries.Contains("JP"))
        {
            return Anime;
        }

        return language == "en" && countries.Contains("US") ? "animation" : null;
    }

    private static HashSet<string> DocumentaryTopics(string? narrative, HashSet<string> keywords)
    {
        var topics = new HashSet<string>(StringComparer.Ordinal);
        if (narrative != "documentary")
        {
            return topics;
        }

        if (keywords.Contains(TrueCrime))
        {
            topics.Add(TrueCrime);
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
        var anime = canonical.Contains(Anime) || keywords.Contains(Anime) || keywords.Contains("japanese animation");
        if (keywords.Contains("live action and animation") || HasConflictingAnimationLabels(canonical, keywords)
            || (liveAction && (anime || canonical.Contains("Animation") || keywords.Overlaps([WesternAnimation, AmericanAnimation]))))
        {
            return null;
        }

        if (liveAction)
        {
            return "live action";
        }

        if (anime)
        {
            return Anime;
        }

        // Animation without an Anime keyword does not prove non-Anime; language, country and
        // manga adaptation are supporting metadata, never sufficient classification evidence.
        return canonical.Contains("Animation") && keywords.Overlaps([WesternAnimation, AmericanAnimation])
            ? "animation"
            : null;
    }

    private static bool HasConflictingAnimationLabels(HashSet<string> genres, HashSet<string> keywords)
        => (genres.Contains(Anime) || keywords.Overlaps([Anime, "japanese animation"])) && keywords.Overlaps([WesternAnimation, AmericanAnimation]);

    private static string? NarrativeFamily(HashSet<string> genres, string? seriesType)
    {
        var declared = NormalizeNarrativeType(seriesType);
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

    internal static string? NormalizeOriginalLanguage(string? language)
        => language is not null && LanguageIdentity.IsLanguageOnlyTag(language) ? LanguageIdentity.GetIso6391Code(language) : null;

    internal static string? NormalizeNarrativeType(string? seriesType)
        => GenreNormalizer.Name(seriesType).ToUpperInvariant() switch
        {
            "DOCUMENTARY" => "documentary",
            "REALITY" => "reality",
            "NEWS" => "news",
            "TALK" => "talk show",
            "SCRIPTED" => "scripted",
            _ => null
        };
}
