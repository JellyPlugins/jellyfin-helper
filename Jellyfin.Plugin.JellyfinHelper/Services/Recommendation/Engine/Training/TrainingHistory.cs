using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>Reconstructs conservative history inputs without borrowing the target's outcome or later activity.</summary>
internal static class TrainingHistory
{
    /// <summary>Builds a detached profile from dated activity strictly before the prediction time.</summary>
    /// <param name="source">The current watch profile.</param>
    /// <param name="beforeUtc">The prediction-time cutoff.</param>
    /// <param name="excludedIds">The target and its episode ids, when known.</param>
    /// <returns>A historical profile; undated activity cannot establish past preferences.</returns>
    internal static UserWatchProfile Before(UserWatchProfile source, DateTime beforeUtc, IReadOnlySet<Guid>? excludedIds = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var cutoff = DateTimeNormalization.ToUtc(beforeUtc);
        var rows = source.WatchedItems.Where(w => w.LastPlayedDate.HasValue
            && DateTimeNormalization.ToUtc(w.LastPlayedDate.Value) < cutoff
            && w.HasMeaningfulInteraction()
            && (excludedIds is null || !excludedIds.Contains(w.ItemId))).ToList();
        var profile = new UserWatchProfile
        {
            UserId = source.UserId,
            UserName = source.UserName,
            MaxParentalRating = source.MaxParentalRating,
            WatchedItems = new Collection<WatchedItemInfo>(rows),
            LastActivityDate = rows.Select(w => w.LastPlayedDate).Max()
        };
        foreach (var row in rows)
        {
            if (row.ItemType == "Series" && row.IsFavorite)
            {
                profile.FavoriteSeriesIds.Add(row.ItemId);
            }

            foreach (var person in row.PeopleNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                profile.PeopleProfile[person] = profile.PeopleProfile.GetValueOrDefault(person) + 1;
            }
        }

        // Current aggregate genre/language counters have no event timestamps and cannot be copied into the past.
        return profile;
    }
}
