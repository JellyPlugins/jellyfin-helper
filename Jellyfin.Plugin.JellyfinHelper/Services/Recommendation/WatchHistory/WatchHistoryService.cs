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
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
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
                DateCreated = item.DateCreated,
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

        // Check series-level favorites: users can favorite an entire series in Jellyfin
        // (the heart button on the series page). This UserData lives on the Series item
        // itself, not on individual episodes.
        allSeries ??= LoadAllSeriesItems();

        foreach (var series in allSeries)
        {
            var seriesUserData = _userDataManager.GetUserData(user, series);
            if (seriesUserData is not null && seriesUserData.IsFavorite)
            {
                profile.FavoriteSeriesIds.Add(series.Id);
                profile.FavoriteCount++;

                // Create a synthetic WatchedItemInfo so that the series' genres, year,
                // and community rating flow into PreferenceBuilder.BuildGenrePreferenceVector()
                // with the FavoriteGenreBoostFactor (3×). Without this, favoriting a series
                // only populates FavoriteSeriesIds (used for candidate exclusion) but does NOT
                // influence genre preferences, studio preferences, or training labels.
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
                    PrimaryImageTag = null
                });

                // Also accumulate genre distribution for series-level favorites
                if (series.Genres is not null)
                {
                    foreach (var genre in series.Genres)
                    {
                        if (!string.IsNullOrWhiteSpace(genre))
                        {
                            profile.GenreDistribution.TryGetValue(genre, out var count);
                            profile.GenreDistribution[genre] = count + 1;
                        }
                    }
                }
            }
        }

        // Build audio + subtitle language profiles in a single pass over allItems.
        // Previously these were two separate methods each iterating allItems and calling
        // GetUserData per item, causing 2× the UserData lookups.
        BuildLanguageProfiles(profile, user, allItems);

        // Build people (actors/directors) profile from BaseItem.People metadata
        BuildPeopleProfile(profile, user, allItems, allSeries);

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
    private void BuildLanguageProfiles(
        UserWatchProfile profile,
        Jellyfin.Database.Implementations.Entities.User user,
        IReadOnlyList<BaseItem> allItems)
    {
        foreach (var item in allItems)
        {
            var userData = _userDataManager.GetUserData(user, item);
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
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Graceful: skip items where stream lookup fails (e.g. corrupted metadata)
                continue;
            }

            if (allStreams is null || allStreams.Count == 0)
            {
                continue;
            }

            // === Audio Language Analysis ===
            var audioStreams = allStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
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
                    var defaultStream = audioStreams
                        .FirstOrDefault(s => s.IsDefault) ?? audioStreams[0];
                    usedAudioLanguage = NormalizeLanguage(defaultStream.Language);
                }

                if (!string.IsNullOrEmpty(usedAudioLanguage))
                {
                    var availableAudioLanguages = audioStreams
                        .Select(s => NormalizeLanguage(s.Language))
                        .Where(l => !string.IsNullOrEmpty(l))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    if (availableAudioLanguages > 0)
                    {
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

            // === Subtitle Language Analysis ===
            if (userData.SubtitleStreamIndex.HasValue && userData.SubtitleStreamIndex.Value >= 0)
            {
                var subtitleStreams = allStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
                if (subtitleStreams.Count > 0)
                {
                    var chosenSubStream = subtitleStreams
                        .FirstOrDefault(s => s.Index == userData.SubtitleStreamIndex.Value);
                    var usedSubLanguage = NormalizeLanguage(chosenSubStream?.Language);

                    if (!string.IsNullOrEmpty(usedSubLanguage))
                    {
                        var availableSubLanguages = subtitleStreams
                            .Select(s => NormalizeLanguage(s.Language))
                            .Where(l => !string.IsNullOrEmpty(l))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count();

                        if (availableSubLanguages > 0)
                        {
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
                    }
                }
            }
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
    /// <param name="user">The Jellyfin user entity.</param>
    /// <param name="allItems">Pre-loaded video items from the library.</param>
    /// <param name="allSeries">Pre-loaded series items from the library.</param>
    private void BuildPeopleProfile(
        UserWatchProfile profile,
        Jellyfin.Database.Implementations.Entities.User user,
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
        var itemLookup = new Dictionary<Guid, BaseItem>(allItems.Count);
        foreach (var item in allItems)
        {
            itemLookup.TryAdd(item.Id, item);
        }

        // Maximum number of actors to consider per item (top-billed only)
        const int maxActorsPerItem = 5;

        foreach (var watchedItem in profile.WatchedItems)
        {
            // Only include items with meaningful interaction
            if (watchedItem is { Played: false, IsFavorite: false })
            {
                continue;
            }

            // For episodes: aggregate at series level to avoid per-episode noise
            if (watchedItem.SeriesId.HasValue && watchedItem.SeriesId.Value != Guid.Empty)
            {
                var seriesId = watchedItem.SeriesId.Value;
                if (!processedSeriesIds.Add(seriesId))
                {
                    continue; // Already counted people for this series
                }

                // Try to get series people
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
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Graceful: skip items where people lookup fails
            return;
        }

        if (people is null || people.Count == 0)
        {
            return;
        }

        var actorCount = 0;
        foreach (var person in people)
        {
            if (string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            // Only include Actors and Directors
            if (person.Type == PersonKind.Director)
            {
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
}
