using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Collects watch history and user profiles from Jellyfin's user data manager.
/// </summary>
public sealed class WatchHistoryService : IWatchHistoryService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<WatchHistoryService> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WatchHistoryService" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public WatchHistoryService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IPluginLogService pluginLog,
        ILogger<WatchHistoryService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public UserWatchProfile? GetUserWatchProfile(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        return BuildProfile(user);
    }

    /// <inheritdoc />
    public Collection<UserWatchProfile> GetAllUserWatchProfiles()
    {
        var users = _userManager.Users.ToList();
        _pluginLog.LogInfo(
            "WatchHistory",
            $"Starting watch profile collection for {users.Count} users...",
            _logger);

        // Load library items once for all users (performance: avoids redundant DB queries)
        var allItems = LoadAllVideoItems();
        var allSeries = LoadAllSeriesItems();

        var profiles = new Collection<UserWatchProfile>();
        foreach (var user in users)
        {
            try
            {
                profiles.Add(BuildProfile(user, allItems, allSeries));
            }
            catch (Exception ex)
            {
                _pluginLog.LogWarning(
                    "WatchHistory",
                    $"Failed to build profile for user '{user.Username}'",
                    ex,
                    _logger);
            }
        }

        _pluginLog.LogInfo(
            "WatchHistory",
            $"Finished watch profile collection: {profiles.Count} profiles built.",
            _logger);

        return profiles;
    }

    /// <summary>
    ///     Loads all video items from the library (movies, episodes, etc.).
    ///     Called once and shared across all user profile builds.
    /// </summary>
    /// <returns>A list of all non-folder video items.</returns>
    internal IReadOnlyList<BaseItem> LoadAllVideoItems()
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsFolder = false
        });
    }

    /// <summary>
    ///     Loads all series items from the library.
    ///     Called once and shared across all user profile builds for series-level favorite detection.
    /// </summary>
    /// <returns>A list of all series items.</returns>
    internal IReadOnlyList<BaseItem> LoadAllSeriesItems()
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            IsFolder = true
        });
    }

    /// <summary>
    ///     Builds a complete watch profile for a single user using pre-loaded library items.
    /// </summary>
    /// <param name="user">The Jellyfin user entity.</param>
    /// <param name="allItems">Pre-loaded video items from the library (null to query on demand).</param>
    /// <param name="allSeries">Pre-loaded series items for favorite detection (null to query on demand).</param>
    /// <returns>A populated watch profile for the user.</returns>
    internal UserWatchProfile BuildProfile(
        Jellyfin.Database.Implementations.Entities.User user,
        IReadOnlyList<BaseItem>? allItems = null,
        IReadOnlyList<BaseItem>? allSeries = null)
    {
        var profile = new UserWatchProfile
        {
            UserId = user.Id,
            UserName = user.Username,
            MaxParentalRating = user.MaxParentalRatingScore
        };

        // Use pre-loaded items or query on demand (single-user path)
        allItems ??= LoadAllVideoItems();

        var ratingSum = 0.0;
        var ratingCount = 0;
        var watchedSeriesIds = new HashSet<Guid>();

        foreach (var item in allItems)
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData is null)
            {
                continue;
            }

            // Only include items with some interaction (played, partially watched, or favorited)
            if (!userData.Played && userData.PlaybackPositionTicks <= 0 && !userData.IsFavorite)
            {
                continue;
            }

            var watchedItem = new WatchedItemInfo
            {
                ItemId = item.Id,
                Name = item.Name ?? string.Empty,
                ItemType = item.GetType().Name,
                PlayCount = userData.PlayCount,
                LastPlayedDate = userData.LastPlayedDate,
                PlaybackPositionTicks = userData.PlaybackPositionTicks,
                RuntimeTicks = item.RunTimeTicks ?? 0,
                Played = userData.Played,
                IsFavorite = userData.IsFavorite,
                UserRating = userData.Rating,
                CommunityRating = item.CommunityRating,
                Genres = item.Genres ?? [],
                Year = item.ProductionYear,
                SeriesId = item is Episode ep ? (ep.SeriesId != Guid.Empty ? ep.SeriesId : null) : null,
                PrimaryImageTag = null
            };

            profile.WatchedItems.Add(watchedItem);

            // Accumulate statistics
            if (userData.Played)
            {
                if (item is Movie)
                {
                    profile.WatchedMovieCount++;
                }
                else if (item is Episode episode)
                {
                    profile.WatchedEpisodeCount++;
                    if (episode.SeriesId != Guid.Empty)
                    {
                        watchedSeriesIds.Add(episode.SeriesId);
                    }
                }

                // Add runtime to total watch time
                if (item.RunTimeTicks.HasValue)
                {
                    profile.TotalWatchTimeTicks += item.RunTimeTicks.Value;
                }
            }

            // Track genre distribution
            if (item.Genres is not null)
            {
                foreach (var genre in item.Genres)
                {
                    if (!string.IsNullOrWhiteSpace(genre))
                    {
                        profile.GenreDistribution.TryGetValue(genre, out var count);
                        profile.GenreDistribution[genre] = count + 1;
                    }
                }
            }

            // Track favorites
            if (userData.IsFavorite)
            {
                profile.FavoriteCount++;
            }

            // Track community rating for average
            if (item.CommunityRating.HasValue)
            {
                ratingSum += item.CommunityRating.Value;
                ratingCount++;
            }

            // Track last activity
            if (userData.LastPlayedDate.HasValue &&
                (!profile.LastActivityDate.HasValue || userData.LastPlayedDate > profile.LastActivityDate))
            {
                profile.LastActivityDate = userData.LastPlayedDate;
            }
        }

        // Check series-level favorites: users can favorite an entire series in Jellyfin.
        // This UserData lives on the Series item itself, not on individual episodes.
        allSeries ??= LoadAllSeriesItems();

        foreach (var series in allSeries)
        {
            var seriesUserData = _userDataManager.GetUserData(user, series);
            if (seriesUserData is not null && seriesUserData.IsFavorite)
            {
                profile.FavoriteSeriesIds.Add(series.Id);
            }
        }

        profile.WatchedSeriesCount = watchedSeriesIds.Count;
        profile.AverageCommunityRating = ratingCount > 0 ? Math.Round(ratingSum / ratingCount, 1) : 0;

        _pluginLog.LogDebug(
            "WatchHistory",
            $"Profile for '{user.Username}': {profile.WatchedMovieCount} movies, " +
            $"{profile.WatchedEpisodeCount} episodes, {profile.WatchedSeriesCount} series, " +
            $"{profile.FavoriteCount} favorites",
            _logger);

        return profile;
    }
}