using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Computes temporal context features: day-of-week affinity,
///     hour-of-day affinity, and time-bucket classification.
///     <para>
///         <b>UTC caveat:</b> All timestamps (both <c>DateTime.UtcNow</c> and
///         <c>LastPlayedDate</c> from Jellyfin) are compared in UTC. For users in
///         non-UTC time zones this can skew day-of-week and time-of-day bucket
///         assignments (e.g., a Saturday evening in PST maps to Sunday UTC).
///         Per-user timezone is not available from Jellyfin's API, so UTC is
///         used consistently to avoid mixed-kind DateTime issues.
///     </para>
/// </summary>
internal static class TemporalFeatures
{
    /// <summary>
    ///     Computes day-of-week affinity: how well a candidate's genre matches
    ///     the user's viewing patterns for the current day of week.
    ///     Returns 0.5 (neutral) if insufficient data.
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
            if (!w.Played || !w.LastPlayedDate.HasValue)
            {
                continue;
            }

            if (w.LastPlayedDate.Value.DayOfWeek != today)
            {
                continue;
            }

            totalToday++;
            if (w.Genres is not null && w.Genres.Any(g => candidateGenreSet.Contains(g)))
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
    ///     Computes hour-of-day affinity: how well a candidate's genre matches
    ///     the user's viewing patterns for the current time-of-day bucket.
    ///     Uses 4 buckets: night (0–5), morning (6–11), afternoon (12–17), evening (18–23).
    ///     Returns 0.5 (neutral) if insufficient data.
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
            if (!w.Played || !w.LastPlayedDate.HasValue)
            {
                continue;
            }

            if (GetTimeBucket(w.LastPlayedDate.Value.Hour) != currentBucket)
            {
                continue;
            }

            totalInBucket++;
            if (w.Genres is not null && w.Genres.Any(g => candidateGenreSet.Contains(g)))
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
    ///     Maps an hour (0–23) to a time-of-day bucket for temporal affinity computation.
    ///     Buckets: 0 = night (0–5), 1 = morning (6–11), 2 = afternoon (12–17), 3 = evening (18–23).
    /// </summary>
    /// <param name="hour">The hour of day (0–23).</param>
    /// <returns>A bucket index (0–3).</returns>
    internal static int GetTimeBucket(int hour) => hour switch
    {
        < 6 => 0,
        < 12 => 1,
        < 18 => 2,
        _ => 3
    };
}