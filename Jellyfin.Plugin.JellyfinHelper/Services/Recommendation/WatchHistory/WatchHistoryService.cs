using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Collects watch history and user profiles from Jellyfin's user data manager.
/// </summary>
public sealed class WatchHistoryService : IWatchHistoryService
{
    /// <summary>The plugin-log category used for all watch-history log entries.</summary>
    private const string LogCategory = "WatchHistory";

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
        var users = _userManager.GetUsers().ToList();

        _pluginLog.LogInfo(
            LogCategory,
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
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogWarning(
                    LogCategory,
                    $"Failed to build profile for user '{user.Username}'",
                    ex,
                    _logger);
            }
        }

        _pluginLog.LogInfo(
            LogCategory,
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

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> GetSeriesEpisodeCounts()
    {
        var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            IsFolder = false
        });

        return CountPlayableEpisodesPerSeries(allEpisodes);
    }

    /// <summary>
    ///     Collapses a flat episode list into a per-series playable-episode count.
    /// </summary>
    /// <param name="episodes">The flat episode list.</param>
    /// <returns>A map of series ID to playable-episode count.</returns>
    private static Dictionary<Guid, int> CountPlayableEpisodesPerSeries(IReadOnlyList<BaseItem> episodes)
    {
        var seriesEpisodeCounts = new Dictionary<Guid, int>();
        foreach (var episode in episodes.OfType<Episode>())
        {
            if (string.IsNullOrEmpty(episode.Path) || episode.SeriesId == Guid.Empty)
            {
                continue;
            }

            seriesEpisodeCounts.TryGetValue(episode.SeriesId, out var count);
            seriesEpisodeCounts[episode.SeriesId] = count + 1;
        }

        return seriesEpisodeCounts;
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

        // Pre-fetch user data for every video item in one batch call (Jellyfin 12+ API).
        var itemUserDataLookup = TryLoadUserDataBatch(user, allItems);

        foreach (var item in allItems)
        {
            var userData = LookupUserData(itemUserDataLookup, item, user);
            if (userData is null)
            {
                continue;
            }

            // Only include items with some interaction (played, partially watched, or favorited)
            if (!userData.Played && userData.PlaybackPositionTicks <= 0 && !userData.IsFavorite)
            {
                continue;
            }

            AccumulateWatchedItem(profile, item, userData, watchedSeriesIds, ref ratingSum, ref ratingCount);
        }

        // Check series-level favorites: users can favorite an entire series in Jellyfin (the heart button on the series page).
        allSeries ??= LoadAllSeriesItems();

        // Second batch call for the (usually much smaller) series list.
        var seriesUserDataLookup = TryLoadUserDataBatch(user, allSeries);

        foreach (var series in allSeries)
        {
            var seriesUserData = LookupUserData(seriesUserDataLookup, series, user);
            if (seriesUserData is not null && seriesUserData.IsFavorite)
            {
                AccumulateFavoriteSeries(profile, series, seriesUserData);
            }
        }

        // Build audio + subtitle language profiles in a single pass over allItems. Previously these were two separate methods each iterating allItems and calling GetUserData per item, causing 2× the UserData lookups.
        BuildLanguageProfiles(profile, user, allItems, itemUserDataLookup);

        // Build people (actors/directors) profile from BaseItem.People metadata
        BuildPeopleProfile(profile, allItems, allSeries);

        profile.WatchedSeriesCount = watchedSeriesIds.Count;
        profile.AverageCommunityRating = ratingCount > 0 ? Math.Round(ratingSum / ratingCount, 1) : 0;

        _pluginLog.LogDebug(
            LogCategory,
            $"Profile for '{user.Username}': {profile.WatchedMovieCount} movies, " +
            $"{profile.WatchedEpisodeCount} episodes, {profile.WatchedSeriesCount} series, " +
            $"{profile.FavoriteCount} favorites",
            _logger);

        return profile;
    }

    /// <summary>
    ///     Builds a WatchedItemInfo for an interacted library item and folds its statistics (watch counts, runtime, genres, favorites, ratings, last activity) into the profile.
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="item">The library item the user interacted with.</param>
    /// <param name="userData">The user data for the item.</param>
    /// <param name="watchedSeriesIds">Accumulator of distinct watched series IDs.</param>
    /// <param name="ratingSum">Running community-rating sum (updated in place).</param>
    /// <param name="ratingCount">Running community-rating count (updated in place).</param>
    private void AccumulateWatchedItem(
        UserWatchProfile profile,
        BaseItem item,
        UserItemData userData,
        HashSet<Guid> watchedSeriesIds,
        ref double ratingSum,
        ref int ratingCount)
    {
        // Billed cast/directors for this item, cached as aligned name/weight lists so the training path can compute BillingWeightedPeople with the same shared helper the live path uses (closes the organic/aggregated-series train/serve gap that previously hardcoded 0.0).
        var itemPeople = ResolveWatchedItemPeople(item);

        var (billedNames, billedWeights) = SimilarityComputer.ExtractBilledPeople(itemPeople);

        Guid? seriesId = null;
        if (item is Episode ep)
        {
            seriesId = ep.SeriesId != Guid.Empty ? ep.SeriesId : null;
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
            SeriesId = seriesId,
            DateCreated = item.DateCreated,
            PrimaryImageTag = null,
            PeopleNames = billedNames,
            PeopleWeights = billedWeights,

            // Content-affinity source fields.
            TmdbCollectionName = ContentAffinityResolver.ResolveTmdbCollectionName(item),
            ProductionCountries = ContentAffinityResolver.ResolveProductionCountries(item),
            InheritedTags = ContentAffinityResolver.ResolveInheritedTags(item),
            SeriesStatus = ContentAffinityResolver.ResolveSeriesStatus(item),
            EndDate = ContentAffinityResolver.ResolveSeriesEndDate(item),
            WriterNames = ContentAffinityResolver.ExtractWriterNames(itemPeople)
        };

        profile.WatchedItems.Add(watchedItem);

        AccumulateWatchedItemStatistics(profile, item, userData, watchedSeriesIds, ref ratingSum, ref ratingCount);
    }

    /// <summary>
    ///     Resolves the billed cast/directors for a watched item, returning null for episodes (people are aggregated at series level, never per episode) and swallowing non-fatal lookup failures.
    /// </summary>
    /// <param name="item">The library item to resolve people for.</param>
    /// <returns>The item's people, or <c>null</c> when the item is an episode or lookup fails.</returns>
    private IReadOnlyList<PersonInfo>? ResolveWatchedItemPeople(BaseItem item)
    {
        if (item is Episode)
        {
            return null;
        }

        try
        {
            return _libraryManager.GetPeople(item);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return null;
        }
    }

    /// <summary>
    ///     Folds a watched item's statistics (played counts, runtime, genres, favorites, community rating, last activity) into the profile.
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="item">The library item the user interacted with.</param>
    /// <param name="userData">The user data for the item.</param>
    /// <param name="watchedSeriesIds">Accumulator of distinct watched series IDs.</param>
    /// <param name="ratingSum">Running community-rating sum (updated in place).</param>
    /// <param name="ratingCount">Running community-rating count (updated in place).</param>
    private static void AccumulateWatchedItemStatistics(
        UserWatchProfile profile,
        BaseItem item,
        UserItemData userData,
        HashSet<Guid> watchedSeriesIds,
        ref double ratingSum,
        ref int ratingCount)
    {
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
        AccumulateGenreDistribution(profile, item.Genres);

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

    /// <summary>
    ///     Records a favorited series into the profile: adds it to FavoriteSeriesIds, appends a synthetic WatchedItemInfo so its metadata influences preference vectors, and folds its genres into the distribution.
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="series">The favorited series item.</param>
    /// <param name="seriesUserData">The user data for the series (already known favorite).</param>
    private void AccumulateFavoriteSeries(
        UserWatchProfile profile,
        BaseItem series,
        UserItemData seriesUserData)
    {
        profile.FavoriteSeriesIds.Add(series.Id);
        profile.FavoriteCount++;

        // Create a synthetic WatchedItemInfo so that the series' genres, year, and community rating flow into PreferenceBuilder.BuildGenrePreferenceVector() with the FavoriteGenreBoostFactor (3×).
        IReadOnlyList<PersonInfo>? seriesPeople;
        try
        {
            seriesPeople = _libraryManager.GetPeople(series);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            seriesPeople = null;
        }

        var (favBilledNames, favBilledWeights) = SimilarityComputer.ExtractBilledPeople(seriesPeople);

        profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = series.Id,
            Name = series.Name ?? string.Empty,
            ItemType = nameof(Series),
            PlayCount = 0,
            LastPlayedDate = null,
            PlaybackPositionTicks = 0,
            RuntimeTicks = 0,
            Played = false,
            IsFavorite = true,
            UserRating = seriesUserData.Rating,
            CommunityRating = series.CommunityRating,
            Genres = series.Genres ?? [],
            Year = series.ProductionYear,
            SeriesId = null, // This IS the series itself, not an episode
            DateCreated = series.DateCreated,
            PrimaryImageTag = null,
            PeopleNames = favBilledNames,
            PeopleWeights = favBilledWeights,

            // Content-affinity source fields for the favorited series, using the same shared resolvers as the primary loop so a favorited series contributes franchise/country/ inherited-tag/writer/completability preference signal identically to a watched item.
            TmdbCollectionName = ContentAffinityResolver.ResolveTmdbCollectionName(series),
            ProductionCountries = ContentAffinityResolver.ResolveProductionCountries(series),
            InheritedTags = ContentAffinityResolver.ResolveInheritedTags(series),
            SeriesStatus = ContentAffinityResolver.ResolveSeriesStatus(series),
            EndDate = ContentAffinityResolver.ResolveSeriesEndDate(series),
            WriterNames = ContentAffinityResolver.ExtractWriterNames(seriesPeople)
        });

        // Also accumulate genre distribution for series-level favorites
        AccumulateGenreDistribution(profile, series.Genres);
    }

    /// <summary>
    ///     Folds an item's genres into the profile's genre distribution, skipping null/whitespace entries.
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="genres">The item's genres, or <c>null</c>.</param>
    private static void AccumulateGenreDistribution(UserWatchProfile profile, IReadOnlyList<string>? genres)
    {
        if (genres is not null)
        {
            foreach (var genre in genres.Where(genre => !string.IsNullOrWhiteSpace(genre)))
            {
                profile.GenreDistribution.TryGetValue(genre, out var count);
                profile.GenreDistribution[genre] = count + 1;
            }
        }
    }

    /// <summary>
    ///     Builds both the audio language and subtitle language preference profiles in a single pass over allItems.
    /// </summary>
    /// <param name="profile">The user profile to populate with language data.</param>
    /// <param name="user">The Jellyfin user entity.</param>
    /// <param name="allItems">Pre-loaded video items from the library.</param>
    /// <param name="itemUserDataLookup">
    ///     Pre-fetched batch dictionary from <see cref="TryLoadUserDataBatch"/>, or <c>null</c>
    ///     if the batch call failed. When <c>null</c>, this method falls back to per-item
    ///     <c>GetUserData</c> calls via <see cref="LookupUserData"/>.
    /// </param>
    private void BuildLanguageProfiles(
        UserWatchProfile profile,
        Jellyfin.Database.Implementations.Entities.User user,
        IReadOnlyList<BaseItem> allItems,
        IReadOnlyDictionary<Guid, UserItemData>? itemUserDataLookup)
    {
        foreach (var item in allItems)
        {
            var userData = LookupUserData(itemUserDataLookup, item, user);
            if (userData is null || (!userData.Played && userData.PlaybackPositionTicks <= 0))
            {
                continue;
            }

            // Get all media streams once for both audio and subtitle analysis
            List<MediaStream>? allStreams;
            try
            {
                allStreams = item.GetMediaStreams()?.ToList();
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a stop signal - propagate instead of skipping items silently.
                // Same contract as BatchFallbackHelper enforces for the batch call sites above.
                throw;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                // Graceful: skip items where stream lookup fails (e.g. corrupted metadata)
                continue;
            }

            if (allStreams is null || allStreams.Count == 0)
            {
                continue;
            }

            // Single-pass partition: split audio and subtitle streams in one iteration.
            var streamsByType = allStreams.ToLookup(s => s.Type);
            var audioStreams = streamsByType[MediaStreamType.Audio].ToList();
            var subtitleStreams = streamsByType[MediaStreamType.Subtitle].ToList();

            AccumulateAudioLanguage(profile, userData, audioStreams);

            AccumulateSubtitleLanguage(profile, userData, subtitleStreams);
        }
    }

    /// <summary>
    ///     Folds the audio language the user watched an item in into the profile's language profile, distinguishing "chosen" (alternatives existed) from "forced" (single option).
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="userData">The user data for the item.</param>
    /// <param name="audioStreams">The item's audio streams.</param>
    private static void AccumulateAudioLanguage(
        UserWatchProfile profile,
        UserItemData userData,
        List<MediaStream> audioStreams)
    {
        if (audioStreams.Count > 0)
        {
            string? usedAudioLanguage = null;

            if (userData.AudioStreamIndex.HasValue)
            {
                var chosenStream = audioStreams
                    .FirstOrDefault(s => s.Index == userData.AudioStreamIndex.Value);
                usedAudioLanguage = NormalizeLanguage(chosenStream?.Language);
            }

            if (string.IsNullOrEmpty(usedAudioLanguage))
            {
                usedAudioLanguage = NormalizeLanguage(audioStreams[0].Language);
            }

            if (!string.IsNullOrEmpty(usedAudioLanguage))
            {
                // availableAudioLanguages is always >= 1 here (usedAudioLanguage was resolved
                // from the same audioStreams list), so the old "> 0" guard was dead code.
                var availableAudioLanguages = audioStreams
                    .Select(s => NormalizeLanguage(s.Language))
                    .Where(l => !string.IsNullOrEmpty(l))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                if (!profile.LanguageProfile.TryGetValue(usedAudioLanguage, out var audioEntry))
                {
                    audioEntry = new LanguageProfileEntry();
                    profile.LanguageProfile[usedAudioLanguage] = audioEntry;
                }

                if (availableAudioLanguages > 1)
                {
                    audioEntry.ChosenCount++;
                }
                else
                {
                    audioEntry.ForcedCount++;
                }
            }
        }
    }

    /// <summary>
    ///     Folds the subtitle language the user selected for an item into the profile's subtitle language profile, distinguishing "chosen" from "forced".
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="userData">The user data for the item.</param>
    /// <param name="subtitleStreams">The item's subtitle streams.</param>
    private static void AccumulateSubtitleLanguage(
        UserWatchProfile profile,
        UserItemData userData,
        List<MediaStream> subtitleStreams)
    {
        if (!userData.SubtitleStreamIndex.HasValue || userData.SubtitleStreamIndex.Value < 0 || subtitleStreams.Count == 0)
        {
            return;
        }

        var chosenSubStream = subtitleStreams
            .FirstOrDefault(s => s.Index == userData.SubtitleStreamIndex.Value);
        var usedSubLanguage = NormalizeLanguage(chosenSubStream?.Language);

        if (string.IsNullOrEmpty(usedSubLanguage))
        {
            return;
        }

        var availableSubLanguages = subtitleStreams
            .Select(s => NormalizeLanguage(s.Language))
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (availableSubLanguages <= 0)
        {
            return;
        }

        if (!profile.SubtitleLanguageProfile.TryGetValue(usedSubLanguage, out var subEntry))
        {
            subEntry = new LanguageProfileEntry();
            profile.SubtitleLanguageProfile[usedSubLanguage] = subEntry;
        }

        if (availableSubLanguages > 1)
        {
            subEntry.ChosenCount++;
        }
        else
        {
            subEntry.ForcedCount++;
        }
    }

    /// <summary>
    ///     Builds the user's people (actors/directors) preference profile by analyzing BaseItem.People metadata for each watched or favorited item.
    /// </summary>
    /// <param name="profile">The user profile to populate with people data.</param>
    /// <param name="allItems">Pre-loaded video items from the library.</param>
    /// <param name="allSeries">Pre-loaded series items from the library.</param>
    private void BuildPeopleProfile(
        UserWatchProfile profile,
        IReadOnlyList<BaseItem> allItems,
        IReadOnlyList<BaseItem>? allSeries)
    {
        // Track which items we've already processed people for (avoid double-counting)
        var processedItemIds = new HashSet<Guid>();

        // For episodes, we want to count people at the series level to avoid
        // over-counting actors who appear in every episode. Track processed series.
        var processedSeriesIds = new HashSet<Guid>();

        var seriesLookup = BuildSeriesLookup(allSeries);
        var itemLookup = BuildItemLookup(allItems, allSeries);

        // Maximum number of actors to consider per item (top-billed only)
        const int maxActorsPerItem = 5;

        foreach (var watchedItem in profile.WatchedItems)
        {
            if (!HasMeaningfulInteraction(watchedItem))
            {
                continue;
            }

            ProcessWatchedItemPeople(
                profile,
                watchedItem,
                seriesLookup,
                itemLookup,
                processedSeriesIds,
                processedItemIds,
                maxActorsPerItem);
        }
    }

    /// <summary>
    ///     Builds a series-ID lookup for efficient series-level people aggregation. Extracted
    ///     verbatim from <see cref="BuildPeopleProfile"/>.
    /// </summary>
    /// <param name="allSeries">Pre-loaded series items, or <c>null</c>.</param>
    /// <returns>A series lookup, or <c>null</c> when no series were supplied.</returns>
    private static Dictionary<Guid, BaseItem>? BuildSeriesLookup(IReadOnlyList<BaseItem>? allSeries)
    {
        if (allSeries is not { Count: > 0 })
        {
            return null;
        }

        var seriesLookup = new Dictionary<Guid, BaseItem>(allSeries.Count);
        foreach (var s in allSeries)
        {
            seriesLookup.TryAdd(s.Id, s);
        }

        return seriesLookup;
    }

    /// <summary>
    ///     Builds an item-ID lookup from the video items plus series items for O(1) access (avoids N+1 DB queries).
    /// </summary>
    /// <param name="allItems">Pre-loaded video items from the library.</param>
    /// <param name="allSeries">Pre-loaded series items from the library, or <c>null</c>.</param>
    /// <returns>A combined item lookup keyed by item ID.</returns>
    private static Dictionary<Guid, BaseItem> BuildItemLookup(
        IReadOnlyList<BaseItem> allItems,
        IReadOnlyList<BaseItem>? allSeries)
    {
        var itemLookup = new Dictionary<Guid, BaseItem>(allItems.Count + (allSeries?.Count ?? 0));
        foreach (var item in allItems)
        {
            itemLookup.TryAdd(item.Id, item);
        }

        if (allSeries is not null)
        {
            foreach (var series in allSeries)
            {
                itemLookup.TryAdd(series.Id, series);
            }
        }

        return itemLookup;
    }

    /// <summary>
    ///     Determines whether a watched item represents a meaningful interaction: played, favorited, or partially-watched with at least 15% progress.
    /// </summary>
    /// <param name="watchedItem">The watched item to test.</param>
    /// <returns><c>true</c> when the item should contribute to the people profile.</returns>
    private static bool HasMeaningfulInteraction(WatchedItemInfo watchedItem)
    {
        if (watchedItem.Played || watchedItem.IsFavorite)
        {
            return true;
        }

        return watchedItem.PlaybackPositionTicks > 0
            && watchedItem.RuntimeTicks > 0
            && (double)watchedItem.PlaybackPositionTicks / watchedItem.RuntimeTicks >= 0.15;
    }

    /// <summary>
    ///     Aggregates people for a single watched item, dispatching episodes to their parent series (series-level metadata only) and movies/other items directly, while de-duplicating via the processed-ID sets.
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="watchedItem">The watched item to process.</param>
    /// <param name="seriesLookup">The series lookup, or <c>null</c>.</param>
    /// <param name="itemLookup">The combined item lookup.</param>
    /// <param name="processedSeriesIds">Accumulator of already-processed series IDs.</param>
    /// <param name="processedItemIds">Accumulator of already-processed item IDs.</param>
    /// <param name="maxActorsPerItem">Maximum number of actors to consider per item.</param>
    private void ProcessWatchedItemPeople(
        UserWatchProfile profile,
        WatchedItemInfo watchedItem,
        Dictionary<Guid, BaseItem>? seriesLookup,
        Dictionary<Guid, BaseItem> itemLookup,
        HashSet<Guid> processedSeriesIds,
        HashSet<Guid> processedItemIds,
        int maxActorsPerItem)
    {
        // For episodes: aggregate at series level to avoid per-episode noise
        if (watchedItem.SeriesId.HasValue && watchedItem.SeriesId.Value != Guid.Empty)
        {
            var seriesId = watchedItem.SeriesId.Value;
            if (!processedSeriesIds.Add(seriesId))
            {
                return; // Already counted people for this series
            }

            // Also mark in processedItemIds so that synthetic favorite-series entries
            // (which have ItemId = seriesId, SeriesId = null) don't double-count.
            processedItemIds.Add(seriesId);

            // Use series-level metadata only. If the series is not in the lookup, skip entirely rather than falling back to episode-level data.
            if (seriesLookup != null && seriesLookup.TryGetValue(seriesId, out var seriesItem))
            {
                AggregatePeopleFromItem(profile, seriesItem, maxActorsPerItem);
            }

            return;
        }

        // For movies and other items: aggregate directly
        if (!processedItemIds.Add(watchedItem.ItemId))
        {
            return; // Already processed
        }

        // Look up the actual BaseItem from the pre-built dictionary (O(1) instead of DB call)
        if (itemLookup.TryGetValue(watchedItem.ItemId, out var baseItem))
        {
            AggregatePeopleFromItem(profile, baseItem, maxActorsPerItem);
        }
    }

    /// <summary>
    ///     Aggregates people (actors/directors) from a single BaseItem into the profile's PeopleProfile.
    /// </summary>
    /// <param name="profile">The user profile to populate.</param>
    /// <param name="item">The library item to extract people from.</param>
    /// <param name="maxActors">Maximum number of actors to include (top-billed by sort order).</param>
    private void AggregatePeopleFromItem(UserWatchProfile profile, BaseItem item, int maxActors)
    {
        IReadOnlyList<PersonInfo>? people;
        try
        {
            people = _libraryManager.GetPeople(item);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate - skipping the item silently would defeat
            // any cooperative cancellation the caller relies on.
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Graceful: skip items where people lookup fails
            return;
        }

        if (people is null || people.Count == 0)
        {
            return;
        }

        var actorCount = 0;
        var directorCount = 0;
        const int maxDirectorsPerItem = 5;
        var seenPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
        {
            if (string.IsNullOrWhiteSpace(person.Name) || seenPeople.Contains(person.Name))
            {
                continue;
            }

            // Only include Actors and Directors (with caps to prevent metadata bloat)
            if (person.Type == PersonKind.Director)
            {
                TryAddPerson(profile, person.Name, seenPeople, ref directorCount, maxDirectorsPerItem);
            }
            else if (person.Type == PersonKind.Actor)
            {
                TryAddPerson(profile, person.Name, seenPeople, ref actorCount, maxActors);
            }
        }
    }

    /// <summary>
    ///     Adds a person (actor or director) to the profile's people distribution if the per-kind cap has not been reached, incrementing the running kind count and marking the name as seen.
    /// </summary>
    /// <param name="profile">The user profile being populated.</param>
    /// <param name="personName">The non-empty, not-yet-seen person name.</param>
    /// <param name="seenPeople">Accumulator of already-counted names for this item.</param>
    /// <param name="kindCount">Running count for this person kind (updated in place).</param>
    /// <param name="maxForKind">Maximum number of persons to include for this kind.</param>
    private static void TryAddPerson(
        UserWatchProfile profile,
        string personName,
        HashSet<string> seenPeople,
        ref int kindCount,
        int maxForKind)
    {
        if (kindCount >= maxForKind)
        {
            return;
        }

        kindCount++;
        seenPeople.Add(personName);
        profile.PeopleProfile.TryGetValue(personName, out var count);
        profile.PeopleProfile[personName] = count + 1;
    }

    /// <summary>
    ///     Normalizes ISO 639-2/B (3-letter) and ISO 639-3 language codes to ISO 639-1 (2-letter) for consistent cross-item comparison.
    /// </summary>
    /// <param name="language">The raw language code from the media stream metadata.</param>
    /// <returns>A normalized 2-letter language code, or null if the input is invalid.</returns>
    internal static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var lower = language.Trim().ToLowerInvariant();

        return lower switch
        {
            "ger" or "deu" => "de",
            "eng" => "en",
            "jpn" => "ja",
            "fre" or "fra" => "fr",
            "spa" => "es",
            "ita" => "it",
            "por" => "pt",
            "rus" => "ru",
            "chi" or "zho" => "zh",
            "kor" => "ko",
            "dut" or "nld" => "nl",
            "pol" => "pl",
            "tur" => "tr",
            "ara" => "ar",
            "hin" => "hi",
            "swe" => "sv",
            "dan" => "da",
            "nor" or "nob" or "nno" => "no",
            "fin" => "fi",
            "hun" => "hu",
            "ces" or "cze" => "cs",
            "ron" or "rum" => "ro",
            "tha" => "th",
            "vie" => "vi",
            "ukr" => "uk",
            "heb" => "he",
            "ell" or "gre" => "el",
            "ind" => "id",
            "msa" or "may" => "ms",
            "hrv" => "hr",
            "srp" => "sr",
            "slk" or "slo" => "sk",
            "slv" => "sl",
            "bul" => "bg",
            "cat" => "ca",
            "est" => "et",
            "lav" => "lv",
            "lit" => "lt",
            "fas" or "per" => "fa",
            "urd" => "ur",
            _ when lower.Length == 2 => lower, // Already ISO 639-1
            _ => lower // Keep unmapped 3-letter codes as-is
        };
    }

    /// <summary>
    ///     Fetches user data for many items in one shot via the Jellyfin 12+ IUserDataManager.GetUserDataBatch call.
    /// </summary>
    private IReadOnlyDictionary<Guid, UserItemData>? TryLoadUserDataBatch(
        Jellyfin.Database.Implementations.Entities.User user,
        IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 0)
        {
            return new Dictionary<Guid, UserItemData>();
        }

        return BatchFallbackHelper.TryRunBatch<IReadOnlyDictionary<Guid, UserItemData>?>(
            batchCall: () =>
            {
                var batch = _userDataManager.GetUserDataBatch(items, user);
                if (batch is null)
                {
                    return null;
                }

                // Accept whatever dictionary shape Jellyfin hands back.
                if (batch is IReadOnlyDictionary<Guid, UserItemData> readOnly)
                {
                    return readOnly;
                }

                return new Dictionary<Guid, UserItemData>(batch);
            },
            fallbackValue: null,
            onFailure: ex => _pluginLog.LogWarning(
                LogCategory,
                $"Batch user-data load failed for user '{user.Username}'; falling back to per-item lookup.",
                ex,
                _logger));
    }

    /// <summary>
    ///     Looks up user data for a single item, preferring the pre-fetched batch dictionary but falling back to a per-item GetUserData call when the batch was not available for this user (batch returned null due to an exception upstream).
    /// </summary>
    /// <param name="lookup">The batch lookup, or <c>null</c> if the batch failed.</param>
    /// <param name="item">The item whose user data to fetch.</param>
    /// <param name="user">The Jellyfin user (used only for the fallback path).</param>
    /// <returns>The user's data for the item, or <c>null</c> when unavailable.</returns>
    private UserItemData? LookupUserData(
        IReadOnlyDictionary<Guid, UserItemData>? lookup,
        BaseItem item,
        Jellyfin.Database.Implementations.Entities.User user)
    {
        if (lookup is not null)
        {
            lookup.TryGetValue(item.Id, out var found);
            return found;
        }

        return _userDataManager.GetUserData(user, item);
    }
}
