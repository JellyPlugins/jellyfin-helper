using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Detached, title-balanced thematic evidence; genres, transport tags and audience permission are not interchangeable.</summary>
internal sealed class ContentFitProfile
{
    private const string AnimeKeyword = "anime";
    private const string ShounenKeyword = "shounen";
    private const string ChildrenTelevisionKeyword = "children's television";
    private const string AdultAnimationKeyword = "adult animation";
    private const string SuperPowerKeyword = "super power";
    private const string PreschoolKeyword = "preschool";
    private const string FamilyGenre = "Family";

    // Explicit label aliases, not free-text translation or synopsis understanding.
    private static readonly Dictionary<string, string> KeywordAliases = new(StringComparer.Ordinal)
    {
        ["shonen"] = ShounenKeyword, ["shōnen"] = ShounenKeyword,
        ["japanese animation"] = AnimeKeyword, ["japanische animation"] = AnimeKeyword, ["アニメ"] = AnimeKeyword,
        ["westliche animation"] = "western animation", ["amerikanische animation"] = "american animation",
        ["realverfilmung"] = "live action",
        ["kids tv"] = ChildrenTelevisionKeyword, ["children's tv"] = ChildrenTelevisionKeyword, ["kinderfernsehen"] = ChildrenTelevisionKeyword,
        ["kinderfilm"] = "children's film", ["kinderserie"] = ChildrenTelevisionKeyword,
        ["pre school"] = PreschoolKeyword, ["vorschule"] = PreschoolKeyword,
        ["adult cartoon"] = AdultAnimationKeyword, ["erwachsenenanimation"] = AdultAnimationKeyword,
        ["fighting"] = "fight", ["kampf"] = "fight",
        ["superpower"] = SuperPowerKeyword, ["superpowers"] = SuperPowerKeyword, ["super powers"] = SuperPowerKeyword, ["superkräfte"] = SuperPowerKeyword,
        ["piraten"] = "pirate", ["freundschaft"] = "friendship", ["magie"] = "magic"
    };

    private static readonly HashSet<string> RepresentationLabels = new(StringComparer.Ordinal)
    {
        AnimeKeyword, "japanese animation", "western animation", "american animation", "live action", "live action and animation"
    };

    private static readonly HashSet<string> TechnicalLabels = new(StringComparer.Ordinal)
    {
        "german dl", "dubbed", "subbed", "dual audio", "multi",
        "480p", "720p", "1080p", "2160p", "4k", "h264", "h265", "x264", "x265", "hevc", "hdr", "hdr10", "dolby vision", "bluray", "blu ray", "web dl", "webdl", "remux",
        "aac", "ac3", "eac3", "ddp", "dolby digital", "dolby digital plus", "dts", "dts hd", "truehd", "atmos"
    };

    private static readonly HashSet<string> OlderAnimationLabels = new(StringComparer.Ordinal)
    {
        ShounenKeyword, "seinen", AdultAnimationKeyword, "dark fantasy", "gore"
    };

    private static readonly HashSet<string> ChildrenLabels = new(StringComparer.Ordinal)
    {
        PreschoolKeyword, ChildrenTelevisionKeyword, "children's film", "children's adventure"
    };

    private static readonly HashSet<string> FormatLabels = new(StringComparer.Ordinal)
    {
        "based on manga", "based on comic", "based on comic book", "based on novel or book", "based on video game",
        "live action and animation", ShounenKeyword, "seinen", AdultAnimationKeyword, "anthology"
    };

    private readonly KeywordDocument[] _documents;
    private readonly Dictionary<string, double> _termWeights;
    private readonly bool _prefersOlderAnimation;
    private readonly bool _hasPositiveChildrenInterest;
    private readonly bool _hasPositiveFamilyAnimationInterest;
    private readonly bool _hasUnclassifiedFamilyAnimationInterest;
    private readonly IReadOnlySet<string> _positiveFamilyAnimationRepresentations;
    private readonly DimensionProfile[] _dimensions;
    private readonly int _positiveTitleCount;
    private readonly ContentFitProfile? _identityProfile;
    private readonly DimensionProfile _likelyRepresentations;

