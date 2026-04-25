using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Builds user preference vectors and sets from watch history:
///     genre preferences, studio preferences, tag preferences, and people preferences.
/// </summary>
internal static class PreferenceBuilder
{
    /// <summary>
    ///     Half-life for genre preference temporal decay in days (~180 days).
    ///     Genres watched recently contribute more than genres watched months ago.
    /// </summary>
    private const double GenreDecayHalfLifeDays = 180.0;

    /// <summary>Decay constant derived from half-life: ln(2) / halfLife.</summary>
    private static readonly double GenreDecayConstant = Math.Log(2.0) / GenreDecayHalfLifeDays;

    /// <summary>
    ///     Builds a normalized genre preference vector from the user's watch history.
    ///     Each genre gets a weight based on recency, play count, and favorites.
    ///     Recent watches count more than old ones (180-day half-life exponential decay).
    ///     Re-watched items get a PlayCount boost. Favorites get an additional boost.
    ///     Items that are favorited but not yet played are also included — the user
    ///     explicitly expressed interest, so their genres should influence preferences.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A dictionary mapping genre names to normalized weights (0–1).</returns>
    internal static Dictionary<string, double> BuildGenrePreferenceVector(UserWatchProfile profile)
    {
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (profile.WatchedItems.Count == 0 && profile.GenreDistribution.Count == 0)
        {
            return vector;
        }

        // Build genre preferences with temporal decay — recent watches count more
        var now = DateTime.UtcNow;
        foreach (var item in profile.WatchedItems)
        {
            // Include items that are played OR favorited — favorites signal explicit interest
            if ((!item.Played && !item.IsFavorite) || item.Genres is null)
            {
                continue;
            }

            // Compute temporal weight: exponential decay with ~180-day half-life
            var ageDays = item.LastPlayedDate.HasValue
                ? Math.Max(0, (now - item.LastPlayedDate.Value).TotalDays)
                : 365.0; // Default to ~1 year for items without timestamp
            var temporalWeight = Math.Exp(-GenreDecayConstant * ageDays);

            // PlayCount boost: re-watched items signal stronger preference
            var playCountBoost = Math.Clamp(item.PlayCount, 0, 5) * 0.2; // max 1.0 extra from re-watches
            var weight = temporalWeight + playCountBoost;

            // Favorite boost
            if (item.IsFavorite)
            {
                weight += EngineConstants.FavoriteGenreBoostFactor;
            }

            foreach (var genre in item.Genres.Where(static g => !string.IsNullOrWhiteSpace(g)))
            {
                vector.TryGetValue(genre, out var current);
                vector[genre] = current + weight;
            }
        }

        // Merge GenreDistribution as base weights for genres not covered by WatchedItems.
        // This ensures backward compatibility and catches genres from items whose
        // WatchedItemInfo has no Genres array (e.g. episodes inheriting parent series genres).
        // Counts are scaled into the same 0–1 dynamic range as watch-derived weights
        // so they supplement rather than dominate after normalization.
        if (profile.GenreDistribution.Count > 0)
        {
            var maxCount = profile.GenreDistribution.Values.Max();
            if (maxCount > 0)
            {
                foreach (var (genre, count) in profile.GenreDistribution)
                {
                    if (string.IsNullOrWhiteSpace(genre) || count <= 0 || vector.ContainsKey(genre))
                    {
                        continue;
                    }

                    vector[genre] = (double)count / maxCount;
                }
            }
        }

        // Normalize to 0–1 range
        if (vector.Count == 0)
        {
            return vector;
        }

        var maxWeight = vector.Values.Max();
        if (maxWeight <= 0)
        {
            return vector;
        }

        foreach (var genre in vector.Keys.ToList())
        {
            vector[genre] /= maxWeight;
        }

        return vector;
    }

