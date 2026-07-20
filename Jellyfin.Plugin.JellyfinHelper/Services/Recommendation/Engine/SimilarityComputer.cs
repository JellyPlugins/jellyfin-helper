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
    // RelevantPersonTypeStrings was used by the Jellyfin 12+ GetPeopleNamesByItems batch API.
    // Removed for 10.11.x compatibility — per-item fallback uses GetPeople(BaseItem) directly.

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
    /// </summary>
    /// <param name="candidates">All candidate base items.</param>
    /// <returns>A dictionary mapping item IDs to their associated person name sets (case-insensitive).</returns>
    internal Dictionary<Guid, HashSet<string>> BuildCandidatePeopleLookup(List<BaseItem> candidates)
    {
        // Fast path: attempt a single batch call to the library. On success, the
        // returned dictionary already filters people-types server-side, so we only
        // need to wrap each name list in a case-insensitive HashSet.
        var batchLookup = TryBuildPeopleLookupBatch(candidates);
        if (batchLookup is not null)
        {
            _pluginLog.LogDebug(
                "Recommendations",
                $"Built people lookup (batch) for {batchLookup.Count}/{candidates.Count} candidates.",
                _logger);
            return batchLookup;
        }

        // Fallback path: per-item GetPeople with client-side type filtering.
        // Kept identical to the pre-Jellyfin-12 behavior so that a single failing
        // candidate cannot abort the entire lookup — only cancellation propagates.
        var lookup = BuildPeopleLookupPerItem(candidates);
        _pluginLog.LogDebug(
            "Recommendations",
            $"Built people lookup (per-item fallback) for {lookup.Count}/{candidates.Count} candidates.",
            _logger);
        return lookup;
    }

    /// <summary>
    ///     Returns null (no batch API in 10.11.x) so the caller falls back to per-item lookups.
    /// </summary>
    private Dictionary<Guid, HashSet<string>>? TryBuildPeopleLookupBatch(List<BaseItem> candidates)
    {
        if (candidates.Count == 0)
        {
            // Nothing to look up — return empty so the "fast path" branch is still taken.
            return new Dictionary<Guid, HashSet<string>>();
        }

        return BatchFallbackHelper.TryRunBatch<Dictionary<Guid, HashSet<string>>?>(
            batchCall: () =>
            {
                // GetPeopleNamesByItems is a Jellyfin 12+ API; not available in 10.11.x.
                return null;
            },
            fallbackValue: null,
            // Log at Warning via _pluginLog for parity with the sibling batch call sites
            // (WatchHistoryService.TryLoadUserDataBatch and UserActivityInsightsService.BuildUserDataLookup).
            // A raw _logger.LogDebug here would silently disappear at the default production
            // log level, so an admin would never notice the batch API fell back to per-item.
            onFailure: ex => _pluginLog.LogWarning(
                "Recommendations",
                "Batch people lookup via GetPeopleNamesByItems failed, falling back to per-item GetPeople.",
                ex,
                _logger));
    }

    /// <summary>
    ///     Per-item people lookup — the pre-Jellyfin-12 implementation.
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
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Graceful fallback: skip this candidate's people data rather than failing the entire lookup.
                // Some item types or corrupted metadata may cause GetPeople to throw.
                // OOM / stack overflow are excluded from the filter so they can propagate up as
                // process-fatal errors instead of being silently absorbed here — matches the
                // contract enforced centrally by BatchFallbackHelper for the batch path above.
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
        var userNormSq = 0.0;
        foreach (var weight in genrePreferences.Values)
        {
            userNormSq += weight * weight;
        }

        var userNorm = Math.Sqrt(userNormSq);

        if (candidateNorm <= 0 || userNorm <= 0)
        {
            return 0;
        }

        var cosineSimilarity = Math.Min(dotProduct / (candidateNorm * userNorm), 1.0);

        // Unknown-genre damping: when a candidate has genres the user has never watched,
        // reduce the similarity proportionally. This prevents items that share some common
        // genres (e.g. "Action") but also have unfamiliar genres (e.g. "Animation") from
        // scoring as high as items where ALL genres are familiar.
        // Factor 0.5 = moderate damping: "never watched" ≠ "dislikes", just less confident.
        // Example: Anime ["Animation", "Action", "Drama"] for an Action/Drama user:
        //   unknownFraction = 1/3, damping = 1 - 0.33 * 0.5 = 0.835 → ~17% reduction.
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
    ///     paths — both live (<c>Engine.ScoreCandidate</c>) and training
    ///     (<c>TrainingDataBuilder</c>, <c>TrainingFeatureComputer</c>) — call the WEIGHTED
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
    ///     that scores candidates based on how much of the user's <b>weight mass</b> they carry,
    ///     rather than raw set membership. Roadmap v3 (C2 hardening pass) — used at inference by
    ///     <c>Engine.ScoreCandidate</c> and consistently across all training phases in
    ///     <c>TrainingDataBuilder</c> so the ML feature has identical semantics on both sides.
    ///     <para>
    ///         <b>Active formula</b> (top-K weighted-budget, clamped [0, 1]):
    ///         <code>
    ///             score = clamp( matchedWeight
    ///                          / max( |candidate| × avg(topK(preferredWeight)),
    ///                                 <see cref="EngineConstants.WeightedPeopleSimilarityMinDenominator"/> ),
    ///                          0, 1 )
    ///         </code>
    ///         where <c>avg(topK(preferredWeight))</c> is the mean of the <see
    ///         cref="EngineConstants.WeightedPeopleSimilarityTopK"/> largest positive weights (the
    ///         full positive set when the user has fewer positive entries than <c>K</c>). Averaging
    ///         only the heavy hitters keeps the denominator anchored to the collaborators who
    ///         actually drive the user's preference structure — averaging over the full positive
    ///         set would let two heavy-hitter matches saturate the score at 1.0 on 100-person
    ///         profiles dominated by one-off cameos. The floor guards two additional failure modes
    ///         (sparse-user overshoot and empty-preferred short-circuit stability) — see the floor
    ///         constant's XML doc for the full rationale.
    ///     </para>
    ///     <para>
    ///         <b>Intuition</b>: the candidate-budget <c>|candidate| × avg</c> is the expected matched
    ///         weight if the candidate's cast were composed entirely of "average preferred" people.
    ///         A candidate that delivers exactly that budget scores 1.0; delivering less scores
    ///         proportionally lower. The monotone ordering (more matched weight → strictly higher
    ///         score, up to the clamp) is what the downstream neural ranking head needs to learn
    ///         person-similarity as a meaningful signal.
    ///     </para>
    ///     <para>
    ///         <b>Design history</b>: an earlier iteration used the naive
    ///         <c>matchedWeight / min(|candidate|, totalPreferredWeight)</c>. That formula
    ///         (a) collapsed all rich-profile candidates to 1.0 as soon as matched-weight exceeded
    ///         |candidate| (ceiling-compression) and (b) let a single heavy-weight match on a sparse
    ///         profile lift the score all the way to 1.0 (sparse-user overshoot). The weighted-budget
    ///         formula addresses both, with explicit regression tests in <c>SimilarityComputerTests</c>.
    ///     </para>
    ///     <para>
    ///         Empty-input contract mirrors <see cref="ComputePeopleSimilarity(HashSet{string},HashSet{string})"/>:
    ///         zero on either empty candidate or empty weights, so train/serve parity is preserved.
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

        // Delegates to the precomputed-context overload so the sorting cost is only paid once
        // when a caller adopts the batched path. This overload keeps the eager compute for
        // legacy call sites and unit tests that pass raw dictionaries.
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

        // Iterate the (typically small) candidate cast rather than the full preference
        // map: a movie has O(10) people while a user's preference map can hold hundreds
        // of names. Both HashSet.Contains and Dictionary.TryGetValue are O(1), so
        // iterating the smaller collection cuts this hot-path loop by an order of
        // magnitude on realistic data.
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
            // No positive-weight overlap → cannot produce a meaningful score even with the floor.
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
    /// <returns>Jaccard similarity (0–1).</returns>
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
}