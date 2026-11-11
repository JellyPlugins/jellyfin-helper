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
            && w.HasPlaybackActivity()
            && (excludedIds is null || !excludedIds.Contains(w.ItemId)))
            .Select(w => w.CopyPlaybackHistory()).ToList();
        return BuildProfile(source, rows);
    }

    /// <summary>Filters already detached playback rows owned exclusively by a training build, without cloning every prefix again.</summary>
    /// <param name="source">The build-owned sanitized playback snapshot.</param>
    /// <param name="cutoff">The normalized exclusive prediction cutoff.</param>
    /// <param name="excludedIds">Target memberships to exclude from this profile only.</param>
    /// <returns>A profile reusing immutable build-owned rows.</returns>
    internal static UserWatchProfile BeforePlaybackSnapshot(UserWatchProfile source, DateTime cutoff, IReadOnlySet<Guid>? excludedIds = null)
    {
        var rows = source.WatchedItems.Where(row => row.LastPlayedDate < cutoff && (excludedIds is null || !excludedIds.Contains(row.ItemId))).ToList();
        return BuildProfile(source, rows);
    }

    private static UserWatchProfile BuildProfile(UserWatchProfile source, List<WatchedItemInfo> rows)
    {
        var profile = new UserWatchProfile
        {
            UserId = source.UserId,
            UserName = source.UserName,
            MaxParentalRating = source.MaxParentalRating,
            WatchedItems = new Collection<WatchedItemInfo>(rows),
            LastActivityDate = rows.Select(w => w.LastPlayedDate).Max()
        };
        foreach (var row in rows.Where(row => row.PeopleNames.Count > 0))
        {
            foreach (var person in row.PeopleNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                profile.PeopleProfile[person] = profile.PeopleProfile.GetValueOrDefault(person) + 1;
            }
        }

        // Neither aggregate counters nor current favorite/rating edits have event timestamps.
        // Only recorded prediction snapshots can preserve those preferences faithfully.
        return profile;
    }
}