    /// <summary>
    ///     Builds a set of studio names the user prefers, derived from their watched and favorited items.
    ///     Looks up the actual BaseItem objects from the candidate lookup to access Studios metadata.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="candidateLookup">Pre-built candidate lookup by item ID (shared across calls for performance).</param>
    /// <returns>A HashSet of preferred studio names (case-insensitive).</returns>
    internal static HashSet<string> BuildStudioPreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, BaseItem> candidateLookup)
    {
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect studios from watched and favorited movies and series
        foreach (var w in userProfile.WatchedItems)
        {
            // Include items that are played OR favorited
            if (!w.Played && !w.IsFavorite)
            {
                continue;
            }

            // Try direct item match (movies)
            if (candidateLookup.TryGetValue(w.ItemId, out var item) && item.Studios is { Length: > 0 })
            {
                foreach (var s in item.Studios.Where(static s => !string.IsNullOrWhiteSpace(s)))
                {
                    studios.Add(s);
                }
            }

            // Also try series match (episodes → parent series)
            if (w.SeriesId.HasValue && candidateLookup.TryGetValue(w.SeriesId.Value, out var seriesItem)
                && seriesItem.Studios is { Length: > 0 })
            {
                foreach (var s in seriesItem.Studios.Where(static s => !string.IsNullOrWhiteSpace(s)))
                {
                    studios.Add(s);
                }
            }
        }

        return studios;
    }

    /// <summary>
    ///     Builds a set of tags the user prefers, derived from their watched and favorited items.
    ///     Looks up the actual BaseItem objects from the candidate lookup to access Tags metadata.
    ///     Used for tag-based content similarity scoring.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="candidateLookup">Pre-built candidate lookup by item ID (shared across calls for performance).</param>
    /// <returns>A HashSet of preferred tag names (case-insensitive).</returns>
    internal static HashSet<string> BuildTagPreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, BaseItem> candidateLookup)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            // Include items that are played OR favorited
            if (!w.Played && !w.IsFavorite)
            {
                continue;
            }

            // Direct item match (movies)
            if (candidateLookup.TryGetValue(w.ItemId, out var item) && item.Tags is { Length: > 0 })
            {
                foreach (var t in item.Tags.Where(static t => !string.IsNullOrWhiteSpace(t)))
                {
                    tags.Add(t);
                }
            }

            // Series match (episodes → parent series)
            if (w.SeriesId.HasValue && candidateLookup.TryGetValue(w.SeriesId.Value, out var seriesItem)
                && seriesItem.Tags is { Length: > 0 })
            {
                foreach (var t in seriesItem.Tags.Where(static t => !string.IsNullOrWhiteSpace(t)))
                {
                    tags.Add(t);
                }
            }
        }

        return tags;
    }

    /// <summary>
    ///     Builds a set of preferred person names (actors/directors) from the user's watched and favorited items.
    ///     Uses the pre-built people lookup to avoid additional library queries.
    ///     Includes people from both directly watched/favorited items and series the user has watched episodes of.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="peopleLookup">Pre-built candidate people lookup (item ID → person names).</param>
    /// <returns>A HashSet of preferred person names (case-insensitive).</returns>
    internal static HashSet<string> BuildPeoplePreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, HashSet<string>> peopleLookup)
    {
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            // Include items that are played OR favorited
            if (!w.Played && !w.IsFavorite)
            {
                continue;
            }

            // Direct item match (movies, episodes)
            if (peopleLookup.TryGetValue(w.ItemId, out var itemPeople))
            {
                people.UnionWith(itemPeople.Where(static p => !string.IsNullOrWhiteSpace(p)));
            }

            // Series match (episodes → parent series)
            if (w.SeriesId.HasValue && peopleLookup.TryGetValue(w.SeriesId.Value, out var seriesPeople))
            {
                people.UnionWith(seriesPeople.Where(static p => !string.IsNullOrWhiteSpace(p)));
            }
        }

        return people;
    }

    /// <summary>
    ///     Builds the genre exposure analysis for a user. This is computed once per user
    ///     and reused for all candidate items to avoid redundant computation.
    ///     Returns a neutral (invalid) analysis when the user has insufficient watch history.
    /// </summary>
    /// <param name="genrePreferences">The user's normalized genre preference vector from <see cref="BuildGenrePreferenceVector"/>.</param>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A reusable genre exposure analysis.</returns>
    internal static GenreExposureAnalysis BuildGenreExposureAnalysis(
        Dictionary<string, double> genrePreferences,
        UserWatchProfile profile)
    {
        // Insufficient history → all features default to 0 (neutral)
        if (profile.WatchedItems.Count < EngineConstants.MinWatchCountForGenreExposure
            || genrePreferences.Count == 0)
        {
            return new GenreExposureAnalysis
            {
                UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AveragePreferenceWeight = 0,
                GenrePreferences = genrePreferences,
                IsValid = false
            };
        }

        // Compute total genre weight for share calculation
        var totalWeight = 0.0;
        foreach (var weight in genrePreferences.Values)
        {
            totalWeight += weight;
        }

        // Identify underexposed genres: those with < threshold share of total weight
        var underexposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (totalWeight > 0)
        {
            foreach (var (genre, weight) in genrePreferences)
            {
                if (weight / totalWeight < EngineConstants.GenreUnderexposureThreshold)
                {
                    underexposed.Add(genre);
                }
            }
        }

        // Identify top-N dominant genres by preference weight
        var dominant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sortedGenres = genrePreferences
            .OrderByDescending(kvp => kvp.Value)
            .Take(EngineConstants.GenreDominanceTopN);
        foreach (var kvp in sortedGenres)
        {
            dominant.Add(kvp.Key);
        }

        // Average preference weight across all genres
        var avgWeight = totalWeight / genrePreferences.Count;

        return new GenreExposureAnalysis
        {
            UnderexposedGenres = underexposed,
            DominantGenres = dominant,
            AveragePreferenceWeight = avgWeight,
            GenrePreferences = genrePreferences,
            IsValid = true
        };
    }

    /// <summary>
    ///     Computes the three genre exposure features for a single candidate item.
    ///     Uses a pre-built <see cref="GenreExposureAnalysis"/> to avoid redundant computation.
    ///     All three features are soft, continuous values in [0, 1] — they never hard-block
    ///     any genre, only provide graduated signals that the ML models can learn to weight.
    /// </summary>
    /// <param name="candidateGenres">The genres of the candidate item.</param>
    /// <param name="analysis">The pre-built genre exposure analysis for the user.</param>
    /// <returns>A tuple of (underexposure, dominanceRatio, affinityGap) all in [0, 1].</returns>
    internal static (double Underexposure, double DominanceRatio, double AffinityGap) ComputeGenreExposureFeatures(
        IReadOnlyList<string> candidateGenres,
        GenreExposureAnalysis analysis)
    {
        // Insufficient data or no candidate genres → all neutral
        if (!analysis.IsValid || candidateGenres.Count == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        var underexposedCount = 0;
        var dominantCount = 0;
        var candidateWeightSum = 0.0;
        var validCount = 0;

        foreach (var genre in candidateGenres)
        {
            // Guard against null/whitespace entries that may come from external metadata providers.
            // TryGetValue would throw ArgumentNullException on null keys, and empty strings
            // would dilute the underexposure/dominance ratios.
            if (string.IsNullOrWhiteSpace(genre))
            {
                continue;
            }

            validCount++;

            if (analysis.UnderexposedGenres.Contains(genre))
            {
                underexposedCount++;
            }

            if (analysis.DominantGenres.Contains(genre))
            {
                dominantCount++;
            }

            // Look up the user's preference weight for this genre (0 if never watched)
            analysis.GenrePreferences.TryGetValue(genre, out var weight);
            candidateWeightSum += weight;
        }

        if (validCount == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        // GenreUnderexposure: fraction of candidate genres that are underexposed
        var underexposure = (double)underexposedCount / validCount;

        // GenreDominanceRatio: fraction of candidate genres in user's top-N
        var dominanceRatio = (double)dominantCount / validCount;

        // GenreAffinityGap: how far below the user's average the candidate's genres are
        // Candidate average weight vs. user's overall average weight
        var candidateAvgWeight = candidateWeightSum / validCount;
        var affinityGap = 0.0;
        if (analysis.AveragePreferenceWeight > 0 && candidateAvgWeight < analysis.AveragePreferenceWeight)
        {
            // Normalize: 0 = at average, 1 = zero weight (complete gap)
            affinityGap = 1.0 - (candidateAvgWeight / analysis.AveragePreferenceWeight);
        }

        return (
            Math.Clamp(underexposure, 0.0, 1.0),
            Math.Clamp(dominanceRatio, 0.0, 1.0),
            Math.Clamp(affinityGap, 0.0, 1.0));
    }

    /// <summary>
    ///     Pre-computed genre exposure analysis for a user, reusable across all candidate items.
    ///     Built once per user by <see cref="BuildGenreExposureAnalysis"/> and passed to
    ///     <see cref="ComputeGenreExposureFeatures"/> for each candidate.
    /// </summary>
    internal sealed class GenreExposureAnalysis
    {
        /// <summary>Gets the set of underexposed genres (below threshold watch share).</summary>
        internal required HashSet<string> UnderexposedGenres { get; init; }

        /// <summary>Gets the user's top-N dominant genres by watch count.</summary>
        internal required HashSet<string> DominantGenres { get; init; }

        /// <summary>Gets the average preference weight across all genres.</summary>
        internal required double AveragePreferenceWeight { get; init; }

        /// <summary>Gets the full genre preference vector for per-genre weight lookups.</summary>
        internal required Dictionary<string, double> GenrePreferences { get; init; }

        /// <summary>Gets a value indicating whether the analysis is valid (user has enough history).</summary>
        internal required bool IsValid { get; init; }
    }
}
