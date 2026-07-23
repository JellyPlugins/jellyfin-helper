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
        var users = _userManager.GetUsers().ToList();
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

        // Pre-fetch user data in one batch per user (Jellyfin 12+ API) to avoid N×M database
        // roundtrips. Falls back to per-item lookup for a user if the batch call fails, so
        // that the report never regresses to worse behavior than the pre-batch implementation.
        var userDataByUser = BuildUserDataLookup(users, allItems);

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
                    // O(1) lookup from the pre-fetched batch dictionary; falls back to
                    // per-item GetUserData for this user if the batch load failed earlier
                    // (BuildUserDataLookup returns null for that user in the outer dict).
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
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
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
    ///     If a user's batch call fails, we record <c>null</c> in the outer dictionary as a
    ///     "please fall back to per-item lookup for this user" marker — the caller checks
    ///     for this on every row. Cancellation propagation is handled by
    ///     <see cref="BatchFallbackHelper"/> to keep parity with the other batch call sites.
    ///     <para>
    ///         Cancellation semantics: <see cref="BatchFallbackHelper.TryRunBatch{T}"/> lets
    ///         <see cref="OperationCanceledException"/> propagate out of the batch delegate.
    ///         When that happens mid-loop the entire method unwinds — earlier-user entries in
    ///         the local <c>result</c> dictionary are discarded together with the reference,
    ///         and the caller (<c>BuildActivityReport</c>) never receives a half-built report.
    ///         The invariant is: cancellation aborts the whole scan; a partial report is never
    ///         observable to any caller.
    ///     </para>
    ///     <para>
    ///         <b>Memory trade-off:</b> pre-fetching keeps one per-item <see cref="UserItemData"/>
    ///         dictionary in memory <i>per user</i> for the entire report duration, so peak memory
    ///         scales as <c>O(users × items)</c>. The previous per-pair path only ever held the
    ///         one row currently being scored. On a 5-user / 50k-item library this is roughly
    ///         5–50 MB of transient state, well below the memory ceiling of a Jellyfin
    ///         scheduled task, and is amortised by cutting the DB roundtrip count from
    ///         <c>users × items</c> down to <c>users</c>. Callers with hundreds of users on very
    ///         large libraries may want to revisit this if the report ever begins to page.
    ///     </para>
    /// </summary>
    /// <param name="users">The users to pre-load data for.</param>
    /// <param name="allItems">The library items to load user data against.</param>
    /// <returns>
    ///     A dictionary keyed by user ID. A <c>null</c> inner value signals batch failure
    ///     for that user — the caller then falls back to per-item <c>GetUserData</c>.
    /// </returns>
    private Dictionary<Guid, IReadOnlyDictionary<Guid, UserItemData>?> BuildUserDataLookup(
        List<Jellyfin.Database.Implementations.Entities.User> users,
        IReadOnlyList<BaseItem> allItems)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<Guid, UserItemData>?>(users.Count);
        foreach (var user in users)
        {
            // We deliberately do the batch call per-user (not per-library) because
            // GetUserDataBatch is keyed on a single user. A failure for one user must not
            // block the others, which is why the fallback marker is stored per-user.
            var perUser = user;
            var lookup = BatchFallbackHelper.TryRunBatch<IReadOnlyDictionary<Guid, UserItemData>?>(
                batchCall: () =>
                {
                    var batch = _userDataManager.GetUserDataBatch(allItems, perUser);
                    if (batch is null)
                    {
                        return null;
                    }

                    // Accept any dictionary shape Jellyfin returns (Dictionary<>, IDictionary<>,
                    // IReadOnlyDictionary<>). Returning IReadOnlyDictionary means the caller
                    // can't accidentally mutate the batch and we don't lock ourselves to a
                    // specific concrete return type across Jellyfin patch versions.
                    return batch as IReadOnlyDictionary<Guid, UserItemData>
                           ?? new Dictionary<Guid, UserItemData>(batch);
                },
                fallbackValue: null,
                onFailure: ex => _pluginLog.LogWarning(
                    "UserActivity",
                    $"Batch user-data load failed for user '{perUser.Username}'; falling back to per-item lookup for this user.",
                    ex,
                    _logger));

            result[user.Id] = lookup;
        }

        return result;
    }
}