    private ContentFitProfile(KeywordDocument[] documents, Dictionary<string, double> weights, AnimationAudience audience, DimensionProfile[]? dimensions = null, int positiveTitleCount = 0, ContentFitProfile? identityProfile = null, DimensionProfile? likelyRepresentations = null)
    {
        _documents = documents;
        _termWeights = weights;
        _prefersOlderAnimation = audience.PrefersOlder;
        _hasPositiveChildrenInterest = audience.HasPositiveChildrenInterest;
        _hasPositiveFamilyAnimationInterest = audience.HasPositiveFamilyAnimationInterest;
        _hasUnclassifiedFamilyAnimationInterest = audience.HasUnclassifiedFamilyAnimationInterest;
        _positiveFamilyAnimationRepresentations = audience.PositiveFamilyAnimationRepresentations ?? new HashSet<string>(StringComparer.Ordinal);
        _dimensions = dimensions ?? [new([], 0), new([], 0), new([], 0)];
        _positiveTitleCount = positiveTitleCount;
        _identityProfile = identityProfile;
        _likelyRepresentations = likelyRepresentations ?? new([], 0);
    }

    /// <summary>Gets an empty profile which cannot invent thematic or audience preferences.</summary>
    internal static ContentFitProfile Empty { get; } = new([], new Dictionary<string, double>(StringComparer.Ordinal), default);

    /// <summary>Builds independent per-title documents from the same eligible own/parent metadata as other preferences.</summary>
    /// <param name="profile">The current or already self-excluded historical profile.</param>
    /// <param name="lookup">Optional known source tags; null values mean unavailable, empty values remain authoritative.</param>
    /// <param name="excluded">Optional target-title exclusion, including parent/episode identities in direct training helpers.</param>
    /// <param name="sourceCache">Optional metadata-only memo shared across historical views of one feature-building cycle.</param>
    /// <param name="metadataCutoff">Optional historical acquisition cutoff; later provider observations remain unavailable.</param>
    /// <returns>Detached semantic inputs, without retaining the caller's rows or collections.</returns>
    internal static ContentFitProfile Create(UserWatchProfile? profile, Func<Guid, IReadOnlyList<string>?>? lookup = null, IReadOnlySet<Guid>? excluded = null, SourceCache? sourceCache = null, DateTime? metadataCutoff = null)
    {
        if (profile is null)
        {
            return Empty;
        }

        var titles = BuildTitleKeywords(profile, lookup ?? (_ => null), excluded, sourceCache ?? new SourceCache(), metadataCutoff);
        var positiveTitles = titles.Values.Where(title => title.HasPositiveInteraction && (title.Ratings.Count == 0 || title.Ratings.Order().Average() > 5)).ToArray();
        var documents = positiveTitles.Where(title => title.Terms.Count > 0).Select(title => title.Terms).ToArray();
        var weights = BuildTermWeights(documents);
        var prepared = positiveTitles.Where(title => title.Terms.Count > 0)
            .Select(title => new KeywordDocument(title.Terms, title.Terms.Order(StringComparer.Ordinal).Sum(keyword => weights[keyword]), title.Genres, title.Key, title.InterestConfidence)).ToArray();
        var descriptors = positiveTitles.Select(title => ContentDescriptor.Create(title.Genres, title.Terms, title.Metadata)).ToArray();
        // A dated interest may retain weak semantic affinity without establishing a
        // current veto against another representation or narrative. Explicit favorites,
        // ratings and unknown legacy ages remain affirmative rather than invented stale data.
        var current = descriptors.Where((_, index) => positiveTitles[index].InterestConfidence >= 0.5).ToArray();
        var representations = DimensionPreferences(current.Select(descriptor => descriptor.Representation is { } value ? new[] { value } : []));
        var narratives = DimensionPreferences(current.Select(descriptor => descriptor.Narrative is { } value ? new[] { value } : []));
        var topics = DimensionPreferences(current.Select(descriptor => descriptor.Topics.ToArray()));
        var likely = DimensionPreferences(current.Select(descriptor => (descriptor.Representation ?? descriptor.ProbableRepresentation) is { } value ? new[] { value } : []));
        return new ContentFitProfile(prepared, weights, BuildAnimationAudience(positiveTitles, descriptors), [representations, narratives, topics], current.Length, BuildIdentityProfile(positiveTitles), likely);
    }

