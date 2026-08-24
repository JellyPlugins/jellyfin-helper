using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Computes similarity metrics between items and user preferences:
///     genre similarity (cosine), people similarity (overlap coefficient),
///     tag similarity (Jaccard), and Jaccard from pre-built sets.
///     Also handles batch-loading people data from the library.
/// </summary>
internal sealed class SimilarityComputer
{
    /// <summary>
    ///     Person-type strings expected by <see cref="ILibraryManager.GetPeopleNamesByItems"/>.
    ///     Derived from <see cref="PersonKind"/> enum names so that any future refactor of
    ///     <see cref="EngineConstants.RelevantPersonKinds"/> automatically flows through here.
    /// </summary>
    private static readonly IReadOnlyList<string> RelevantPersonTypeStrings =
        EngineConstants.RelevantPersonKinds.Select(k => k.ToString()).ToList().AsReadOnly();

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SimilarityComputer"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    internal SimilarityComputer(
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger logger)
    {
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Batch-loads people (actors/directors) for all candidate items into a lookup dictionary.
    ///     Called once per recommendation run and shared across all users for performance.
    ///     Only stores person names for relevant types (Actor, Director) to keep memory compact.
    ///     <para>
    ///         Uses <see cref="ILibraryManager.GetPeopleNamesByItems"/> (Jellyfin 12+) as a single
    ///         database roundtrip when available, falling back to per-item
    ///         <c>ILibraryManager.GetPeople(BaseItem)</c> calls if the batch API throws for any reason.
    ///         The fallback guarantees the lookup is never worse than the pre-Jellyfin-12 implementation.
    ///     </para>
    /// </summary>
    /// <param name="candidates">All candidate base items.</param>
    /// <returns>A dictionary mapping item IDs to their associated person name sets (case-insensitive).</returns>
    internal Dictionary<Guid, HashSet<string>> BuildCandidatePeopleLookup(List<BaseItem> candidates)
    {
        // Fast path: single batch call. Filters people-types server-side, so we only
        // wrap each name list in a case-insensitive HashSet.
        var batchLookup = TryBuildPeopleLookupBatch(candidates);
        if (batchLookup is not null)
        {
            _pluginLog.LogDebug(
                "Recommendations",
                $"Built people lookup (batch) for {batchLookup.Count}/{candidates.Count} candidates.",
                _logger);
            return batchLookup;
        }

        // Fallback: per-item GetPeople with client-side type filtering. Kept identical to
        // pre-Jellyfin-12 behavior so a single failing candidate cannot abort the lookup;
        // only cancellation propagates.
        var lookup = BuildPeopleLookupPerItem(candidates);
        _pluginLog.LogDebug(
            "Recommendations",
            $"Built people lookup (per-item fallback) for {lookup.Count}/{candidates.Count} candidates.",
            _logger);
        return lookup;
    }

    /// <summary>
    ///     Tries the Jellyfin 12+ <see cref="ILibraryManager.GetPeopleNamesByItems"/> batch API.
    ///     Returns <c>null</c> on failure so the caller falls back to per-item lookups; on an
    ///     empty candidate list we short-circuit with an empty dictionary (nothing to do).
    ///     The try/catch is delegated to <see cref="BatchFallbackHelper"/> so cancellation
    ///     propagation stays in sync with the other batch call sites.
    /// </summary>
    private Dictionary<Guid, HashSet<string>>? TryBuildPeopleLookupBatch(List<BaseItem> candidates)
    {
        if (candidates.Count == 0)
        {
            // Nothing to look up - return empty so the "fast path" branch is still taken.
            return new Dictionary<Guid, HashSet<string>>();
        }

        return BatchFallbackHelper.TryRunBatch<Dictionary<Guid, HashSet<string>>?>(
            batchCall: () =>
            {
                var itemIds = candidates.Select(c => c.Id).ToList();
                var batch = _libraryManager.GetPeopleNamesByItems(itemIds, RelevantPersonTypeStrings);
                if (batch is null)
                {
                    return null;
                }

                var lookup = new Dictionary<Guid, HashSet<string>>(batch.Count);
                foreach (var kvp in batch)
                {
                    // GetPeopleNamesByItems is documented to omit items with no matches,
                    // but be defensive in case an implementation returns an empty list.
                    if (kvp.Value is null || kvp.Value.Count == 0)
                    {
                        continue;
                    }

                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in kvp.Value.Where(static n => !string.IsNullOrWhiteSpace(n)))
                    {
                        names.Add(name);
                    }

                    if (names.Count > 0)
                    {
                        lookup[kvp.Key] = names;
                    }
                }

                return lookup;
            },
            fallbackValue: null,
            // Log at Warning via _pluginLog for parity with sibling batch call sites
            // (WatchHistoryService.TryLoadUserDataBatch, UserActivityInsightsService.BuildUserDataLookup).
            // A raw _logger.LogDebug would vanish at the default production log level, so an
            // admin would never notice the batch API fell back to per-item.
            onFailure: ex => _pluginLog.LogWarning(
                "Recommendations",
                "Batch people lookup via GetPeopleNamesByItems failed, falling back to per-item GetPeople.",
                ex,
                _logger));
    }

    /// <summary>
    ///     Per-item people lookup - the pre-Jellyfin-12 implementation.
    ///     Used as a fallback when <see cref="TryBuildPeopleLookupBatch"/> fails.
    /// </summary>
    /// <param name="candidates">The candidate items.</param>
    /// <returns>A dictionary mapping item IDs to case-insensitive name sets.</returns>
    private Dictionary<Guid, HashSet<string>> BuildPeopleLookupPerItem(List<BaseItem> candidates)
    {
        var lookup = new Dictionary<Guid, HashSet<string>>(candidates.Count);

        foreach (var candidate in candidates)
        {
            try
            {
                var people = _libraryManager.GetPeople(candidate);
                if (people is null || people.Count == 0)
                {
                    continue;
                }

                HashSet<string>? names = null;
                foreach (var person in people)
                {
                    if (string.IsNullOrWhiteSpace(person.Name))
                    {
                        continue;
                    }

                    // Only include actors and directors - other types add noise without predictive value
                    if (!EngineConstants.RelevantPersonKinds.Contains(person.Type))
                    {
                        continue;
                    }

                    names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    names.Add(person.Name);
                }

                if (names is { Count: > 0 })
                {
                    lookup[candidate.Id] = names;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Do not swallow cancellation - propagate to caller
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                // Fail-soft: skip this candidate's people rather than aborting the whole lookup
                // (some item types / corrupted metadata make GetPeople throw). OOM / stack
                // overflow are excluded from the filter so they propagate as process-fatal,
                // matching BatchFallbackHelper's contract for the batch path above.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed to load people for candidate {ItemId}, skipping", candidate.Id);
                }
            }
        }

        return lookup;
    }

    /// <summary>
    ///     Computes genre similarity between a candidate item and the user's genre preference vector
    ///     using cosine similarity. This properly handles multi-genre items (e.g. Action + SciFi + Adventure)
    ///     without penalizing them for having many genres.
    /// </summary>
    /// <param name="candidateGenres">The genres of the candidate item.</param>
    /// <param name="genrePreferences">The user's genre preference vector.</param>
    /// <returns>A similarity score between 0 and 1.</returns>
    internal static double ComputeGenreSimilarity(
        IReadOnlyList<string> candidateGenres,
        Dictionary<string, double> genrePreferences)
    {
        var userNormSq = 0.0;
        foreach (var w in genrePreferences.Values)
        {
            userNormSq += w * w;
        }

        return ComputeGenreSimilarity(candidateGenres, genrePreferences, userNormSq);
    }

    /// <summary>
    ///     Computes genre similarity using a precomputed user-norm-squared value.
    ///     Use this overload in per-candidate hot loops where genrePreferences is fixed per user.
    /// </summary>
    /// <param name="candidateGenres">The genres of the candidate item.</param>
    /// <param name="genrePreferences">The user's genre preference vector.</param>
    /// <param name="precomputedUserNormSq">Precomputed sum of squared genre-preference weights.</param>
    /// <returns>A similarity score between 0 and 1.</returns>
    internal static double ComputeGenreSimilarity(
        IReadOnlyList<string> candidateGenres,
        Dictionary<string, double> genrePreferences,
        double precomputedUserNormSq)
    {
        if (candidateGenres.Count == 0 || genrePreferences.Count == 0)
        {
            return 0;
        }

        // Deduplicate candidate genres to avoid inflated similarity from repeated entries
        var uniqueCandidateGenres = new HashSet<string>(
            candidateGenres.Where(static g => !string.IsNullOrWhiteSpace(g)),
            StringComparer.OrdinalIgnoreCase);

        if (uniqueCandidateGenres.Count == 0)
        {
            return 0;
        }

        // Cosine similarity: dot(candidate, user) / (|candidate| * |user|)
        // Candidate vector: 1.0 for each genre present, 0.0 otherwise
        // User vector: preference weight for each genre
        var dotProduct = 0.0;
        var unknownGenreCount = 0;
        foreach (var genre in uniqueCandidateGenres)
        {
            if (genrePreferences.TryGetValue(genre, out var weight))
            {
                if (weight > 0)
                {
                    dotProduct += weight; // candidate component is 1.0
                }

                // weight == 0: genre is known (user watched it) but normalized to zero -
                // not counted as "unknown" since the user has been exposed to it.
            }
            else
            {
                // Genre is truly absent from the user's preference vector - never watched.
                unknownGenreCount++;
            }
        }

        if (dotProduct <= 0)
        {
            return 0;
        }

        // |candidate| = sqrt(number of unique genres) since each component is 1.0
        var candidateNorm = Math.Sqrt(uniqueCandidateGenres.Count);

        // |user| = sqrt(sum of squared weights)
        var userNorm = Math.Sqrt(precomputedUserNormSq);

        if (double.IsNaN(userNorm) || double.IsInfinity(userNorm))
        {
            return 0;
        }

        if (candidateNorm <= 0 || userNorm <= 0)
        {
            return 0;
        }

        var cosineSimilarity = Math.Min(dotProduct / (candidateNorm * userNorm), 1.0);

        if (double.IsNaN(cosineSimilarity))
        {
            return 0;
        }

        // Unknown-genre damping: reduce similarity proportionally when a candidate has genres
        // the user has never watched, so items mixing familiar and unfamiliar genres don't score
        // as high as fully-familiar ones. Factor 0.5 = moderate: "never watched" != "dislikes".
        // Example: Anime ["Animation","Action","Drama"] for an Action/Drama user:
        //   unknownFraction = 1/3, damping = 1 - 0.33 * 0.5 = 0.835 -> ~17% reduction.
        if (unknownGenreCount == 0)
        {
            return cosineSimilarity;
        }

        var unknownFraction = (double)unknownGenreCount / uniqueCandidateGenres.Count;
        const double unknownGenreDampingFactor = 0.5;
        cosineSimilarity *= 1.0 - (unknownFraction * unknownGenreDampingFactor);

        return cosineSimilarity;
    }

    /// <summary>
    ///     Computes people similarity between a candidate's cast/directors and the user's
    ///     preferred people set using Overlap coefficient: |A ∩ B| / min(|A|, |B|).
    ///     This is preferred over Jaccard for people similarity because the user's preferred
    ///     people set is typically much larger than a single candidate's cast, which would
    ///     make Jaccard converge towards zero. Overlap coefficient focuses on what fraction
    ///     of the smaller set is shared, giving a meaningful signal.
    /// </summary>
    /// <param name="candidatePeople">The candidate item's person names.</param>
    /// <param name="preferredPeople">The user's preferred person names.</param>
    /// <returns>An overlap coefficient between 0 and 1.</returns>
    /// <remarks>
    ///     Retained for the legacy unit-test suite that pins the overlap-coefficient contract
    ///     (see <c>RecommendationEngineTests.ComputePeopleSimilarity_*</c>). Production scoring
    ///     paths - both live (<c>Engine.ScoreCandidate</c>) and training
    ///     (<c>TrainingDataBuilder</c>, <c>TrainingFeatureComputer</c>) - call the WEIGHTED
    ///     overload exclusively. Prefer that overload for any new call site; this one exists
    ///     only so historical behavioural tests keep pinning the plain overlap semantics.
    /// </remarks>
    internal static double ComputePeopleSimilarity(
        HashSet<string> candidatePeople,
        HashSet<string> preferredPeople)
    {
        if (candidatePeople.Count == 0 || preferredPeople.Count == 0)
        {
            return 0;
        }

        // Iterate over the smaller set for efficiency
        var (smaller, larger) = candidatePeople.Count <= preferredPeople.Count
            ? (candidatePeople, preferredPeople)
            : (preferredPeople, candidatePeople);
        var intersection = smaller.Count(name => larger.Contains(name));

        var minSize = Math.Min(candidatePeople.Count, preferredPeople.Count);
        return minSize > 0 ? (double)intersection / minSize : 0;
    }

    /// <summary>
    ///     Weighted variant of <see cref="ComputePeopleSimilarity(HashSet{string}, HashSet{string})"/>
    ///     that scores candidates by how much of the user's <b>weight mass</b> they carry rather than
    ///     raw set membership. Used at inference by <c>Engine.ScoreCandidate</c> and across all training
    ///     phases in <c>TrainingDataBuilder</c> so the ML feature is identical on both sides.
    ///     <para>
    ///         <b>Active formula</b> (top-K weighted-budget, clamped [0, 1]):
    ///         <code>
    ///             score = clamp( matchedWeight
    ///                          / max( |candidate| × avg(topK(preferredWeight)),
    ///                                 <see cref="EngineConstants.WeightedPeopleSimilarityMinDenominator"/> ),
    ///                          0, 1 )
    ///         </code>
    ///         where <c>avg(topK(preferredWeight))</c> is the mean of the <see
    ///         cref="EngineConstants.WeightedPeopleSimilarityTopK"/> largest positive weights (the full
    ///         positive set if fewer than <c>K</c> exist). Averaging only the heavy hitters anchors the
    ///         denominator to the collaborators driving the user's preferences; averaging the full set
    ///         would let two heavy matches saturate to 1.0 on 100-person profiles full of one-off cameos.
    ///         The floor guards sparse-user overshoot and empty-preferred stability (see the floor
    ///         constant's XML doc).
    ///     </para>
    ///     <para>
    ///         <b>Intuition</b>: <c>|candidate| × avg</c> is the expected matched weight if the cast were
    ///         all "average preferred" people. Delivering exactly that scores 1.0; less scores lower. The
    ///         monotone ordering (more matched weight -> strictly higher score, up to the clamp) is what the
    ///         downstream neural ranking head needs.
    ///     </para>
    ///     <para>
    ///         <b>Design history</b>: an earlier <c>matchedWeight / min(|candidate|, totalPreferredWeight)</c>
    ///         (a) collapsed rich-profile candidates to 1.0 once matched-weight exceeded |candidate|
    ///         (ceiling-compression) and (b) let one heavy-weight match on a sparse profile hit 1.0
    ///         (sparse-user overshoot). The weighted-budget formula fixes both; see regression tests in
    ///         <c>SimilarityComputerTests</c>.
    ///     </para>
    ///     <para>
    ///         Empty-input contract mirrors <see cref="ComputePeopleSimilarity(HashSet{string},HashSet{string})"/>:
    ///         zero on empty candidate or empty weights, preserving train/serve parity.
    ///     </para>
    /// </summary>
    /// <param name="candidatePeople">The candidate item's person names.</param>
    /// <param name="preferredPeopleWeights">
    ///     The user's preferred people with per-name weights (typically the count of items each person
    ///     appears on across the user's watch history). Weights must be non-negative; zero or negative
    ///     entries are treated as absent.
    /// </param>
    /// <returns>A weighted-budget people-similarity score between 0 and 1.</returns>
    internal static double ComputePeopleSimilarity(
        HashSet<string> candidatePeople,
        IReadOnlyDictionary<string, double> preferredPeopleWeights)
    {
        if (candidatePeople.Count == 0 || preferredPeopleWeights.Count == 0)
        {
            return 0;
        }

        // Delegates to the precomputed-context overload so the sort cost is paid once on the
        // batched path. This overload keeps the eager compute for legacy call sites and unit
        // tests that pass raw dictionaries.
        var averagePreferredWeight = ComputeAveragePreferredWeight(preferredPeopleWeights);
        return ComputePeopleSimilarity(candidatePeople, preferredPeopleWeights, averagePreferredWeight);
    }

    /// <summary>
    ///     Precomputes the top-K average preferred weight used as the denominator anchor in
    ///     <see cref="ComputePeopleSimilarity(HashSet{string}, IReadOnlyDictionary{string, double})"/>.
    ///     Callers that score many candidates against the SAME <paramref name="preferredPeopleWeights"/>
    ///     (batched inference, training-data build) should call this once per user and pass the
    ///     result into <see cref="ComputePeopleSimilarity(HashSet{string}, IReadOnlyDictionary{string, double}, double)"/>
    ///     to skip the O(P log P) sort inside the per-candidate hot path.
    ///     <para>
    ///         Returns <c>0.0</c> when no positive-weight entries exist; the overload that consumes
    ///         this value treats a zero average as "no meaningful preference structure" and yields
    ///         the same result as the eager path.
    ///     </para>
    /// </summary>
    /// <param name="preferredPeopleWeights">The user's weighted preferences.</param>
    /// <returns>The mean of the top-<see cref="EngineConstants.WeightedPeopleSimilarityTopK"/> positive weights, or <c>0.0</c> if none.</returns>
    internal static double ComputeAveragePreferredWeight(
        IReadOnlyDictionary<string, double> preferredPeopleWeights)
    {
        if (preferredPeopleWeights.Count == 0)
        {
            return 0.0;
        }

        var positiveEntries = new List<double>(preferredPeopleWeights.Count);
        foreach (var kvp in preferredPeopleWeights)
        {
            if (kvp.Value > 0.0)
            {
                positiveEntries.Add(kvp.Value);
            }
        }

        if (positiveEntries.Count == 0)
        {
            return 0.0;
        }

        // Sparse profiles (positiveEntries.Count < K) fall back to the full set, so the previous
        // behaviour for low-cardinality preferences is unchanged and the floor still guards
        // pathological sparse-profile scores.
        var sampleSize = Math.Min(positiveEntries.Count, EngineConstants.WeightedPeopleSimilarityTopK);
        positiveEntries.Sort((a, b) => b.CompareTo(a));
        var topKSum = 0.0;
        for (var i = 0; i < sampleSize; i++)
        {
            topKSum += positiveEntries[i];
        }

        return topKSum / sampleSize;
    }

    /// <summary>
    ///     Batched variant of <see cref="ComputePeopleSimilarity(HashSet{string}, IReadOnlyDictionary{string, double})"/>
    ///     that takes a precomputed <paramref name="averagePreferredWeight"/> so the O(P log P) top-K
    ///     sort does not run per candidate. Callers scoring N candidates for one user can pay the
    ///     sort exactly once via <see cref="ComputeAveragePreferredWeight"/> and reuse the result.
    ///     <para>
    ///         Semantics are identical to the eager overload: matched-weight over
    ///         <c>max( |candidate| × avg, floor )</c>, clamped to <c>[0, 1]</c>. Empty inputs (either
    ///         candidate or weights) short-circuit to <c>0</c> just like the eager path.
    ///     </para>
    /// </summary>
    /// <param name="candidatePeople">The candidate item's person names.</param>
    /// <param name="preferredPeopleWeights">The user's weighted preferences (same dictionary passed to <see cref="ComputeAveragePreferredWeight"/>).</param>
    /// <param name="averagePreferredWeight">Precomputed top-K average from <see cref="ComputeAveragePreferredWeight"/>.</param>
    /// <returns>A weighted-budget people-similarity score between 0 and 1.</returns>
    internal static double ComputePeopleSimilarity(
        HashSet<string> candidatePeople,
        IReadOnlyDictionary<string, double> preferredPeopleWeights,
        double averagePreferredWeight)
    {
        if (candidatePeople.Count == 0 || preferredPeopleWeights.Count == 0 || averagePreferredWeight <= 0.0)
        {
            return 0;
        }

        // Iterate the (typically small) candidate cast rather than the full preference map:
        // a movie has O(10) people, a user's map can hold hundreds. Both lookups are O(1),
        // so iterating the smaller collection cuts this hot-path loop by an order of magnitude.
        var matchedWeight = 0.0;
        foreach (var name in candidatePeople)
        {
            if (preferredPeopleWeights.TryGetValue(name, out var weight) && weight > 0.0)
            {
                matchedWeight += weight;
            }
        }

        if (matchedWeight <= 0.0)
        {
            // No positive-weight overlap -> cannot produce a meaningful score even with the floor.
            // Early return also avoids emitting a small positive score for zero-match candidates
            // just because the floor would otherwise appear in the denominator.
            return 0;
        }

        var candidateBudget = candidatePeople.Count * averagePreferredWeight;
        var denominator = Math.Max(candidateBudget, EngineConstants.WeightedPeopleSimilarityMinDenominator);

        return Math.Clamp(matchedWeight / denominator, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes tag similarity between a candidate item's tags and the user's preferred tag set
    ///     using Jaccard similarity: |A ∩ B| / |A ∪ B|.
    ///     Returns 0 if either set is empty (no tags available).
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="preferredTags">The user's preferred tag set.</param>
    /// <returns>A Jaccard similarity score between 0 and 1.</returns>
    internal static double ComputeTagSimilarity(BaseItem candidate, HashSet<string> preferredTags)
    {
        if (candidate.Tags is not { Length: > 0 } || preferredTags.Count == 0)
        {
            return 0;
        }

        var candidateTags = new HashSet<string>(candidate.Tags, StringComparer.OrdinalIgnoreCase);
        return ComputeJaccardFromSets(candidateTags, preferredTags);
    }

    /// <summary>
    ///     Computes Jaccard similarity from pre-built HashSets (avoids repeated allocation).
    ///     Used by the MMR loop where genre sets are cached.
    /// </summary>
    /// <param name="setA">First genre set.</param>
    /// <param name="setB">Second genre set.</param>
    /// <returns>Jaccard similarity (0-1).</returns>
    internal static double ComputeJaccardFromSets(HashSet<string> setA, HashSet<string> setB)
    {
        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0;
        }

        // Iterate over the smaller set for efficiency
        var (smaller, larger) = setA.Count <= setB.Count ? (setA, setB) : (setB, setA);
        var intersection = smaller.Count(g => larger.Contains(g));

        var union = setA.Count + setB.Count - intersection;
        return union > 0 ? (double)intersection / union : 0;
    }

    /// <summary>
    ///     Computes franchise affinity: how strongly a candidate's TMDb collection matches the user's
    ///     franchise preference map. This is the single shared implementation called by BOTH the live
    ///     scoring path and the training path so the two cannot drift (train/serve parity).
    ///     <para>Returns 0.0 when the candidate has no collection name or the user has no franchise
    ///     preference (empty map / unknown franchise) - never throws, never divides.</para>
    /// </summary>
    /// <param name="candidateFranchise">The candidate's TMDb collection name, or null/empty.</param>
    /// <param name="preferredFranchises">The user's normalized franchise to weight map.</param>
    /// <returns>Franchise affinity in [0, 1].</returns>
    internal static double ComputeFranchiseAffinity(
        string? candidateFranchise,
        IReadOnlyDictionary<string, double> preferredFranchises)
    {
        if (string.IsNullOrWhiteSpace(candidateFranchise) || preferredFranchises.Count == 0)
        {
            return 0.0;
        }

        // The preference map is already max-normalized to [0,1]; a direct lookup gives the
        // user's affinity for this exact franchise (0.0 when never engaged with).
        return preferredFranchises.TryGetValue(candidateFranchise, out var weight)
            ? Math.Clamp(weight, 0.0, 1.0)
            : 0.0;
    }

    /// <summary>
    ///     Computes production-location affinity: weighted overlap of a candidate's production countries
    ///     with the user's country-preference map. Single shared implementation for live + training.
    ///     <para>Returns 0.0 when the candidate has no countries or the user has no country preference -
    ///     never throws, never divides (averages over the candidate's own country count only).</para>
    /// </summary>
    /// <param name="candidateCountries">The candidate's production countries.</param>
    /// <param name="preferredCountries">The user's normalized country to weight map.</param>
    /// <returns>Production-location affinity in [0, 1].</returns>
    internal static double ComputeProductionLocationAffinity(
        IReadOnlyList<string>? candidateCountries,
        IReadOnlyDictionary<string, double> preferredCountries)
    {
        if (candidateCountries is not { Count: > 0 } || preferredCountries.Count == 0)
        {
            return 0.0;
        }

        var matched = 0.0;
        var counted = 0;
        foreach (var country in candidateCountries)
        {
            if (string.IsNullOrWhiteSpace(country))
            {
                continue;
            }

            counted++;
            if (preferredCountries.TryGetValue(country, out var weight) && weight > 0.0)
            {
                matched += weight;
            }
        }

        // Average the matched preference weight over the candidate's own (whitespace-filtered) country
        // count. counted == 0 (all-whitespace) short-circuits the division safely.
        return counted > 0 ? Math.Clamp(matched / counted, 0.0, 1.0) : 0.0;
    }

    /// <summary>
    ///     Computes inherited-tag similarity: Jaccard overlap of a candidate's inherited tags with the
    ///     user's preferred inherited-tag set. Single shared implementation for live + training.
    ///     Delegates to the division-safe <see cref="ComputeJaccardFromSets"/>.
    ///     <para>Returns 0.0 when either side is empty.</para>
    /// </summary>
    /// <param name="candidateInheritedTags">The candidate's inherited tags.</param>
    /// <param name="preferredInheritedTags">The user's preferred inherited-tag set (case-insensitive).</param>
    /// <returns>Inherited-tag similarity in [0, 1].</returns>
    internal static double ComputeInheritedTagSimilarity(
        IReadOnlyList<string>? candidateInheritedTags,
        HashSet<string> preferredInheritedTags)
    {
        if (candidateInheritedTags is not { Count: > 0 } || preferredInheritedTags.Count == 0)
        {
            return 0.0;
        }

        var candidateSet = new HashSet<string>(
            candidateInheritedTags.Where(static t => !string.IsNullOrWhiteSpace(t)),
            StringComparer.OrdinalIgnoreCase);

        return ComputeJaccardFromSets(candidateSet, preferredInheritedTags);
    }

    /// <summary>
    ///     Computes writer affinity: weighted name-overlap of a candidate's writers with the user's
    ///     writer-preference map. Single shared implementation for live + training. Reuses the
    ///     division-safe weighted people-similarity primitive so it behaves identically to the
    ///     actor/director people channel while staying a separate signal.
    ///     <para>Returns 0.0 when either side is empty.</para>
    ///     <para>This eager overload recomputes the top-K average per call; hot paths that score many
    ///     candidates for one user should precompute the average via <see cref="ComputeAveragePreferredWeight"/>
    ///     once and call the three-argument overload instead.</para>
    /// </summary>
    /// <param name="candidateWriters">The candidate's writer names.</param>
    /// <param name="preferredWriterWeights">The user's writer to weight map.</param>
    /// <returns>Writer affinity in [0, 1].</returns>
    internal static double ComputeWriterAffinity(
        IReadOnlyList<string>? candidateWriters,
        IReadOnlyDictionary<string, double> preferredWriterWeights)
        => ComputeWriterAffinity(
            candidateWriters,
            preferredWriterWeights,
            ComputeAveragePreferredWeight(preferredWriterWeights));

    /// <summary>
    ///     Batched writer-affinity overload taking a precomputed top-K average writer weight, so the
    ///     O(W log W) sort inside <see cref="ComputeAveragePreferredWeight"/> runs once per user rather
    ///     than once per candidate. Mirrors the precomputed-average <c>ComputePeopleSimilarity</c> path.
    ///     <para>Returns 0.0 when either side is empty.</para>
    /// </summary>
    /// <param name="candidateWriters">The candidate's writer names.</param>
    /// <param name="preferredWriterWeights">The user's writer to weight map.</param>
    /// <param name="averageWriterWeight">Precomputed top-K average from <see cref="ComputeAveragePreferredWeight"/>.</param>
    /// <returns>Writer affinity in [0, 1].</returns>
    internal static double ComputeWriterAffinity(
        IReadOnlyList<string>? candidateWriters,
        IReadOnlyDictionary<string, double> preferredWriterWeights,
        double averageWriterWeight)
    {
        if (candidateWriters is not { Count: > 0 } || preferredWriterWeights.Count == 0)
        {
            return 0.0;
        }

        var candidateSet = new HashSet<string>(
            candidateWriters.Where(static w => !string.IsNullOrWhiteSpace(w)),
            StringComparer.OrdinalIgnoreCase);
        if (candidateSet.Count == 0)
        {
            return 0.0;
        }

        return ComputePeopleSimilarity(candidateSet, preferredWriterWeights, averageWriterWeight);
    }

    /// <summary>
    ///     Computes billing-weighted people affinity: like the actor/director people channel but the
    ///     candidate side carries per-person billing weights (top-billed cast count for more). Single
    ///     shared implementation for live + training. Scores the candidate's billed people against the
    ///     user's favoured billed-people map, weighting each match by the candidate's billing weight.
    ///     <para>Returns 0.0 when either side is empty - never divides by zero (denominator floored).</para>
    /// </summary>
    /// <param name="candidateBilling">Candidate name to billing weight (top-billed -> higher).</param>
    /// <param name="preferredBilledPeople">The user's favoured billed-people name to weight map.</param>
    /// <returns>Billing-weighted people affinity in [0, 1].</returns>
    internal static double ComputeBillingWeightedPeople(
        IReadOnlyDictionary<string, double> candidateBilling,
        IReadOnlyDictionary<string, double> preferredBilledPeople)
    {
        if (candidateBilling.Count == 0 || preferredBilledPeople.Count == 0)
        {
            return 0.0;
        }

        // matched = Σ over shared names of (candidate billing weight × user preference weight);
        // normalized by the candidate's total billing budget so a top-billed match dominates a
        // deep-cast match. Denominator is the candidate's own billing sum (always > 0 here).
        var matched = 0.0;
        var billingBudget = 0.0;
        foreach (var (name, billing) in candidateBilling)
        {
            if (billing <= 0.0 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            billingBudget += billing;
            if (preferredBilledPeople.TryGetValue(name, out var pref) && pref > 0.0)
            {
                matched += billing * pref;
            }
        }

        if (billingBudget <= 0.0 || matched <= 0.0)
        {
            return 0.0;
        }

        return Math.Clamp(matched / billingBudget, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes the genre/studio IDF rarity prior for a candidate: the mean inverse-document-frequency
    ///     of its genres and studios against a library-wide IDF table. Rare genres/studios score higher.
    ///     Single shared implementation for live + training.
    ///     <para>Returns 0.0 when the candidate has no genres/studios or the IDF table is empty/unavailable -
    ///     never throws, never divides by zero (the table is pre-normalized to [0,1]).</para>
    /// </summary>
    /// <param name="candidateGenres">The candidate's genres.</param>
    /// <param name="candidateStudios">The candidate's studios.</param>
    /// <param name="genreStudioIdf">Library-wide genre/studio to normalized-IDF map (null/empty -> 0.0).</param>
    /// <returns>Mean IDF rarity prior in [0, 1].</returns>
    internal static double ComputeGenreStudioIdfPrior(
        IReadOnlyList<string>? candidateGenres,
        IReadOnlyList<string>? candidateStudios,
        IReadOnlyDictionary<string, double>? genreStudioIdf)
    {
        if (genreStudioIdf is null || genreStudioIdf.Count == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        var counted = 0;

        void Accumulate(IReadOnlyList<string>? terms)
        {
            if (terms is not { Count: > 0 })
            {
                return;
            }

            foreach (var term in terms)
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    continue;
                }

                counted++;
                if (genreStudioIdf.TryGetValue(term, out var idf))
                {
                    sum += idf;
                }
            }
        }

        Accumulate(candidateGenres);
        Accumulate(candidateStudios);

        return counted > 0 ? Math.Clamp(sum / counted, 0.0, 1.0) : 0.0;
    }

    /// <summary>
    ///     Extracts billed cast/director names and their billing weights from an item's people list,
    ///     as two positionally-aligned lists suitable for caching on <c>WatchedItemInfo</c>. Billing
    ///     weight is derived from <see cref="PersonInfo.SortOrder"/> via
    ///     <see cref="EngineConstants.ComputeBillingWeight"/> - the SAME formula the live scoring path
    ///     uses - so a training example rebuilt from these cached lists yields an identical
    ///     BillingWeightedPeople value (train/serve parity). Duplicate names keep the highest weight.
    ///     <para>Returns empty lists when no billable people are present (fail-soft).</para>
    /// </summary>
    /// <param name="people">The item's people (from <c>ILibraryManager.GetPeople</c>), or null.</param>
    /// <returns>Aligned (names, weights) for the item's billed cast/directors.</returns>
    internal static (List<string> Names, List<double> Weights) ExtractBilledPeople(IReadOnlyList<PersonInfo>? people)
    {
        var names = new List<string>();
        var weights = new List<double>();
        if (people is null || people.Count == 0)
        {
            return (names, weights);
        }

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fallbackOrder = 0;
        foreach (var person in people)
        {
            if ((person.Type != PersonKind.Actor && person.Type != PersonKind.Director)
                || string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            var order = person.SortOrder ?? fallbackOrder;
            var weight = EngineConstants.ComputeBillingWeight(order);
            if (index.TryGetValue(person.Name, out var existing))
            {
                if (weight > weights[existing])
                {
                    weights[existing] = weight;
                }
            }
            else
            {
                index[person.Name] = names.Count;
                names.Add(person.Name);
                weights.Add(weight);
            }

            fallbackOrder++;
        }

        return (names, weights);
    }
}