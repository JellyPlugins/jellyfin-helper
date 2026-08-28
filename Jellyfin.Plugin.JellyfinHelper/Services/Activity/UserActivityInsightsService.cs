using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Scans all library items and all users to produce per-item activity summaries with per-user breakdowns (play count, last watched, completion %, favorites, rating).
/// </summary>
public class UserActivityInsightsService : IUserActivityInsightsService
{
    private const string LogSource = "UserActivity";

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
        var users = _userManager.GetUsers()?.ToList() ?? new List<Jellyfin.Database.Implementations.Entities.User>();
        _pluginLog.LogInfo(
            LogSource,
            $"Building activity report for {users.Count} users",
            _logger);

        // Query all playable video items once
        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsFolder = false
        });

        _pluginLog.LogDebug(
            LogSource,
            $"Scanning {allItems.Count} items across {users.Count} users",
            _logger);

        // Pre-fetch user data one batch per user (Jellyfin 12+ API) to avoid N×M DB roundtrips.
        // Falls back to per-item lookup if a user's batch call fails.
        var userDataByUser = BuildUserDataLookup(users, allItems);

        // Build per-item summaries with all user interactions
        var summaries = new Dictionary<Guid, UserActivitySummary>();
        long totalPlayCount = 0;

        foreach (var item in allItems)
        {
            var aggregate = new ItemActivityAggregate();

            foreach (var user in users)
            {
                AccumulateUserActivity(item, user, userDataByUser, aggregate);
            }

            // Only include items that have at least one user interaction
            if (aggregate.Activities.Count == 0)
            {
                continue;
            }

            var summary = BuildItemSummary(item, aggregate);

            summaries[item.Id] = summary;
            totalPlayCount += aggregate.TotalPlays;
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
            LogSource,
            $"Activity report complete: {result.TotalItemsWithActivity} items with activity, " +
            $"{result.TotalPlayCount} total plays across {result.TotalUsersAnalyzed} users",
            _logger);

        return result;
    }

    /// <summary>
    ///     Reads a single user's data for an item (from the pre-fetched batch, falling back to a per-item lookup) and folds any interaction into the running .
    /// </summary>
    /// <param name="item">The library item being scanned.</param>
    /// <param name="user">The user whose data is being read.</param>
    /// <param name="userDataByUser">The pre-fetched per-user data lookup.</param>
    /// <param name="aggregate">The running aggregate to fold this user's activity into.</param>
    private void AccumulateUserActivity(
        BaseItem item,
        Jellyfin.Database.Implementations.Entities.User user,
        Dictionary<Guid, IReadOnlyDictionary<Guid, UserItemData>?> userDataByUser,
        ItemActivityAggregate aggregate)
    {
        try
        {
            // O(1) lookup from the pre-fetched batch; falls back to per-item
            // GetUserData if the batch load failed (null inner dict).
            UserItemData? userData;
            if (userDataByUser.TryGetValue(user.Id, out var userLookup) && userLookup is not null)
            {
                userLookup.TryGetValue(item.Id, out userData);
            }
            else
            {
                userData = _userDataManager.GetUserData(user, item);
            }

            if (userData is null)
            {
                return;
            }

            var hasPlaybackActivity = userData.Played
                || userData.PlaybackPositionTicks > 0
                || userData.PlayCount > 0;

            // Only include if there's any interaction (playback or favorite)
            if (!hasPlaybackActivity && !userData.IsFavorite)
            {
                return;
            }

            var completion = CalculateCompletion(
                userData.PlaybackPositionTicks,
                item.RunTimeTicks ?? 0,
                userData.Played);

            // Normalize LastPlayedDate to UTC; IUserDataManager does not guarantee
            // DateTimeKind.Utc, which can cause mixed-kind timestamps in cached JSON.
            var lastPlayedUtc = DateTimeNormalization.ToUtc(userData.LastPlayedDate);

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

            aggregate.Activities.Add(activity);

            if (hasPlaybackActivity)
            {
                aggregate.TotalPlays += userData.PlayCount;
                aggregate.CompletionSum += completion;
                aggregate.ViewerCount++;
            }

            if (userData.IsFavorite)
            {
                aggregate.FavoriteCount++;
            }

            if (hasPlaybackActivity && lastPlayedUtc.HasValue &&
                (!aggregate.MostRecent.HasValue || lastPlayedUtc > aggregate.MostRecent))
            {
                aggregate.MostRecent = lastPlayedUtc;
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Failed to read user data for user '{user.Username}' on item '{item.Name}'",
                ex,
                _logger);
        }
    }

    /// <summary>
    ///     Builds the per-item <see cref="UserActivitySummary" /> from the item metadata and its
    ///     completed activity aggregate.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="aggregate">The completed activity aggregate for the item.</param>
    /// <returns>The activity summary for the item.</returns>
    private static UserActivitySummary BuildItemSummary(BaseItem item, ItemActivityAggregate aggregate)
    {
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

        return new UserActivitySummary
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
            TotalPlayCount = aggregate.TotalPlays,
            UniqueViewers = aggregate.ViewerCount,
            MostRecentWatch = aggregate.MostRecent,
            AverageCompletionPercent = aggregate.ViewerCount > 0
                ? Math.Round(aggregate.CompletionSum / aggregate.ViewerCount, 1)
                : 0,
            FavoriteCount = aggregate.FavoriteCount,
            UserActivities = new Collection<UserItemActivity>(aggregate.Activities)
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

    /// <summary>
    ///     Runs one batch call per user to pre-load their user data for every library item.
    /// </summary>
    /// <param name="users">The users to pre-load data for.</param>
    /// <param name="allItems">The library items to load user data against.</param>
    /// <returns>
    ///     A dictionary keyed by user ID. A <c>null</c> inner value signals batch failure
    ///     for that user - the caller then falls back to per-item <c>GetUserData</c>.
    /// </returns>
    private Dictionary<Guid, IReadOnlyDictionary<Guid, UserItemData>?> BuildUserDataLookup(
        List<Jellyfin.Database.Implementations.Entities.User> users,
        IReadOnlyList<BaseItem> allItems)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<Guid, UserItemData>?>(users.Count);
        foreach (var user in users)
        {
            // Batch call is per-user (not per-library) because GetUserDataBatch is keyed on a
            // single user. A failure for one user must not block others, hence the per-user marker.
            var perUser = user;
            var lookup = BatchFallbackHelper.TryRunBatch<IReadOnlyDictionary<Guid, UserItemData>?>(
                batchCall: () =>
                {
                    var batch = _userDataManager.GetUserDataBatch(allItems, perUser);
                    if (batch is null)
                    {
                        return null;
                    }

                    // Accept any dictionary shape Jellyfin returns; IReadOnlyDictionary keeps the batch immutable to the caller and avoids locking to a concrete return type across Jellyfin patch versions.
                    return batch as IReadOnlyDictionary<Guid, UserItemData>
                           ?? new Dictionary<Guid, UserItemData>(batch);
                },
                fallbackValue: null,
                onFailure: ex => _pluginLog.LogWarning(
                    LogSource,
                    $"Batch user-data load failed for user '{perUser.Username}'; falling back to per-item lookup for this user.",
                    ex,
                    _logger));

            result[user.Id] = lookup;
        }

        return result;
    }

    /// <summary>
    ///     Mutable per-item accumulator used while folding each user's interaction into the running
    ///     totals for a single library item.
    /// </summary>
    private sealed class ItemActivityAggregate
    {
        /// <summary>
        ///     Gets the per-user activities recorded for the item.
        /// </summary>
        public List<UserItemActivity> Activities { get; } = new();

        /// <summary>
        ///     Gets or sets the total play count across all users.
        /// </summary>
        public long TotalPlays { get; set; }

        /// <summary>
        ///     Gets or sets the sum of completion percentages across all viewers.
        /// </summary>
        public double CompletionSum { get; set; }

        /// <summary>
        ///     Gets or sets the number of unique viewers with playback activity.
        /// </summary>
        public int ViewerCount { get; set; }

        /// <summary>
        ///     Gets or sets the number of users who favorited the item.
        /// </summary>
        public int FavoriteCount { get; set; }

        /// <summary>
        ///     Gets or sets the most recent watch timestamp across all users.
        /// </summary>
        public DateTime? MostRecent { get; set; }
    }
}
