using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Detached, title-balanced thematic evidence; genres, transport tags and audience permission are not interchangeable.</summary>
internal sealed class ContentFitProfile
{
    private static readonly HashSet<string> TechnicalLabels = new(StringComparer.Ordinal)
    {
        "german", "german dl", "deutsch", "english", "japanese", "de", "en", "ja", "dubbed", "subbed", "dual audio", "multi",
        "480p", "720p", "1080p", "2160p", "4k", "h264", "h265", "x264", "x265", "hevc", "hdr", "hdr10", "dolby vision", "bluray", "blu ray", "web dl", "webdl", "remux"
    };

    private static readonly HashSet<string> BroadLabels = new(StringComparer.Ordinal)
    {
        "anime", "animation", "action", "adventure", "comedy", "drama", "fantasy", "family", "romance", "science fiction"
    };

    private static readonly HashSet<string> OlderAnimationLabels = new(StringComparer.Ordinal)
    {
        "shounen", "seinen", "adult animation", "dark fantasy", "gore"
    };

    private static readonly HashSet<string> ChildrenLabels = new(StringComparer.Ordinal)
    {
        "preschool", "children's television", "children's film", "children's adventure", "kinderfilm", "kinderserie"
    };

    private readonly KeywordDocument[] _documents;
    private readonly Dictionary<string, double> _termWeights;
    private readonly bool _prefersOlderAnimation;

    private ContentFitProfile(KeywordDocument[] documents, Dictionary<string, double> weights, bool prefersOlderAnimation)
    {
        _documents = documents;
        _termWeights = weights;
        _prefersOlderAnimation = prefersOlderAnimation;
    }

    /// <summary>Gets an empty profile which cannot invent thematic or audience preferences.</summary>
    internal static ContentFitProfile Empty { get; } = new([], new Dictionary<string, double>(StringComparer.Ordinal), false);

    /// <summary>Builds independent per-title documents from the same eligible own/parent metadata as other preferences.</summary>
    /// <param name="profile">The current or already self-excluded historical profile.</param>
    /// <param name="lookup">Optional known source tags; null values mean unavailable, empty values remain authoritative.</param>
    /// <param name="excluded">Optional target-title exclusion, including parent/episode identities in direct training helpers.</param>
    /// <param name="sourceCache">Optional metadata-only memo shared across historical views of one feature-building cycle.</param>
    /// <returns>Detached semantic inputs, without retaining the caller's rows or collections.</returns>
    internal static ContentFitProfile Create(UserWatchProfile? profile, Func<Guid, IReadOnlyList<string>?>? lookup = null, IReadOnlySet<Guid>? excluded = null, SourceCache? sourceCache = null)
    {
        if (profile is null)
        {
            return Empty;
        }

        var titles = BuildTitleKeywords(profile, lookup ?? (_ => null), excluded, sourceCache ?? new SourceCache());
        var older = titles.Values.Count(title => title.Genres.Contains("Animation") && title.Terms.Overlaps(OlderAnimationLabels));
        var children = titles.Values.Count(title => title.Genres.Contains("Kids") || title.Terms.Overlaps(ChildrenLabels));
        var documents = titles.Values.Where(title => title.Terms.Count > 0).Select(title => title.Terms).ToArray();
        var weights = BuildTermWeights(documents);
        return new ContentFitProfile(documents.Select(document => new KeywordDocument(document, document.Order(StringComparer.Ordinal).Sum(keyword => weights[keyword]))).ToArray(), weights, older > children);
    }

    private static Dictionary<Guid, TitleKeywords> BuildTitleKeywords(UserWatchProfile profile, Func<Guid, IReadOnlyList<string>?> lookup, IReadOnlySet<Guid>? excluded, SourceCache cache)
    {
        var titles = new Dictionary<Guid, TitleKeywords>();
        foreach (var row in profile.WatchedItems.Where(PreferenceBuilder.IsEligibleForPreferenceWeighting))
        {
            if (excluded is not null && (excluded.Contains(row.ItemId) || (row.SeriesId is { } parent && excluded.Contains(parent))))
            {
                continue;
            }

            if (TitleKey(profile.UserId, row, cache) is not { } key)
            {
                // Unknown identities cannot join unrelated keyword rows into one invented coherent title.
                continue;
            }

            if (!titles.TryGetValue(key, out var title))
            {
                title = new TitleKeywords();
                titles.Add(key, title);
            }

            title.Genres.UnionWith(GenreNormalizer.Set(row.Genres));
            var tags = PreferenceBuilder.ResolveMetadataPreferenceValues(row, lookup, value => (value.Tags, value.OwnTags, value.SeriesTags));
            title.Terms.UnionWith(Keywords(tags, cache.Normalized));
        }

        return titles;
    }

