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
            // Nothing to look up — return empty so the "fast path" branch is still taken.
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
            onFailure: ex =>
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        ex,
                        "Batch people lookup via GetPeopleNamesByItems failed, falling back to per-item GetPeople.");
                }
            });
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