using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;

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
    {
        if (candidate.Genres is not { Length: > 0 } || userProfile.WatchedItems.Count < 10)
        {
            return 0.5;
        }

        var today = (now ?? DateTime.UtcNow).DayOfWeek;
        var matchCount = 0;
        var totalToday = 0;
        var candidateGenreSet = new HashSet<string>(candidate.Genres, StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.LastPlayedDate.HasValue)
            {
                continue;
            }

            // Include all items with real playback interaction (not just Played=true). This aligns with the broader interaction predicate used by TrainingService and Engine (Played || PlayCount > 0 || PlaybackPositionTicks > 0).
            if (!w.Played && w.PlayCount <= 0 && w.PlaybackPositionTicks <= 0)
            {
                continue;
            }

            if (w.LastPlayedDate.Value.DayOfWeek != today)
            {
                continue;
            }

            totalToday++;
            if (w.Genres is not null && candidateGenreSet.Overlaps(w.Genres))
            {
                matchCount++;
            }
        }

        if (totalToday < 3)
        {
            return 0.5;
        }

        return Math.Clamp((double)matchCount / totalToday, 0.0, 1.0);
    }

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
    {
        if (candidate.Genres is not { Length: > 0 } || userProfile.WatchedItems.Count < 10)
        {
            return 0.5;
        }

        var currentHour = (now ?? DateTime.UtcNow).Hour;
        var currentBucket = GetTimeBucket(currentHour);
        var candidateGenreSet = new HashSet<string>(candidate.Genres, StringComparer.OrdinalIgnoreCase);

        var matchCount = 0;
        var totalInBucket = 0;

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.LastPlayedDate.HasValue)
            {
                continue;
            }

            // Include all items with real playback interaction (not just Played=true). This aligns with the broader interaction predicate used by TrainingService and Engine (Played || PlayCount > 0 || PlaybackPositionTicks > 0).
            if (!w.Played && w.PlayCount <= 0 && w.PlaybackPositionTicks <= 0)
            {
                continue;
            }

            if (GetTimeBucket(w.LastPlayedDate.Value.Hour) != currentBucket)
            {
                continue;
            }

            totalInBucket++;
            if (w.Genres is not null && candidateGenreSet.Overlaps(w.Genres))
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

    /// <summary>
    ///     Maps an hour (0-23) to a time-of-day bucket for temporal affinity computation. Buckets: 0 = night (0-5), 1 = morning (6-11), 2 = afternoon (12-17), 3 = evening (18-23).
    /// </summary>
    /// <param name="hour">The hour of day (0-23).</param>
    /// <returns>A bucket index (0-3).</returns>
    internal static int GetTimeBucket(int hour) => hour switch
    {
        < 6 => 0,
        < 12 => 1,
        < 18 => 2,
        _ => 3
    };

    /// <summary>
    ///     Resolves the IsWeekend flag consistently across all feature-vector construction paths (live scoring, Phase 1 recommendation-history examples, Phase 2 organic watches, Phase 3 random negatives, and aggregated series examples).
    /// </summary>
    /// <param name="userProfile">The user's watch profile. Must not be null.</param>
    /// <param name="referenceOverride">
    ///     Optional fallback timestamp used when the profile has no <see cref="UserWatchProfile.LastActivityDate"/>.
    /// </param>
    /// <returns>
    ///     <c>true</c> if the resolved reference falls on a Saturday or Sunday; otherwise <c>false</c>.
    ///     When neither the profile anchor nor an override is available, returns a deterministic <c>false</c>
    ///     so the neural net never learns a signal tied to when the training task happened to run.
    /// </returns>
    internal static bool ResolveIsWeekend(UserWatchProfile userProfile, DateTime? referenceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(userProfile);

        // No anchor + no override = no signal. Emit false rather than UtcNow so train/serve rows stay identical.
        var reference = userProfile.LastActivityDate ?? referenceOverride;
        if (!reference.HasValue)
        {
            return false;
        }

        return reference.Value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }
}
