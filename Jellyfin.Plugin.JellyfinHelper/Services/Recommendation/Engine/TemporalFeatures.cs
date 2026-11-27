using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Computes temporal context features: day-of-week affinity, hour-of-day affinity, and time-bucket classification.
/// </summary>
internal static class TemporalFeatures
{
    /// <summary>
    ///     Computes day-of-week affinity: how well a candidate's genre matches the user's viewing patterns for the current day of week.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="now">
    ///     Reference point for "now" (defaults to <see cref="DateTime.UtcNow"/>).
    ///     Exposed for deterministic unit testing.
    /// </param>
    /// <returns>An affinity score between 0 and 1.</returns>
    internal static double ComputeDayOfWeekAffinity(BaseItem candidate, UserWatchProfile userProfile, DateTime? now = null)
        => ComputeTemporalAffinity(candidate.Genres, userProfile, now ?? DateTime.UtcNow, true, candidate.Id, candidate is Series ? candidate.Id : null);

    /// <summary>Computes day affinity from the exact genres captured with one prediction.</summary>
    /// <param name="candidateGenres">Captured effective genres, not a later item lookup.</param>
    /// <param name="userProfile">The already scoped playback profile.</param>
    /// <param name="predictionTimeUtc">The prediction's single reference clock.</param>
    /// <param name="excludedItem">Optional target item to exclude.</param>
    /// <param name="excludedSeries">Optional parent series to exclude.</param>
    /// <returns>The same bounded affinity used by historical reconstruction.</returns>
    internal static double ComputeDayOfWeekAffinity(IReadOnlyCollection<string>? candidateGenres, UserWatchProfile userProfile, DateTime predictionTimeUtc, Guid excludedItem = default, Guid? excludedSeries = null)
        => ComputeTemporalAffinity(candidateGenres, userProfile, predictionTimeUtc, true, excludedItem, excludedSeries);

    /// <summary>
    ///     Computes hour-of-day affinity: how well a candidate's genre matches the user's viewing patterns for the current time-of-day bucket.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="now">
    ///     Reference point for "now" (defaults to <see cref="DateTime.UtcNow"/>).
    ///     Exposed for deterministic unit testing.
    /// </param>
    /// <returns>An affinity score between 0 and 1.</returns>
    internal static double ComputeHourOfDayAffinity(BaseItem candidate, UserWatchProfile userProfile, DateTime? now = null)
        => ComputeTemporalAffinity(candidate.Genres, userProfile, now ?? DateTime.UtcNow, false, candidate.Id, candidate is Series ? candidate.Id : null);

    /// <summary>Computes hour affinity from the exact genres captured with one prediction.</summary>
    /// <param name="candidateGenres">Captured effective genres.</param>
    /// <param name="userProfile">The already scoped playback profile.</param>
    /// <param name="predictionTimeUtc">The prediction's single reference clock.</param>
    /// <param name="excludedItem">Optional target item to exclude.</param>
    /// <param name="excludedSeries">Optional parent series to exclude.</param>
    /// <returns>The same bounded affinity used by historical reconstruction.</returns>
    internal static double ComputeHourOfDayAffinity(IReadOnlyCollection<string>? candidateGenres, UserWatchProfile userProfile, DateTime predictionTimeUtc, Guid excludedItem = default, Guid? excludedSeries = null)
        => ComputeTemporalAffinity(candidateGenres, userProfile, predictionTimeUtc, false, excludedItem, excludedSeries);

    /// <summary>Reduces dated, strictly earlier playback in one UTC bucket without target/series leakage.</summary>
    /// <param name="candidateGenres">The exact captured genre source.</param>
    /// <param name="userProfile">The caller's scoped history.</param>
    /// <param name="predictionTimeUtc">The exclusive prediction-time boundary.</param>
    /// <param name="isDay">Selects a weekday rather than hour bucket.</param>
    /// <param name="excludedItem">Target item identity, if known.</param>
    /// <param name="excludedSeries">Target parent-series identity, if known.</param>
    /// <returns>Neutral with fewer than ten profile rows or three eligible bucket observations.</returns>
    internal static double ComputeTemporalAffinity(IEnumerable<string>? candidateGenres, UserWatchProfile userProfile, DateTime predictionTimeUtc, bool isDay, Guid excludedItem = default, Guid? excludedSeries = null)
    {
        ArgumentNullException.ThrowIfNull(userProfile);
        var candidateGenreSet = GenreNormalizer.Set(candidateGenres);
        if (candidateGenreSet.Count == 0 || userProfile.WatchedItems.Count < 10)
        {
            return 0.5;
        }

        var reference = DateTimeNormalization.ToUtc(predictionTimeUtc);
        var matchCount = 0;
        var totalInBucket = 0;

        foreach (var genres in userProfile.WatchedItems.Where(row => EligibleForTemporalBucket(row, reference, isDay, excludedItem, excludedSeries)).Select(row => row.Genres))
        {
            totalInBucket++;
            if (genres is not null && GenreNormalizer.Overlaps(candidateGenreSet, genres))
            {
                matchCount++;
            }
        }

        if (totalInBucket < 3)
        {
            return 0.5;
        }

        return Math.Clamp((double)matchCount / totalInBucket, 0.0, 1.0);
    }

    private static bool EligibleForTemporalBucket(WatchedItemInfo row, DateTime reference, bool isDay, Guid excludedItem, Guid? excludedSeries)
    {
        if (!row.LastPlayedDate.HasValue || !row.HasPlaybackActivity() || (excludedItem != Guid.Empty && row.ItemId == excludedItem)
            || (excludedSeries is { } series && series != Guid.Empty && (row.ItemId == series || row.SeriesId == series)))
        {
            return false;
        }

        var played = DateTimeNormalization.ToUtc(row.LastPlayedDate.Value);
        return played < reference && (isDay ? played.DayOfWeek == reference.DayOfWeek : GetTimeBucket(played.Hour) == GetTimeBucket(reference.Hour));
    }

    /// <summary>Maps an hour to night, morning, afternoon or evening.</summary>
    /// <param name="hour">The hour of day (0-23).</param>
    /// <returns>The matching bucket index.</returns>
    internal static int GetTimeBucket(int hour) => hour switch
    {
        < 6 => 0,
        < 12 => 1,
        < 18 => 2,
        _ => 3
    };

    /// <summary>
    ///     Resolves IsWeekend consistently across training and inference.
    /// </summary>
    /// <param name="userProfile">The user profile.</param>
    /// <param name="referenceOverride">The prediction-time context; defaults to the current UTC time.</param>
    /// <returns>True for Saturday or Sunday, false otherwise.</returns>
    internal static bool ResolveIsWeekend(UserWatchProfile userProfile, DateTime? referenceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(userProfile);

        var reference = DateTimeNormalization.ToUtc(referenceOverride ?? DateTime.UtcNow);
        return reference.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }
}