    private static AnimationAudience BuildAnimationAudience(TitleKeywords[] titles, ContentDescriptor[] descriptors)
    {
        var older = 0;
        var children = 0;
        var family = false;
        var unclassified = false;
        var familyRepresentations = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < titles.Length; index++)
        {
            var title = titles[index];
            if (title.InterestConfidence >= 0.5 && title.Genres.Contains("Animation") && title.Terms.Overlaps(OlderAnimationLabels))
            {
                older++;
            }

            if (IsChildrenTitle(title.Genres, title.Terms))
            {
                children++;
            }

            if (IsFamilyAnimation(title.Genres))
            {
                family = true;
                unclassified |= descriptors[index].Representation is null;
                if (descriptors[index].Representation is { } representation)
                {
                    familyRepresentations.Add(representation);
                }
            }
        }

        return new(older > children, children > 0, family, unclassified) { PositiveFamilyAnimationRepresentations = familyRepresentations };
    }

    private static bool IsFamilyAnimation(HashSet<string> genres)
        => genres.Contains("Animation") && genres.Contains(FamilyGenre);

    private static DimensionProfile DimensionPreferences(IEnumerable<string[]> values)
    {
        var known = values.Where(value => value.Length > 0).ToArray();
        return new(known.SelectMany(value => value).GroupBy(value => value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal), known.Length);
    }

    private static bool IsChildrenTitle(HashSet<string> genres, HashSet<string> keywords)
        => genres.Contains("Kids") || keywords.Overlaps(ChildrenLabels);

    private static ContentFitProfile? BuildIdentityProfile(TitleKeywords[] titles)
    {
        var identified = titles.Select(title => (Terms: IdentityTerms(title.Metadata), title.Genres, title.Key, title.InterestConfidence)).Where(title => title.Terms.Count > 0).ToArray();
        if (identified.Length == 0)
        {
            return null;
        }

        var weights = BuildTermWeights(identified.Select(title => title.Terms).ToArray());
        var documents = identified.Select(title => new KeywordDocument(title.Terms, title.Terms.Sum(term => weights[term]), title.Genres, title.Key, title.InterestConfidence)).ToArray();
        return new ContentFitProfile(documents, weights, default);
    }

    private static HashSet<string> IdentityTerms(TitleMetadataSnapshot? metadata)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in (metadata?.KeywordReferences ?? []).Where(IsNarrativeKeyword))
        {
            terms.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tmdb:keyword:{keyword.Id}"));
        }

        return terms;
    }

    private static bool IsNarrativeKeyword(ProviderKeyword keyword)
        => keyword.Id > 0 && keyword.Name is not null && CanonicalKeyword(keyword.Name) is { } name
            && !RepresentationLabels.Contains(name) && !GenreNormalizer.IsKnownGenreKeyword(name) && !FormatLabels.Contains(name)
            && !ChildrenLabels.Contains(name);

    private static Dictionary<Guid, TitleKeywords> BuildTitleKeywords(UserWatchProfile profile, Func<Guid, IReadOnlyList<string>?> lookup, IReadOnlySet<Guid>? excluded, SourceCache cache, DateTime? metadataCutoff)
    {
        var titles = new Dictionary<Guid, TitleKeywords>();
        var referenceTime = metadataCutoff ?? DateTime.UtcNow;
        foreach (var row in profile.WatchedItems.Where(PreferenceBuilder.IsEligibleForPreferenceWeighting))
        {
            if (IsExcludedTitle(row, excluded))
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
                title = new TitleKeywords { Key = key };
                titles.Add(key, title);
            }

            var historicalRow = metadataCutoff.HasValue ? row.AtMetadataCutoff(metadataCutoff.Value) : row;
            title.Genres.UnionWith(GenreNormalizer.Set(historicalRow.Genres));
            CapturePositiveInteraction(title, row);

            CaptureRating(title, row.UserRating);

            var tags = PreferenceBuilder.ResolveMetadataPreferenceValues(row, lookup, value => (value.Tags, value.OwnTags, value.SeriesTags));
            title.Terms.UnionWith(Keywords(tags, cache.Normalized));
            CaptureLatestMetadata(title, historicalRow.TitleMetadata);
        }

        foreach (var title in titles.Values)
        {
            AddProviderEvidence(title, cache.Normalized);
            // Metadata refresh is not renewed interest. Current explicit favorites
            // remain affirmative; dated playback confidence ages at the prediction boundary.
            title.InterestConfidence = title.HasFavorite || title.Ratings.Count > 0 || !title.LatestPositiveAtUtc.HasValue
                ? 1.0
                : Math.Exp(-Math.Log(2) * Math.Max(0, (referenceTime - title.LatestPositiveAtUtc.Value).TotalDays) / EngineConstants.GenreDecayHalfLifeDays);
        }

        return titles;
    }

    private static bool IsExcludedTitle(WatchedItemInfo row, IReadOnlySet<Guid>? excluded)
        => excluded is not null && (excluded.Contains(row.ItemId) || (row.SeriesId is { } parent && excluded.Contains(parent)));

    private static void CapturePositiveInteraction(TitleKeywords title, WatchedItemInfo row)
    {
        title.HasPositiveInteraction |= row.Played || row.IsFavorite || (row.UserRating is > 5 && double.IsFinite(row.UserRating.Value));
        title.HasFavorite |= row.IsFavorite;
        if ((row.Played || row.IsFavorite) && row.LastPlayedDate is { } playedAt
            && (!title.LatestPositiveAtUtc.HasValue || playedAt > title.LatestPositiveAtUtc.Value))
        {
            title.LatestPositiveAtUtc = playedAt;
        }
    }

    private static void CaptureLatestMetadata(TitleKeywords title, TitleMetadataSnapshot? metadata)
    {
        if (metadata is not null && (title.Metadata is null || metadata.FetchedAtUtc > title.Metadata.FetchedAtUtc))
        {
            title.Metadata = metadata;
        }
    }

    private static void CaptureRating(TitleKeywords title, double? rating)
    {
        if (rating is > 0 && double.IsFinite(rating.Value))
        {
            title.Ratings.Add(Math.Clamp(rating.Value, 0, 10));
        }
    }

    private static void AddProviderEvidence(TitleKeywords title, Dictionary<string, string?> normalized)
    {
        title.Genres.UnionWith(GenreNormalizer.Set(title.Metadata?.SourceGenres));
        if (title.Metadata?.GenreIds is { } ids)
        {
            title.Genres.UnionWith(GenreNormalizer.Set(Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery.TmdbGenreMap.ToJellyfinGenres(ids)));
        }

        if (title.Metadata?.Keywords is { } keywords)
        {
            title.Terms.UnionWith(Keywords(keywords, normalized));
        }
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
        => Match(candidateTags, null).Affinity;

    private MatchEvidence Match(IEnumerable<string>? candidateTags, IReadOnlySet<string>? genres)
    {
        var candidate = Keywords(candidateTags).Order(StringComparer.Ordinal).ToArray();
        if (candidate.Length == 0 || _documents.Length == 0)
        {
            return default;
        }

        var candidateWeight = candidate.Sum(TermWeight);
        var best = 0.0;
        var support = 0.0;
        Guid? tasteKey = null;
        foreach (var document in _documents)
        {
            var overlap = MeasureOverlap(candidate, document.Terms);

            // A specific provider keyword is valid evidence even for a sparse profile. Generic
            // Anime/Action alone gives none; scattered matches still cannot become one perfect title.
            if (overlap.SpecificMatches > 0)
            {
                best = Math.Max(best, 2.0 * overlap.Weight / (candidateWeight + document.Weight));
            }

            // A candidate may add many new story terms without losing the coherent themes of a
            // known title. Require two actual narrative matches and a majority of THAT document;
            // scattered words, language, Anime/Shounen and adaptation format alone give no support.
            var coverage = overlap.Weight / document.Weight;
            var confidence = coverage * document.InterestConfidence;
            if (overlap.NarrativeMatches >= 2 && coverage >= 0.5 && genres is not null && document.Genres.Overlaps(genres) && confidence > support)
            {
                support = confidence;
                tasteKey = document.Key;
            }
        }

        return new MatchEvidence(Math.Clamp(best, 0, 1), Math.Clamp(support, 0, 1), tasteKey);
    }

    private KeywordOverlap MeasureOverlap(string[] candidate, HashSet<string> terms)
    {
        var weight = 0.0;
        var specific = 0;
        var narrative = 0;
        foreach (var keyword in candidate)
        {
            if (!terms.Contains(keyword))
            {
                continue;
            }

            weight += TermWeight(keyword);
            var broad = RepresentationLabels.Contains(keyword) || GenreNormalizer.IsKnownGenreKeyword(keyword);
            specific += broad ? 0 : 1;
            narrative += broad || FormatLabels.Contains(keyword) || ChildrenLabels.Contains(keyword) ? 0 : 1;
        }

        return new KeywordOverlap(weight, specific, narrative);
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
        var canonical = GenreNormalizer.Set(genres);
        if (_prefersOlderAnimation && canonical.Contains("Kids"))
        {
            return similarity;
        }

        var match = Match(tags, canonical);
        return AdjustGenreSimilarity(similarity, ResolveAudienceMismatch(canonical, tags, officialRating, preferences, match), match.Affinity);
    }

    private static double AdjustGenreSimilarity(double similarity, double audienceMismatch, double thematic)
    {
        // Keep raw tag-Jaccard separate. Fine title affinity supports the existing
        // bounded genre-confidence signal instead of feeding old standardizers a different unit.
        var supported = similarity > 0 ? Math.Min(1, similarity + ((1 - similarity) * thematic * 0.5)) : similarity;
        var mismatchFactor = audienceMismatch >= 0.5 ? 0.3 : 0.5;
        return audienceMismatch > 0
            ? Math.Min(supported, similarity * mismatchFactor)
            : supported;
    }

    private double ResolveAudienceMismatch(HashSet<string> canonical, IEnumerable<string>? tags, string? officialRating, Dictionary<string, double> preferences, MatchEvidence match)
    {
        if (!_prefersOlderAnimation || _hasPositiveChildrenInterest || preferences.Count == 0 || canonical.Contains("Kids"))
        {
            return 0;
        }

        var keywords = Keywords(tags);
        var canonicalPreferences = GenreNormalizer.Preferences(preferences);
        var maximum = canonicalPreferences.Values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        if (maximum <= 0)
        {
            return 0;
        }

        if (keywords.Overlaps(OlderAnimationLabels)
            || canonicalPreferences.GetValueOrDefault(FamilyGenre) >= maximum * 0.5 || canonicalPreferences.GetValueOrDefault("Kids") >= maximum * 0.5
            || match.Affinity >= 0.5 || match.Support >= 0.5)
        {
            return 0;
        }

        if (keywords.Overlaps(ChildrenLabels))
        {
            return 0.5;
        }

        // Certification is not target audience: combined with Animation/Family this only halves
        // an otherwise unsupported genre match. No title/country/language blacklist or score ban.
        return IsAllAgesHint(officialRating) && (canonical.Contains("Animation") || canonical.Contains(FamilyGenre))
            ? 0.25
            : 0;
    }

    /// <summary>Enriches only a newly built vector; recorded prediction snapshots never pass through this method.</summary>
    /// <param name="features">The newly built 41-feature vector.</param>
    /// <param name="genres">Candidate genres.</param>
    /// <param name="tags">Candidate own/provider keywords.</param>
    /// <param name="officialRating">Optional provider certification, not a preference outcome.</param>
    /// <param name="preferences">The unchanged complete genre preference vector.</param>
    /// <param name="metadata">Optional structured provider snapshot captured at this scoring boundary.</param>
    internal void Apply(CandidateFeatures features, IReadOnlyList<string> genres, IEnumerable<string>? tags, string? officialRating, Dictionary<string, double> preferences, TitleMetadataSnapshot? metadata = null)
    {
        if (metadata?.Keywords is { } keywords)
        {
            tags = (tags ?? []).Concat(keywords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var canonical = GenreNormalizer.Set(genres);
        var match = MatchWithIdentity(tags, canonical, metadata);

        features.HasContentMetadata = (tags is not null && _documents.Length > 0) || (_identityProfile is not null && metadata?.KeywordReferences is not null);
        features.ContentFitAffinity = features.HasContentMetadata == true ? match.Affinity : null;
        var mismatch = ResolveAudienceMismatch(canonical, tags, officialRating, preferences, match);
        features.GenreSimilarity = canonical.Contains("Kids") && _prefersOlderAnimation
            ? features.GenreSimilarity
            : AdjustGenreSimilarity(features.GenreSimilarity, mismatch, match.Affinity);
        features.ContentFitSupport = null;
        features.ContentTasteKey = null;
        features.AudienceMismatch = null;
        features.ContentFamilyMismatch = CombinedFamilyMismatch(genres, tags, preferences, metadata);
        if (features.HasGenreEvidence && features.GenreSimilarity > 0)
        {
            features.ContentFitSupport = match.Support > 0 ? match.Support : null;
            features.ContentTasteKey = match.Support > 0 ? match.TasteKey : null;
            features.AudienceMismatch = mismatch > 0 ? mismatch : null;
        }
    }

    private MatchEvidence MatchWithIdentity(IEnumerable<string>? tags, HashSet<string> genres, TitleMetadataSnapshot? metadata)
    {
        var match = Match(tags, genres);
        if (_identityProfile is null || metadata?.KeywordReferences is null)
        {
            return match;
        }

        var identity = _identityProfile.Match(IdentityTerms(metadata), genres);
        // Independent identity evidence may strengthen translated labels, never dilute a
        // valid text match or count one topic twice as two narrative matches.
        return new MatchEvidence(Math.Max(match.Affinity, identity.Affinity), Math.Max(match.Support, identity.Support), identity.Support > match.Support ? identity.TasteKey : match.TasteKey);
    }

    private double? CombinedFamilyMismatch(IReadOnlyList<string> genres, IEnumerable<string>? tags, Dictionary<string, double> preferences, TitleMetadataSnapshot? metadata)
    {
        // Positive children's interest is not restricted to the majority's animation style.
        // Preserve narrative/topic checks: preschool evidence does not imply an interest in reality TV.
        var candidateGenres = ContentDescriptor.GenreEvidence(genres, metadata);
        var candidate = ContentDescriptor.Create(genres, tags, metadata);
        var preserveRepresentation = (_hasPositiveChildrenInterest && IsChildrenTitle(candidateGenres, Keywords(tags)))
            || HasPositiveFamilyAnimationRepresentation(candidateGenres, candidate);
        // An unclassified positive family-animation lane cannot inherit the majority's
        // style. This withholds a representation verdict, never narrative/topic checks.
        var family = ResolveFamilyMismatch(candidate, preserveRepresentation);
        var children = ResolveConfirmedChildrenMismatch(tags, preferences, candidateGenres);
        return family.HasValue || children.HasValue ? Math.Max(family ?? 0, children ?? 0) : null;
    }

    private bool HasPositiveFamilyAnimationRepresentation(HashSet<string> genres, ContentDescriptor candidate)
    {
        if (!IsFamilyAnimation(genres))
        {
            return false;
        }

        // Aging weakens ranking strength, not the existence of an affirmed family
        // representation. An Anime family title does not affirm western animation.
        var representation = candidate.Representation ?? candidate.ProbableRepresentation;
        return _hasUnclassifiedFamilyAnimationInterest || (representation is not null && _positiveFamilyAnimationRepresentations.Contains(representation));
    }

    private double? ResolveConfirmedChildrenMismatch(IEnumerable<string>? tags, Dictionary<string, double> preferences, HashSet<string> candidateGenres)
    {
        var keywords = Keywords(tags);
        if (!_prefersOlderAnimation || _hasPositiveChildrenInterest || !keywords.Overlaps(ChildrenLabels) || keywords.Overlaps(OlderAnimationLabels))
        {
            return null;
        }

        if (_hasPositiveFamilyAnimationInterest && IsFamilyAnimation(candidateGenres) && !keywords.Contains(PreschoolKeyword))
        {
            // Broad children's-film labels do not negate a positive family-animation
            // interest. Preschool remains a separate, explicit audience distinction.
            return null;
        }

        var genres = GenreNormalizer.Preferences(preferences);
        var maximum = genres.Values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        if (maximum <= 0 || genres.GetValueOrDefault(FamilyGenre) >= maximum * 0.5 || genres.GetValueOrDefault("Kids") >= maximum * 0.5)
        {
            return null;
        }

        // Explicit preschool/children metadata is an audience distinction, unlike a low rating.
        // A generic shared friendship/pirate keyword cannot erase this distinction.
        return 0.5;
    }

    private double? ResolveFamilyMismatch(ContentDescriptor candidate, bool preserveRepresentation)
    {
        if (_positiveTitleCount < 3)
        {
            return null;
        }

        string[][] values = [candidate.Representation is { } representation ? [representation] : [], candidate.Narrative is { } narrative ? [narrative] : [], candidate.Topics.ToArray()];
        var known = false;
        for (var dimension = 0; dimension < values.Length; dimension++)
        {
            var profile = _dimensions[dimension];
            if ((dimension == 0 && preserveRepresentation) || values[dimension].Length == 0 || profile.KnownTitleCount < _positiveTitleCount * 0.8)
            {
                continue;
            }

            known = true;
            if (!values[dimension].Any(profile.Preferences.ContainsKey))
            {
                return 0.5;
            }
        }

        var likelyRepresentation = candidate.Representation ?? candidate.ProbableRepresentation;
        if (!preserveRepresentation && likelyRepresentation is not null && _likelyRepresentations.KnownTitleCount >= _positiveTitleCount * 0.8)
        {
            if (!_likelyRepresentations.Preferences.ContainsKey(likelyRepresentation))
            {
                return 0.25;
            }

            known = true;
        }

        return known ? 0 : null;
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
        if (LanguageIdentity.IsLanguageOnlyTag(tag))
        {
            return null;
        }

        var normalized = NormalizeKeyword(tag);
        if (normalized is null || IsOperationalKeyword(normalized) || TechnicalLabels.Contains(normalized))
        {
            return null;
        }

        return KeywordAliases.GetValueOrDefault(normalized, normalized);
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

    private readonly record struct MatchEvidence(double Affinity, double Support, Guid? TasteKey = null);

    private readonly record struct AnimationAudience(bool PrefersOlder, bool HasPositiveChildrenInterest, bool HasPositiveFamilyAnimationInterest, bool HasUnclassifiedFamilyAnimationInterest)
    {
        /// <summary>Gets independently affirmed family representations without renewing their preference strength.</summary>
        internal IReadOnlySet<string>? PositiveFamilyAnimationRepresentations { get; init; }
    }

    private readonly record struct KeywordOverlap(double Weight, int SpecificMatches, int NarrativeMatches);

    private sealed record KeywordDocument(HashSet<string> Terms, double Weight, HashSet<string> Genres, Guid Key, double InterestConfidence);

    private sealed record DimensionProfile(Dictionary<string, int> Preferences, int KnownTitleCount);

    /// <summary>Serial feature-cycle memo of pure metadata transforms, never preferences, outcomes or predictions.</summary>
    internal sealed class SourceCache
    {
        internal Dictionary<string, string?> Normalized { get; } = new(StringComparer.Ordinal);

        internal Dictionary<(Guid User, Guid Title, int Provider, bool Series), Guid> Identities { get; } = [];
    }

    private sealed class TitleKeywords
    {
        internal Guid Key { get; init; }

        internal HashSet<string> Genres { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> Terms { get; } = new(StringComparer.Ordinal);

        internal bool HasPositiveInteraction { get; set; }

        internal bool HasFavorite { get; set; }

        internal DateTime? LatestPositiveAtUtc { get; set; }

        internal double InterestConfidence { get; set; }

        internal List<double> Ratings { get; } = [];

        internal TitleMetadataSnapshot? Metadata { get; set; }
    }
}
