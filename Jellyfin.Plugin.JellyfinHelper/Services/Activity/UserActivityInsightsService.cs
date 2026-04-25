using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Scans all library items and all users to produce per-item activity summaries
///     with per-user breakdowns (play count, last watched, completion %, favorites, rating).
/// </summary>
public class UserActivityInsightsService : IUserActivityInsightsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<UserActivityInsightsService> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UserActivityInsightsService" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public UserActivityInsightsService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IPluginLogService pluginLog,
        ILogger<UserActivityInsightsService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public UserActivityResult BuildActivityReport()
    {
        var users = _userManager.Users.ToList();
        _pluginLog.LogInfo(
            "UserActivity",
            $"Building activity report for {users.Count} users",
            _logger);

        // Query all playable video items once
        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsFolder = false
        });

        _pluginLog.LogDebug(
            "UserActivity",
            $"Scanning {allItems.Count} items across {users.Count} users",
            _logger);

        // Build per-item summaries with all user interactions
        var summaries = new Dictionary<Guid, UserActivitySummary>();
        long totalPlayCount = 0;

        foreach (var item in allItems)
        {
            var itemActivities = new List<UserItemActivity>();
            var itemTotalPlays = 0;
            var completionSum = 0.0;
            var viewerCount = 0;
            var favoriteCount = 0;
            DateTime? mostRecent = null;

            foreach (var user in users)
            {
                try
                {
                    var userData = _userDataManager.GetUserData(user, item);
                    if (userData is null)
                    {
                        continue;
                    }

                    var hasPlaybackActivity = userData.Played
                        || userData.PlaybackPositionTicks > 0
                        || userData.PlayCount > 0;

                    // Only include if there's any interaction (playback or favorite)
                    if (!hasPlaybackActivity && !userData.IsFavorite)
                    {
                        continue;
                    }

                    var completion = CalculateCompletion(
                        userData.PlaybackPositionTicks,
                        item.RunTimeTicks ?? 0,
                        userData.Played);

                    // Normalize LastPlayedDate to UTC before assignment/comparison.
                    // Jellyfin's IUserDataManager does not guarantee DateTimeKind.Utc,
                    // which can cause mixed-kind timestamps in cached JSON.
                    var lastPlayedUtc = NormalizeToUtc(userData.LastPlayedDate);

                    var activity = new UserItemActivity
                    {
                        UserId = user.Id,
                        UserName = user.Username,
                        PlayCount = userData.PlayCount,
                        LastPlayedDate = lastPlayedUtc,
                        PlaybackPositionTicks = userData.PlaybackPositionTicks,
                        CompletionPercent = completion,
                        Played = userData.Played,
                        IsFavorite = userData.IsFavorite,
                        UserRating = userData.Rating
                    };

                    itemActivities.Add(activity);

                    if (hasPlaybackActivity)
                    {
                        itemTotalPlays += userData.PlayCount;
                        completionSum += completion;
                        viewerCount++;
                    }

                    if (userData.IsFavorite)
                    {
                        favoriteCount++;
                    }

                    if (hasPlaybackActivity && lastPlayedUtc.HasValue &&
                        (!mostRecent.HasValue || lastPlayedUtc > mostRecent))
                    {
                        mostRecent = lastPlayedUtc;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    _pluginLog.LogWarning(
                        "UserActivity",
                        $"Failed to read user data for user '{user.Username}' on item '{item.Name}'",
                        ex,
                        _logger);
                }
            }

            // Only include items that have at least one user interaction
            if (itemActivities.Count == 0)
            {
                continue;
            }

            string? seriesName = null;
            string? episodeLabel = null;

            if (item is Episode episode)
            {
                seriesName = episode.SeriesName;
                var season = episode.ParentIndexNumber;
                var epNum = episode.IndexNumber;
                if (season.HasValue && epNum.HasValue)
                {
                    episodeLabel = string.Format(
                        CultureInfo.InvariantCulture,
                        "S{0:D2}E{1:D2}",
                        season.Value,
                        epNum.Value);
                }
            }

            var summary = new UserActivitySummary
            {
                ItemId = item.Id,
                ItemName = item.Name ?? string.Empty,
                ItemType = item.GetType().Name,
                SeriesName = seriesName,
                EpisodeLabel = episodeLabel,
                Year = item.ProductionYear,
                Genres = item.Genres ?? [],
                CommunityRating = item.CommunityRating,
                RuntimeTicks = item.RunTimeTicks ?? 0,
                TotalPlayCount = itemTotalPlays,
                UniqueViewers = viewerCount,
                MostRecentWatch = mostRecent,
                AverageCompletionPercent = viewerCount > 0
                    ? Math.Round(completionSum / viewerCount, 1)
                    : 0,
                FavoriteCount = favoriteCount,
                UserActivities = new Collection<UserItemActivity>(itemActivities)
            };

            summaries[item.Id] = summary;
            totalPlayCount += itemTotalPlays;
        }

        // Sort by total play count descending
        var sortedItems = summaries.Values
            .OrderByDescending(s => s.TotalPlayCount)
            .ThenByDescending(s => s.MostRecentWatch)
            .ToList();

        var result = new UserActivityResult
        {
            GeneratedAt = DateTime.UtcNow,
            TotalItemsWithActivity = sortedItems.Count,
            TotalUsersAnalyzed = users.Count,
            TotalPlayCount = totalPlayCount,
            Items = new Collection<UserActivitySummary>(sortedItems)
        };

        _pluginLog.LogInfo(
            "UserActivity",
            $"Activity report complete: {result.TotalItemsWithActivity} items with activity, " +
            $"{result.TotalPlayCount} total plays across {result.TotalUsersAnalyzed} users",
            _logger);

        return result;
    }

    /// <summary>
    ///     Normalizes a nullable <see cref="DateTime"/> to UTC.
    ///     Handles <see cref="DateTimeKind.Utc"/>, <see cref="DateTimeKind.Local"/>,
    ///     and <see cref="DateTimeKind.Unspecified"/> (treated as UTC).
    /// </summary>
    /// <param name="value">The nullable DateTime to normalize.</param>
    /// <returns>The UTC-normalized DateTime, or null if input is null.</returns>
    private static DateTime? NormalizeToUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    /// <summary>
    ///     Calculates the completion percentage for a media item.
    /// </summary>
    /// <param name="positionTicks">Current playback position in ticks.</param>
    /// <param name="runtimeTicks">Total runtime in ticks.</param>
    /// <param name="played">Whether the item is marked as played.</param>
    /// <returns>Completion percentage between 0 and 100.</returns>
    internal static double CalculateCompletion(long positionTicks, long runtimeTicks, bool played)
    {
        if (played)
        {
            return 100.0;
        }

        if (runtimeTicks <= 0 || positionTicks <= 0)
        {
            return 0.0;
        }

        var percent = (double)positionTicks / runtimeTicks * 100.0;
        return Math.Min(Math.Round(percent, 1), 100.0);
    }
}