    private static Guid? TitleKey(Guid user, WatchedItemInfo row, SourceCache cache)
    {
        var parent = row.SeriesId.GetValueOrDefault();
        var isSeries = parent != Guid.Empty || string.Equals(row.ItemType, "Series", StringComparison.OrdinalIgnoreCase);
        var provider = parent != Guid.Empty ? row.SeriesTmdbId : row.TmdbId;
        if (parent == Guid.Empty && string.Equals(row.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            // An orphan episode's provider ID is not a movie title identity. Its library ID
            // remains distinct from a movie carrying the same numeric provider value.
            provider = 0;
        }

        var title = parent != Guid.Empty ? parent : row.ItemId;
        if (title == Guid.Empty && provider <= 0)
        {
            return null;
        }

        var identity = (user, title, provider, isSeries);
        if (!cache.Identities.TryGetValue(identity, out var key))
        {
            key = TrainingExample.CreateTitleObservationId(user, title, provider, isSeries);
            cache.Identities.Add(identity, key);
        }

        return key;
    }

    private static Dictionary<string, double> BuildTermWeights(HashSet<string>[] documents)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (var keyword in document)
            {
                frequencies[keyword] = frequencies.GetValueOrDefault(keyword) + 1;
            }
        }

        return frequencies.ToDictionary(pair => pair.Key, pair => Math.Log(1.0 + ((documents.Length + 1.0) / (pair.Value + 1.0))), StringComparer.Ordinal);
    }

    /// <summary>Scores coherent title-level semantic overlap, without dilution from unrelated history.</summary>
    /// <param name="candidateTags">Own provider keywords, not folder-inherited metadata.</param>
    /// <returns>A bounded affinity; absent tags and broad labels alone do not manufacture a match.</returns>
    internal double ScoreTags(IEnumerable<string>? candidateTags)
    {
        var candidate = Keywords(candidateTags).Order(StringComparer.Ordinal).ToArray();
        if (candidate.Length == 0 || _documents.Length == 0)
        {
            return 0;
        }

        var candidateWeight = candidate.Sum(TermWeight);
        var best = 0.0;
        foreach (var document in _documents)
        {
            var overlap = 0.0;
            var specific = 0;
            foreach (var keyword in candidate.Where(document.Terms.Contains))
            {
                overlap += TermWeight(keyword);
                specific += BroadLabels.Contains(keyword) ? 0 : 1;
            }

            // A specific provider keyword is valid evidence even for a sparse profile. Generic
            // Anime/Action alone gives none; scattered matches still cannot become one perfect title.
            if (specific > 0)
            {
                best = Math.Max(best, 2.0 * overlap / (candidateWeight + document.Weight));
            }
        }

        return Math.Clamp(best, 0, 1);
    }

    /// <summary>Moderates unsupported broad genre enthusiasm, never permitting or excluding an age-rated title.</summary>
    /// <param name="similarity">The existing complete-vector genre affinity.</param>
    /// <param name="genres">Actual candidate genres.</param>
    /// <param name="tags">Actual own/provider keywords.</param>
    /// <param name="officialRating">Optional provider certification, used only as an ambiguous All-Ages hint.</param>
    /// <param name="preferences">The unchanged personal genre vector.</param>
    /// <returns>A soft genre affinity; unknown evidence and family viewers remain neutral.</returns>
    internal double AdjustGenreSimilarity(double similarity, IReadOnlyList<string> genres, IEnumerable<string>? tags, string? officialRating, Dictionary<string, double> preferences)
    {
        var thematic = ScoreTags(tags);
        // Keep the trained v5 tag coordinate's scale. Fine title affinity supports the existing
        // bounded genre-confidence signal instead of feeding old standardizers a different unit.
        var supported = similarity > 0 ? Math.Min(1, similarity + ((1 - similarity) * thematic * 0.5)) : similarity;
        if (!_prefersOlderAnimation || preferences.Count == 0)
        {
            return supported;
        }

        var canonical = GenreNormalizer.Set(genres);
        var keywords = Keywords(tags);
        var canonicalPreferences = GenreNormalizer.Preferences(preferences);
        var maximum = canonicalPreferences.Values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        if (maximum <= 0)
        {
            return supported;
        }

        if (canonical.Contains("Kids"))
        {
            return similarity;
        }

        if (keywords.Overlaps(OlderAnimationLabels)
            || canonicalPreferences.GetValueOrDefault("Family") >= maximum * 0.5 || canonicalPreferences.GetValueOrDefault("Kids") >= maximum * 0.5
            || thematic >= 0.5)
        {
            return supported;
        }

        if (keywords.Overlaps(ChildrenLabels))
        {
            return Math.Min(supported, similarity * 0.3);
        }

        // Certification is not target audience: combined with Animation/Family this only halves
        // an otherwise unsupported genre match. No title/country/language blacklist or score ban.
        return IsAllAgesHint(officialRating) && (canonical.Contains("Animation") || canonical.Contains("Family"))
            ? Math.Min(supported, similarity * 0.5)
            : supported;
    }

    /// <summary>Enriches only a newly built vector; recorded prediction snapshots never pass through this method.</summary>
    /// <param name="features">The newly built v5 vector.</param>
    /// <param name="genres">Candidate genres.</param>
    /// <param name="tags">Candidate own/provider keywords.</param>
    /// <param name="officialRating">Optional provider certification, not a preference outcome.</param>
    /// <param name="preferences">The unchanged complete genre preference vector.</param>
    internal void Apply(CandidateFeatures features, IReadOnlyList<string> genres, IEnumerable<string>? tags, string? officialRating, Dictionary<string, double> preferences)
    {
        features.GenreSimilarity = AdjustGenreSimilarity(features.GenreSimilarity, genres, tags, officialRating, preferences);
    }

    /// <summary>Creates semantic keyword keys without mutating input metadata or interpreting transport labels as taste.</summary>
    /// <param name="tags">Own provider tags, or null when unknown.</param>
    /// <returns>Normalized, de-duplicated semantic terms.</returns>
    internal static HashSet<string> Keywords(IEnumerable<string>? tags)
        => Keywords(tags, null);

    private static HashSet<string> Keywords(IEnumerable<string>? tags, Dictionary<string, string?>? cache)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            if (cache is null || !cache.TryGetValue(tag, out var normalized))
            {
                normalized = CanonicalKeyword(tag);
                if (cache is not null)
                {
                    cache[tag] = normalized;
                }
            }

            if (normalized is not null)
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string? CanonicalKeyword(string tag)
    {
        var normalized = NormalizeKeyword(tag);
        if (normalized is null || IsOperationalKeyword(normalized) || TechnicalLabels.Contains(normalized))
        {
            return null;
        }

        return normalized switch
        {
            "shonen" or "shōnen" => "shounen",
            "kids tv" or "children's tv" or "kinderfernsehen" => "children's television",
            "pre school" or "vorschule" => "preschool",
            "adult cartoon" or "erwachsenenanimation" => "adult animation",
            "fighting" => "fight",
            "superpower" or "superpowers" or "super powers" => "super power",
            _ => normalized
        };
    }

    private static string? NormalizeKeyword(string tag)
    {
        try
        {
            return string.Join(' ', tag.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant().Replace('-', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (ArgumentException)
        {
            // Malformed provider Unicode is unavailable evidence, not a reason to abort every user's scoring.
            return null;
        }
    }

    private static bool IsOperationalKeyword(string keyword)
    {
        if (keyword.StartsWith("je arr tag", StringComparison.Ordinal))
        {
            return true;
        }

        var separator = keyword.IndexOf(':', StringComparison.Ordinal);
        return separator >= 0 && keyword[..separator].Trim() is "radarr" or "sonarr";
    }

    private double TermWeight(string keyword)
        => _termWeights.GetValueOrDefault(keyword, Math.Log(1.0 + _documents.Length + 1.0));

    private static bool IsAllAgesHint(string? rating)
    {
        if (rating is null)
        {
            return false;
        }

        var normalized = rating.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized is "FSK0" or "FSK6" or "G" or "TVY" or "TVY7";
    }

    private sealed record KeywordDocument(HashSet<string> Terms, double Weight);

    /// <summary>Serial feature-cycle memo of pure metadata transforms, never preferences, outcomes or predictions.</summary>
    internal sealed class SourceCache
    {
        internal Dictionary<string, string?> Normalized { get; } = new(StringComparer.Ordinal);

        internal Dictionary<(Guid User, Guid Title, int Provider, bool Series), Guid> Identities { get; } = [];
    }

    private sealed class TitleKeywords
    {
        internal HashSet<string> Genres { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
    }
}
