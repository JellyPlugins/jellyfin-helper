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
            catch (Exception ex) when (!ex.IsFatal())
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
    ///     Collapses a flat episode list into a per-series playable-episode count. Only episodes
    ///     with a non-empty <c>Path</c> and a valid <c>SeriesId</c> are counted, matching the
    ///     engine's identical rule (see the recommendation engine's candidate load) so the
    ///     progression ratio (watched / total) stays consistent across both subsystems.
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
        // Falls back to null on any exception; the per-item lookup below then reverts to
        // the pre-batch code path via _userDataManager.GetUserData, so the profile never
        // regresses to worse behavior than before this optimization.
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

        // Check series-level favorites: users can favorite an entire series in Jellyfin
        // (the heart button on the series page). This UserData lives on the Series item
        // itself, not on individual episodes.
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

        // Build audio + subtitle language profiles in a single pass over allItems.
        // Previously these were two separate methods each iterating allItems and calling
        // GetUserData per item, causing 2× the UserData lookups.
        // Reuses the already-fetched itemUserDataLookup to avoid a third pass.
        BuildLanguageProfiles(profile, user, allItems, itemUserDataLookup);

        // Build people (actors/directors) profile from BaseItem.People metadata
        BuildPeopleProfile(profile, allItems, allSeries);

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

    /// <summary>
    ///     Builds a <see cref="WatchedItemInfo"/> for an interacted library item and folds its
    ///     statistics (watch counts, runtime, genres, favorites, ratings, last activity) into the
    ///     profile. Extracted verbatim from the primary watched-items loop in <see cref="BuildProfile"/>.
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
        // Billed cast/directors for this item, cached as aligned name/weight lists so the training
        // path can compute BillingWeightedPeople with the same shared helper the live path uses
        // (closes the organic/aggregated-series train/serve gap that previously hardcoded 0.0).
        // Resolved ONLY for non-episode items (movies + series), which are exactly the item types
        // that appear as live scoring candidates - episodes never do. Skipping episodes also
        // preserves the invariant that people are aggregated at series level, never per episode
        // (GetPeople is never called on an Episode), avoiding guest-cast noise.
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

            // Content-affinity source fields. Populated here on the watched side so the preference
            // builders (franchise/country/inherited-tag/writer/series-completability) have real
            // signal to compare live candidates against - extracted with the exact same shared,
            // library-free resolvers the live scoring and precompute paths use, guaranteeing parity.
            // WriterNames reuses the people list already fetched above (no extra GetPeople call).
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
    ///     Resolves the billed cast/directors for a watched item, returning <c>null</c> for episodes
    ///     (people are aggregated at series level, never per episode) and swallowing non-fatal lookup
    ///     failures. Extracted verbatim from the people-resolution block of
    ///     <see cref="AccumulateWatchedItem"/>.
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
    ///     Folds a watched item's statistics (played counts, runtime, genres, favorites, community
    ///     rating, last activity) into the profile. Extracted verbatim from the statistics-accumulation
    ///     block of <see cref="AccumulateWatchedItem"/>.
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
    ///     Records a favorited series into the profile: adds it to <c>FavoriteSeriesIds</c>, appends
    ///     a synthetic <see cref="WatchedItemInfo"/> so its metadata influences preference vectors, and
    ///     folds its genres into the distribution. Extracted verbatim from the series loop in
    ///     <see cref="BuildProfile"/>.
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

        // Create a synthetic WatchedItemInfo so that the series' genres, year,
        // and community rating flow into PreferenceBuilder.BuildGenrePreferenceVector()
        // with the FavoriteGenreBoostFactor (3×). Without this, favoriting a series
        // only populates FavoriteSeriesIds (used for candidate exclusion) but does NOT
        // influence genre preferences, studio preferences, or training labels.
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

            // Content-affinity source fields for the favorited series, using the same shared
            // resolvers as the primary loop so a favorited series contributes franchise/country/
            // inherited-tag/writer/completability preference signal identically to a watched item.
            // WriterNames reuses seriesPeople (already fetched); SeriesStatus/EndDate are real here.
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
    ///     Folds an item's genres into the profile's genre distribution, skipping null/whitespace
    ///     entries. Extracted verbatim from the two identical genre-distribution blocks in
    ///     <see cref="BuildProfile"/>.
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
    ///     Builds both the audio language and subtitle language preference profiles
    ///     in a single pass over <paramref name="allItems"/>.
    ///     This eliminates the prior pattern of two separate loops each calling
    ///     <c>GetUserData</c> and <c>GetMediaStreams</c> per item (2× the cost).
    ///     Distinguishes "chosen" (user had alternatives) from "forced" (only option)
    ///     for both audio and subtitle tracks.
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

            // === Audio Language Analysis ===
            AccumulateAudioLanguage(profile, userData, audioStreams);

            // === Subtitle Language Analysis ===
            AccumulateSubtitleLanguage(profile, userData, subtitleStreams);
        }
    }

    /// <summary>
    ///     Folds the audio language the user watched an item in into the profile's language profile,
    ///     distinguishing "chosen" (alternatives existed) from "forced" (single option). Extracted
    ///     verbatim from the audio-analysis block of <see cref="BuildLanguageProfiles"/>.
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
    ///     Folds the subtitle language the user selected for an item into the profile's subtitle
    ///     language profile, distinguishing "chosen" from "forced". Extracted verbatim from the
    ///     subtitle-analysis block of <see cref="BuildLanguageProfiles"/>.
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
    ///     Builds the user's people (actors/directors) preference profile by analyzing
    ///     <c>BaseItem.People</c> metadata for each watched or favorited item.
    ///     Only Actors and Directors are included. Each person is counted once per distinct item
    ///     (not per play count) to reflect breadth of exposure rather than re-watch frequency.
    ///     For episodes, the parent series' people are used (via SeriesId lookup) to avoid
    ///     counting per-episode guest actors disproportionately.
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

        // Build a series lookup for efficient access
        Dictionary<Guid, BaseItem>? seriesLookup = null;
        if (allSeries is { Count: > 0 })
        {
            seriesLookup = new Dictionary<Guid, BaseItem>(allSeries.Count);
            foreach (var s in allSeries)
            {
                seriesLookup.TryAdd(s.Id, s);
            }
        }

        // Build an item lookup from allItems for O(1) access instead of N+1 DB queries.
        // This eliminates the per-item GetItemList call that was causing performance issues.
        // Also include series items so that synthetic favorite-series entries in WatchedItems
        // can resolve to their BaseItem for people aggregation.
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

        // Maximum number of actors to consider per item (top-billed only)
        const int maxActorsPerItem = 5;

        foreach (var watchedItem in profile.WatchedItems)
        {
            // Only include items with meaningful interaction.
            // Includes: Played items, Favorites, and partially-watched items with >=15% progress.
            // Excludes: items started and immediately abandoned (< 15% progress).
            if (!watchedItem.Played && !watchedItem.IsFavorite)
            {
                var hasSignificantProgress = watchedItem.PlaybackPositionTicks > 0
                    && watchedItem.RuntimeTicks > 0
                    && (double)watchedItem.PlaybackPositionTicks / watchedItem.RuntimeTicks >= 0.15;
                if (!hasSignificantProgress)
                {
                    continue;
                }
            }

            // For episodes: aggregate at series level to avoid per-episode noise
            if (watchedItem.SeriesId.HasValue && watchedItem.SeriesId.Value != Guid.Empty)
            {
                var seriesId = watchedItem.SeriesId.Value;
                if (!processedSeriesIds.Add(seriesId))
                {
                    continue; // Already counted people for this series
                }

                // Also mark in processedItemIds so that synthetic favorite-series entries
                // (which have ItemId = seriesId, SeriesId = null) don't double-count.
                processedItemIds.Add(seriesId);

                // Use series-level metadata only. If the series is not in the lookup, skip entirely
                // rather than falling back to episode-level data. Falling back would count only the
                // limited guest cast of a single episode instead of the full main cast, and would
                // also cause a double-count if a synthetic favourite-series row (ItemId == seriesId,
                // SeriesId == null) is processed later and the processedItemIds guard at line 559
                // already blocked it from reaching the people aggregation path.
                if (seriesLookup != null && seriesLookup.TryGetValue(seriesId, out var seriesItem))
                {
                    AggregatePeopleFromItem(profile, seriesItem, maxActorsPerItem);
                }

                continue;
            }

            // For movies and other items: aggregate directly
            if (!processedItemIds.Add(watchedItem.ItemId))
            {
                continue; // Already processed
            }

            // Look up the actual BaseItem from the pre-built dictionary (O(1) instead of DB call)
            if (itemLookup.TryGetValue(watchedItem.ItemId, out var baseItem))
            {
                AggregatePeopleFromItem(profile, baseItem, maxActorsPerItem);
            }
        }
    }

    /// <summary>
    ///     Aggregates people (actors/directors) from a single BaseItem into the profile's PeopleProfile.
    ///     Only includes persons with PersonKind.Actor (top-billed, limited count) and PersonKind.Director.
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
            if (string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            if (seenPeople.Contains(person.Name))
            {
                continue;
            }

            // Only include Actors and Directors (with caps to prevent metadata bloat)
            if (person.Type == PersonKind.Director)
            {
                if (directorCount >= maxDirectorsPerItem)
                {
                    continue;
                }

                directorCount++;
                seenPeople.Add(person.Name);
                profile.PeopleProfile.TryGetValue(person.Name, out var dirCount);
                profile.PeopleProfile[person.Name] = dirCount + 1;
            }
            else if (person.Type == PersonKind.Actor)
            {
                if (actorCount >= maxActors)
                {
                    continue; // Only top-billed actors
                }

                actorCount++;
                seenPeople.Add(person.Name);
                profile.PeopleProfile.TryGetValue(person.Name, out var actCount);
                profile.PeopleProfile[person.Name] = actCount + 1;
            }
        }
    }

    /// <summary>
    ///     Normalizes ISO 639-2/B (3-letter) and ISO 639-3 language codes to ISO 639-1 (2-letter)
    ///     for consistent cross-item comparison. Codes already in 2-letter form are returned as-is.
    ///     Returns null for null, empty, or whitespace-only input.
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
    ///     Fetches user data for many items in one shot via the Jellyfin 12+
    ///     <c>IUserDataManager.GetUserDataBatch</c> call. Returns <c>null</c> on failure so
    ///     the caller can fall back to per-item lookups. An empty <paramref name="items"/>
    ///     list short-circuits with an empty dictionary - treated as "batch succeeded, no
    ///     results" rather than "batch failed, fall back".
    ///     The try/catch shape is delegated to <see cref="BatchFallbackHelper"/> so the
    ///     three batch call sites in the plugin can't drift apart on cancellation handling.
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
                "WatchHistory",
                $"Batch user-data load failed for user '{user.Username}'; falling back to per-item lookup.",
                ex,
                _logger));
    }

    /// <summary>
    ///     Looks up user data for a single item, preferring the pre-fetched batch dictionary
    ///     but falling back to a per-item <c>GetUserData</c> call when the batch was not
    ///     available for this user (batch returned <c>null</c> due to an exception upstream).
    ///     A missing entry in a valid batch is treated as "no user data" - identical to
    ///     the pre-batch behavior of <c>GetUserData</c> returning <c>null</c>.
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
