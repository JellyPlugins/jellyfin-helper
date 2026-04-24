using System;
using System.Collections.Generic;
using System.Linq;
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

                    // Only include actors and directors — other types add noise without predictive value
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
                throw; // Do not swallow cancellation — propagate to caller
            }
            catch (Exception ex)
            {
                // Graceful fallback: skip this candidate's people data rather than failing the entire lookup.
                // Some item types or corrupted metadata may cause GetPeople to throw.
                _logger.LogDebug(ex, "Failed to load people for candidate {ItemId}, skipping", candidate.Id);
            }
        }

        _pluginLog.LogDebug(
            "Recommendations",
            $"Built people lookup for {lookup.Count}/{candidates.Count} candidates.",
            _logger);

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
        foreach (var genre in uniqueCandidateGenres)
        {
            if (genrePreferences.TryGetValue(genre, out var weight))
            {
                dotProduct += weight; // candidate component is 1.0
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

        return Math.Min(dotProduct / (candidateNorm * userNorm), 1.0);
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

        var intersection = 0;
        // Iterate over the smaller set for efficiency
        var (smaller, larger) = candidatePeople.Count <= preferredPeople.Count
            ? (candidatePeople, preferredPeople)
            : (preferredPeople, candidatePeople);
        foreach (var name in smaller)
        {
            if (larger.Contains(name))
            {
                intersection++;
            }
        }

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

        var intersection = 0;
        // Iterate over the smaller set for efficiency
        var (smaller, larger) = setA.Count <= setB.Count ? (setA, setB) : (setB, setA);
        foreach (var g in smaller)
        {
            if (larger.Contains(g))
            {
                intersection++;
            }
        }

        var union = setA.Count + setB.Count - intersection;
        return union > 0 ? (double)intersection / union : 0;
    }
}