using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>
///     Creates and manages per-user recommendation playlists in Jellyfin. Playlists are identified by a well-known name prefix so they can be found and replaced on each scheduled run.
/// </summary>
public sealed class RecommendationPlaylistService : IRecommendationPlaylistService
{
    /// <summary>
    ///     The prefix used to identify recommendation playlists managed by this plugin. This prefix is used for searching existing playlists to delete before recreation.
    /// </summary>
    internal const string PlaylistNamePrefix = "🎬 Recommended";

    /// <summary>Durable provenance for identifying managed playlists after an owner's rename or deletion.</summary>
    internal const string ManagedOwnerProviderKey = "JellyfinHelperPlaylistOwner";

    private const string LogCategory = "PlaylistSync";

    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<RecommendationPlaylistService> _logger;
    private readonly IUserDataManager? _userDataManager;
    private readonly IPluginConfigurationService? _configService;
    private readonly VirtualItemDetector? _virtualItems;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationPlaylistService"/> class.
    /// </summary>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="userDataManager">Current Jellyfin playback state for final delivery checks.</param>
    /// <param name="configService">Current excluded-library configuration.</param>
    /// <param name="virtualItems">Shared detector for non-playable virtual stubs.</param>
    public RecommendationPlaylistService(
        IPlaylistManager playlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<RecommendationPlaylistService> logger,
        IUserDataManager? userDataManager = null,
        IPluginConfigurationService? configService = null,
        VirtualItemDetector? virtualItems = null)
    {
        _playlistManager = playlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
        _userDataManager = userDataManager;
        _configService = configService;
        _virtualItems = virtualItems;
    }

    /// <inheritdoc />
    public async Task<PlaylistSyncResult> UpdatePlaylistsForAllUsersAsync(
        IReadOnlyList<RecommendationResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        var syncResult = new PlaylistSyncResult();

        _pluginLog.LogInfo(
            LogCategory,
            $"Starting playlist sync for {results.Count} users.",
            _logger);

        // Disabled users are treated as if they do not exist: never create playlists for them and
        // remove any stale ones from previous runs. A null roster means the user list was
        // unavailable, which must stay distinct from "no disabled users" so stale results
        // cannot create playlists for users who are disabled at sync time.
        var disabledUserIds = GetDisabledUserIds(cancellationToken, out var liveUserIds);
        var handledDisabledUserIds = new HashSet<Guid>();

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (disabledUserIds?.Contains(result.UserId) == true)
            {
                SyncDisabledUserPlaylist(result, syncResult, handledDisabledUserIds, cancellationToken);
                continue;
            }

            if (!IsVerifiedEnabledUser(result, handledDisabledUserIds, syncResult, cancellationToken))
            {
                continue;
            }

            await SyncEnabledUserPlaylistAsync(result, syncResult, cancellationToken).ConfigureAwait(false);
        }

        disabledUserIds ??= [];

        RemoveStalePlaylistsForDisabledUsers(disabledUserIds, handledDisabledUserIds, syncResult, cancellationToken);
        if (liveUserIds is not null)
        {
            syncResult.OldPlaylistsRemoved += RemoveIdentifiedInactivePlaylists(liveUserIds, disabledUserIds, cancellationToken);
        }

        _pluginLog.LogInfo(
            LogCategory,
            $"Playlist sync complete: {syncResult.PlaylistsCreated} created, {syncResult.OldPlaylistsRemoved} old removed, {syncResult.PlaylistsFailed} failed, {syncResult.TotalItemsAdded} total items.",
            _logger);

        return syncResult;
    }

    /// <summary>
    ///     Removes stale playlists for a single disabled user without creating new ones. A per-user
    ///     cleanup failure is logged and swallowed so remaining users are still processed.
    /// </summary>
    /// <param name="result">The recommendation result of the disabled user.</param>
    /// <param name="syncResult">The running sync counters (updated in place).</param>
    /// <param name="handledDisabledUserIds">Accumulator of already-processed disabled user ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private void SyncDisabledUserPlaylist(
        RecommendationResult result,
        PlaylistSyncResult syncResult,
        HashSet<Guid> handledDisabledUserIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var removedDisabled = RemoveUserPlaylists(result.UserId, cancellationToken);
            syncResult.OldPlaylistsRemoved += removedDisabled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to remove stale playlists for disabled user '{result.UserName}'.",
                ex,
                _logger);
        }

        handledDisabledUserIds.Add(result.UserId);

        _pluginLog.LogDebug(
            LogCategory,
            $"Skipping playlist creation for disabled user '{result.UserName}' - stale playlists removed.",
            _logger);
    }

    /// <summary>
    ///     Syncs the recommendation playlist for a single enabled user: creates the new playlist
    ///     before removing old ones so the user is never left without one if creation fails.
    /// </summary>
    /// <param name="result">The recommendation result of the enabled user.</param>
    /// <param name="syncResult">The running sync counters (updated in place).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task SyncEnabledUserPlaylistAsync(
        RecommendationResult result,
        PlaylistSyncResult syncResult,
        CancellationToken cancellationToken)
    {
        try
        {
            // Skip playlist creation if there are no recommendations
            if (result.Recommendations.Count == 0)
            {
                // Still clean up old playlists when there are no new recommendations
                var removedEmpty = RemoveUserPlaylists(result.UserId, cancellationToken);
                syncResult.OldPlaylistsRemoved += removedEmpty;

                _pluginLog.LogDebug(
                    LogCategory,
                    $"No recommendations for user '{result.UserName}' - skipping playlist creation.",
                    _logger);
                return;
            }

            // Resolve each series to one currently permitted unseen episode before
            // PlaylistManager can expand its container into every child episode.
            var user = _userManager.GetUserById(result.UserId);
            if (user is null || IsDisabled(user))
            {
                return;
            }

            var excluded = CleanupConfigHelper.ParseCommaSeparated(_configService?.GetConfiguration().ExcludedLibraries ?? string.Empty);
            var scope = excluded.Count > 0 ? LibraryPathResolver.GetLibraryRootScope(_libraryManager, excluded) : null;
            var itemIds = ResolvePlaylistItemIds(result.Recommendations, result.Recommendations.Count, user, scope, cancellationToken);

            if (itemIds.Length == 0)
            {
                // Clean up stale playlists when no playable items resolve,
                // so users don't keep seeing outdated recommendations.
                var removedStale = RemoveUserPlaylists(result.UserId, cancellationToken);
                syncResult.OldPlaylistsRemoved += removedStale;

                _pluginLog.LogDebug(
                    LogCategory,
                    $"No playable items resolved for user '{result.UserName}' - skipping playlist creation.",
                    _logger);
                return;
            }

            // Build a personalized playlist name per user to avoid filesystem name collisions
            var playlistName = BuildPlaylistName(result.UserName);

            var request = new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = result.UserId,
                ItemIdList = itemIds,
                MediaType = MediaType.Unknown, // Mixed content (movies + series)

                // Explicitly private: without this the visibility falls back to the server
                // default, which was public on some Jellyfin versions and made every user see every playlist.
                Users = [],
                Public = false,
            };

            // Create the new playlist BEFORE removing old ones so the user is never left
            // without a recommendation playlist if creation fails.
            cancellationToken.ThrowIfCancellationRequested();
            var ownerAtCreation = _userManager.GetUserById(result.UserId);
            if (ownerAtCreation is null || IsDisabled(ownerAtCreation))
            {
                return;
            }

            if (_userDataManager is not null || _configService is not null || _virtualItems is not null)
            {
                itemIds = RecheckResolvedItemsAtCreation(itemIds, ownerAtCreation, cancellationToken);
                if (itemIds.Length == 0)
                {
                    return;
                }

                request.ItemIdList = itemIds;
            }

            var playlistResult = await _playlistManager.CreatePlaylist(request).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(playlistResult.Id))
            {
                await MarkManagedPlaylistAsync(playlistResult.Id, result.UserId, cancellationToken).ConfigureAwait(false);
                // New playlist created - now safe to remove old playlists.
                var removed = RemoveUserPlaylistsExcept(
                    result.UserId,
                    playlistResult.Id,
                    cancellationToken);
                syncResult.OldPlaylistsRemoved += removed;

                syncResult.PlaylistsCreated++;
                syncResult.TotalItemsAdded += itemIds.Length;

                _pluginLog.LogDebug(
                    LogCategory,
                    $"Created playlist '{playlistName}' for user '{result.UserName}' with {itemIds.Length} items.",
                    _logger);
            }
            else
            {
                syncResult.PlaylistsFailed++;
                _pluginLog.LogWarning(
                    LogCategory,
                    $"Playlist creation returned empty ID for user '{result.UserName}'.",
                    logger: _logger);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            syncResult.PlaylistsFailed++;
            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to sync playlist for user '{result.UserName}'.",
                ex,
                _logger);
        }
    }

    /// <summary>
    ///     Removes stale playlists for disabled users that had no fresh results (e.g. excluded from
    ///     generation), so no outdated recommendation playlist survives from previous runs.
    /// </summary>
    /// <param name="disabledUserIds">The ids of all disabled users.</param>
    /// <param name="handledDisabledUserIds">Accumulator of already-processed disabled user ids.</param>
    /// <param name="syncResult">The running sync counters (updated in place).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private void RemoveStalePlaylistsForDisabledUsers(
        HashSet<Guid> disabledUserIds,
        HashSet<Guid> handledDisabledUserIds,
        PlaylistSyncResult syncResult,
        CancellationToken cancellationToken)
    {
        foreach (var disabledUserId in disabledUserIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!handledDisabledUserIds.Add(disabledUserId))
            {
                continue;
            }

            try
            {
                syncResult.OldPlaylistsRemoved += RemoveUserPlaylists(disabledUserId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogWarning(
                    LogCategory,
                    $"Failed to remove stale playlists for disabled user {disabledUserId}.",
                    ex,
                    _logger);
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> RemoveAllRecommendationPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var totalRemoved = 0;
        var users = _userManager.GetUsers().ToList();

        _pluginLog.LogInfo(
            LogCategory,
            $"Removing all recommendation playlists for {users.Count} users...",
            _logger);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var removed = RemoveUserPlaylists(user.Id, cancellationToken);
                totalRemoved += removed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogWarning(
                    LogCategory,
                    $"Failed to remove playlists for user '{user.Username}'.",
                    ex,
                    _logger);
            }
        }

        totalRemoved += RemoveIdentifiedInactivePlaylists(users.Select(user => user.Id).ToHashSet(), [], cancellationToken, removeAll: true);

        _pluginLog.LogInfo(
            LogCategory,
            $"Removed {totalRemoved} recommendation playlists.",
            _logger);

        return totalRemoved;
    }

    /// <summary>
    ///     Builds the playlist name with the user's display name so each user gets a uniquely named playlist on disk.
    /// </summary>
    /// <param name="userName">The user's display name.</param>
    /// <returns>The full playlist name.</returns>
    internal static string BuildPlaylistName(string userName)
    {
        return PlaylistNamePrefix + " for " + (string.IsNullOrWhiteSpace(userName) ? "you" : userName);
    }

    /// <summary>
    ///     Returns true when the Jellyfin user is disabled. Disabled users are treated as if they do not
    ///     exist: no recommendations are generated and their playlists are removed.
    /// </summary>
    /// <param name="user">The Jellyfin user entity.</param>
    /// <returns><c>true</c> when the user carries the IsDisabled permission.</returns>
    private static bool IsDisabled(Jellyfin.Database.Implementations.Entities.User user) =>
        user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled);

    /// <summary>
    ///     Resolves every result against the live user record, even when an earlier roster was available.
    ///     A resolved disabled user is routed through disabled-user cleanup, a resolved enabled
    ///     user returns true for syncing, and a missing/failed lookup returns false to skip.
    /// </summary>
    private bool IsVerifiedEnabledUser(
        RecommendationResult result,
        HashSet<Guid> handledDisabledUserIds,
        PlaylistSyncResult syncResult,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = _userManager.GetUserById(result.UserId);
            if (user is null)
            {
                _pluginLog.LogDebug(
                    LogCategory,
                    $"Skipping playlist creation for unknown user '{result.UserName}' - user lookup returned null.",
                    _logger);
                return false;
            }

            if (IsDisabled(user))
            {
                SyncDisabledUserPlaylist(result, syncResult, handledDisabledUserIds, cancellationToken);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Skipping playlist creation for user '{result.UserName}' - user lookup failed.",
                ex,
                _logger);
            return false;
        }
    }

    /// <summary>
    ///     Collects the ids of all disabled users. Never throws for non-cancellation failures: a user-list
    ///     failure degrades to null (roster unavailable) so callers resolve each result via GetUserById.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The disabled user ids, or null when the roster is unavailable.</returns>
    /// <param name="liveUserIds">The complete known roster, or null when enumeration was unavailable.</param>
    private HashSet<Guid>? GetDisabledUserIds(CancellationToken cancellationToken, out HashSet<Guid>? liveUserIds)
    {
        liveUserIds = null;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var users = _userManager.GetUsers();
            if (users is null)
            {
                _pluginLog.LogWarning(
                    LogCategory,
                    "Failed to list disabled users - user roster unavailable, verifying each user individually.",
                    logger: _logger);
                return null;
            }

            var roster = users.Select(user => (user.Id, Disabled: IsDisabled(user))).ToArray();
            liveUserIds = roster.Select(user => user.Id).ToHashSet();
            return roster.Where(user => user.Disabled).Select(user => user.Id).ToHashSet();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                LogCategory,
                "Failed to list disabled users - user roster unavailable, verifying each user individually.",
                ex,
                _logger);
            return null;
        }
    }

    /// <summary>
    ///     Resolves recommendation item IDs into playable playlist item IDs. Jellyfin's IPlaylistManager expands container items (Series, Seasons) into all their child episodes when added to a playlist.
    /// </summary>
    /// <param name="recommendations">The score-ranked recommendations to resolve.</param>
    /// <param name="maxItems">Maximum number of playable items to collect.</param>
    /// <returns>An array of playable item IDs suitable for playlist creation.</returns>
    internal Guid[] ResolvePlaylistItemIds(IEnumerable<RecommendedItem> recommendations, int maxItems)
        => ResolvePlaylistItemIds(recommendations, maxItems, null, null, CancellationToken.None);

    private Guid[] ResolvePlaylistItemIds(
        IEnumerable<RecommendedItem> recommendations,
        int maxItems,
        Jellyfin.Database.Implementations.Entities.User? user,
        LibraryRootScope? scope,
        CancellationToken cancellationToken)
    {
        var resolvedIds = new List<Guid>();
        var uniqueIds = new HashSet<Guid>();
        var skippedCount = 0;
        var snapshot = recommendations.ToArray();
        var verifyLive = _userDataManager is not null || _configService is not null || _virtualItems is not null;
        var permitted = user is not null && verifyLive
            ? LibraryUserAccess.GetAllowedIds(_libraryManager, user, snapshot.Select(item => item.ItemId).Distinct().ToArray())
            : null;
        var sequenceMovies = new Lazy<(Dictionary<int, Movie> Unseen, HashSet<int> Completed)>(() => LoadSequenceMovies(user!, scope, cancellationToken));

        foreach (var rec in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolvedIds.Count >= maxItems)
            {
                break;
            }

            if (permitted?.Contains(rec.ItemId) == false)
            {
                skippedCount++;
                continue;
            }

            var live = verifyLive ? _libraryManager.GetItemById(rec.ItemId) : null;
            if (verifyLive && (live is null || !IsEligibleAtDelivery(live, user, scope)))
            {
                skippedCount++;
                continue;
            }

            if (string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve one permitted unseen episode to avoid playlist expansion.
                var firstEpisode = ResolveFirstEpisodeForSeries(rec.ItemId, user, scope, cancellationToken);

                if (firstEpisode.HasValue && uniqueIds.Add(firstEpisode.Value))
                {
                    resolvedIds.Add(firstEpisode.Value);
                    _pluginLog.LogDebug(
                        LogCategory,
                        $"Resolved series '{rec.Name}' to first eligible episode (ID: {firstEpisode.Value}).",
                        _logger);
                }
                else
                {
                    // Skip unresolvable series and let the loop pick the next candidate.
                    skippedCount++;
                    _pluginLog.LogDebug(
                        LogCategory,
                        $"Could not resolve first episode for series '{rec.Name}' (ID: {rec.ItemId}) - skipping, will backfill.",
                        _logger);
                }
            }
            else
            {
                var itemId = rec.ItemId;
                if (live is Movie && user is not null && _userDataManager is not null
                    && TmdbLibraryMapper.TryGetTmdbId(live, out var movieId)
                    && MovieSequencePolicy.IsKnownMovie(movieId, ContentAffinityResolver.ResolveTmdbCollectionId(live)))
                {
                    var (unseen, completed) = sequenceMovies.Value;
                    var required = MovieSequencePolicy.RequiredMovie(movieId, ContentAffinityResolver.ResolveTmdbCollectionId(live), completed);
                    if (required != movieId)
                    {
                        if (!unseen.TryGetValue(required, out var predecessor))
                        {
                            skippedCount++;
                            continue;
                        }

                        itemId = predecessor.Id;
                    }
                }

                if (uniqueIds.Add(itemId))
                {
                    resolvedIds.Add(itemId);
                }
            }
        }

        if (skippedCount > 0)
        {
            _pluginLog.LogInfo(
                LogCategory,
                $"Skipped {skippedCount} unresolvable items during playlist resolution. Resolved {resolvedIds.Count}/{maxItems} items.",
                _logger);
        }

        return resolvedIds.ToArray();
    }

    /// <summary>
    ///     Finds the first episode of a series by querying the library for all episodes belonging to the given series ID, then sorting by season and episode index in memory.
    /// </summary>
    /// <param name="seriesId">The Jellyfin series item ID.</param>
    /// <param name="user">Current user policy and playback owner.</param>
    /// <param name="scope">Captured current excluded-library roots.</param>
    /// <param name="cancellationToken">Cancellation checked before resolution.</param>
    /// <returns>The ID of the first permitted unseen episode, or null if none remains.</returns>
    private Guid? ResolveFirstEpisodeForSeries(Guid seriesId, Jellyfin.Database.Implementations.Entities.User? user, LibraryRootScope? scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode], AncestorIds = [seriesId], IsFolder = false, User = user
        };
        if (user is not null)
        {
            _libraryManager.ConfigureUserAccess(query, user);
        }

        var episodes = _libraryManager.GetItemList(query);

        // Recheck each episode's own scope and user state, then prefer regular episodes over specials.
        var first = episodes
            .OfType<Episode>()
            .Where(e =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return IsEligibleAtDelivery(e, user, scope);
            })
            .OrderBy(e => e.ParentIndexNumber.GetValueOrDefault() <= 0 ? 1 : 0) // Deprioritize season 0 / specials
            .ThenBy(e => e.ParentIndexNumber.GetValueOrDefault() <= 0 ? int.MaxValue : e.ParentIndexNumber!.Value)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .FirstOrDefault();

        return first?.Id;
    }

    private bool IsEligibleAtDelivery(BaseItem item, Jellyfin.Database.Implementations.Entities.User? user, LibraryRootScope? scope)
        => IsAccessibleMediaAtDelivery(item, user, scope) && (user is null || _userDataManager is null
            || IsUnseenUserData(_userDataManager.GetUserData(user, item)));

    private static bool IsUnseenUserData(UserItemData? data)
        => data is { Played: false, PlayCount: <= 0, PlaybackPositionTicks: <= 0, IsFavorite: false }
            && !(data.Rating is > 0 && double.IsFinite(data.Rating.Value));

    private Guid[] RecheckResolvedItemsAtCreation(Guid[] itemIds, Jellyfin.Database.Implementations.Entities.User owner, CancellationToken cancellationToken)
    {
        var excluded = CleanupConfigHelper.ParseCommaSeparated(_configService?.GetConfiguration().ExcludedLibraries ?? string.Empty);
        var scope = excluded.Count > 0 ? LibraryPathResolver.GetLibraryRootScope(_libraryManager, excluded) : null;
        var permitted = LibraryUserAccess.GetAllowedIds(_libraryManager, owner, itemIds);
        return itemIds.Where(id =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return permitted.Contains(id) && _libraryManager.GetItemById(id) is { } item && IsEligibleAtDelivery(item, owner, scope);
        }).ToArray();
    }

    private bool IsAccessibleMediaAtDelivery(BaseItem item, Jellyfin.Database.Implementations.Entities.User? user, LibraryRootScope? scope)
    {
        if (item.IsVirtualItem || string.IsNullOrEmpty(item.Path) || _virtualItems?.IsVirtual(item) == true
            || (scope is not null && !LibraryPathResolver.IsAllowed(item.Path, scope)))
        {
            return false;
        }

        if (user is null)
        {
            return true;
        }

        if (user.MaxParentalRatingScore is { } maximum
            && (!item.InheritedParentalRatingValue.HasValue || item.InheritedParentalRatingValue.Value > maximum))
        {
            return false;
        }

        return true;
    }

    private (Dictionary<int, Movie> Unseen, HashSet<int> Completed) LoadSequenceMovies(
        Jellyfin.Database.Implementations.Entities.User user,
        LibraryRootScope? scope,
        CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], User = user, Recursive = true };
        _libraryManager.ConfigureUserAccess(query, user);
        var movies = _libraryManager.GetItemList(query);
        var unseen = new Dictionary<int, Movie>();
        var completed = new HashSet<int>();
        var engaged = new HashSet<int>();
        foreach (var movie in movies.OfType<Movie>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TmdbLibraryMapper.TryGetTmdbId(movie, out var id) || !MovieSequencePolicy.IsKnownMovieId(id) || !IsAccessibleMediaAtDelivery(movie, user, scope))
            {
                continue;
            }

            var data = _userDataManager!.GetUserData(user, movie);
            if (data is not null && !IsUnseenUserData(data))
            {
                engaged.Add(id);
            }

            if (data?.Played == true)
            {
                completed.Add(id);
            }
            else if (IsUnseenUserData(data))
            {
                unseen.TryAdd(id, movie);
            }
        }

        foreach (var id in engaged)
        {
            unseen.Remove(id);
        }

        return (unseen, completed);
    }

    /// <summary>
    ///     Finds and removes all recommendation playlists owned by the specified user. Identifies managed playlists by the PlaylistNamePrefix prefix.
    /// </summary>
    /// <param name="userId">The user ID whose playlists to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of playlists removed.</returns>
    private int RemoveUserPlaylists(Guid userId, CancellationToken cancellationToken)
    {
        return RemoveUserPlaylistsExcept(userId, excludePlaylistId: null, cancellationToken);
    }

    /// <summary>
    ///     Finds and removes recommendation playlists, optionally excluding one.
    ///     ILibraryManager.DeleteItem is synchronous; no async overload exists.
    /// </summary>
    private int RemoveUserPlaylistsExcept(
        Guid userId,
        string? excludePlaylistId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return 0;
        }

        var expectedName = BuildPlaylistName(user.Username);

        // Load ALL playlists visible to this user without SearchTerm filtering.
        var existingPlaylists = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist],
                User = user
            });

        var removed = 0;
        Guid? parsedExcludeId = excludePlaylistId is not null && Guid.TryParse(excludePlaylistId, out var tempId) ? tempId : (Guid?)null;
        foreach (var playlist in existingPlaylists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (playlist is MediaBrowser.Controller.Playlists.Playlist owned && owned.OwnerUserId != Guid.Empty && owned.OwnerUserId != userId)
            {
                continue;
            }

            if (!IsManagedPlaylistName(playlist, expectedName) && !HasManagedOwner(playlist, userId))
            {
                continue;
            }

            // Skip the just-created replacement playlist
            if (parsedExcludeId is not null && playlist.Id == parsedExcludeId.Value)
            {
                continue;
            }

            if (TryRemovePlaylist(playlist, userId))
            {
                removed++;
            }
        }

        return removed;
    }

    private async Task MarkManagedPlaylistAsync(string playlistId, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            if (!Guid.TryParse(playlistId, out var id) || _libraryManager.GetItemById(id) is not MediaBrowser.Controller.Playlists.Playlist playlist
                || playlist.OwnerUserId != userId)
            {
                return;
            }

            var parent = _playlistManager.GetPlaylistsFolder(userId) ?? _playlistManager.GetPlaylistsFolder();
            if (parent is null)
            {
                return;
            }

            playlist.ProviderIds[ManagedOwnerProviderKey] = userId.ToString("N");
            await _libraryManager.UpdateItemAsync(playlist, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            _playlistManager.SavePlaylistFile(playlist);
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogWarning(LogCategory, "Could not persist the recommendation playlist's ownership marker.", ex, _logger);
        }
    }

    private int RemoveIdentifiedInactivePlaylists(HashSet<Guid> liveUsers, HashSet<Guid> disabledUsers, CancellationToken cancellationToken, bool removeAll = false)
    {
        var removed = 0;
        try
        {
            var playlists = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Playlist], Recursive = true });
            foreach (var playlist in playlists.OfType<MediaBrowser.Controller.Playlists.Playlist>().DistinctBy(item => item.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owner = playlist.OwnerUserId;
                if (HasManagedOwner(playlist, owner) && (removeAll || !liveUsers.Contains(owner) || disabledUsers.Contains(owner))
                    && (removeAll || IsVerifiedInactiveOwner(owner))
                    && TryRemovePlaylist(playlist, owner))
                {
                    removed++;
                }
            }
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogWarning(LogCategory, "Could not inspect identified recommendation playlists for inactive owners.", ex, _logger);
        }

        return removed;
    }

    private bool IsVerifiedInactiveOwner(Guid owner)
    {
        try
        {
            var user = _userManager.GetUserById(owner);
            return user is null || IsDisabled(user);
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogDebug(LogCategory, "Playlist owner lookup unavailable; preserving that playlist.", _logger);
            return false;
        }
    }

    private static bool HasManagedOwner(BaseItem item, Guid owner)
        => owner != Guid.Empty && item is MediaBrowser.Controller.Playlists.Playlist playlist && playlist.OwnerUserId == owner
            && playlist.ProviderIds.TryGetValue(ManagedOwnerProviderKey, out var marker) && Guid.TryParse(marker, out var recordedOwner) && recordedOwner == owner;

    /// <summary>
    ///     Returns true when the playlist name is one of ours: an exact match to , or that name plus a numeric dedupe suffix.
    /// </summary>
    /// <param name="playlist">The candidate playlist item.</param>
    /// <param name="expectedName">The managed playlist name for this user.</param>
    /// <returns><c>true</c> when the playlist is a managed one.</returns>
    private static bool IsManagedPlaylistName(BaseItem playlist, string expectedName)
    {
        // Match our managed playlists by exact name or exact name + numeric dedupe suffix (e.g. "🎬 Recommended for Alice1").
        return playlist.Name is not null
            && (
                string.Equals(playlist.Name, expectedName, StringComparison.Ordinal)
                || (
                    playlist.Name.StartsWith(expectedName, StringComparison.Ordinal)
                    && playlist.Name.Length > expectedName.Length
                    && playlist.Name[expectedName.Length..].All(char.IsDigit)
                )
            );
    }

    /// <summary>
    ///     Deletes a single managed playlist resiliently, falling back to a DB-only delete plus best-effort folder removal when the file-location delete throws.
    /// </summary>
    /// <param name="playlist">The playlist to remove.</param>
    /// <param name="userId">The owning user id (for logging).</param>
    /// <returns><c>true</c> when the playlist was removed (directly or via fallback).</returns>
    private bool TryRemovePlaylist(BaseItem playlist, Guid userId)
    {
        // Delete the playlist resiliently. Two hardening points learned from an orphaned-folder.
        try
        {
            _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true });
            _pluginLog.LogDebug(
                LogCategory,
                $"Removed old playlist '{playlist.Name}' (ID: {playlist.Id}) for user {userId}.",
                _logger);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            var fellBack = false;
            try
            {
                // Remove the DB item without touching the file location, so it can't be
                // re-imported; then best-effort remove the orphaned folder.
                _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = false });
                fellBack = true;
                var path = playlist.Path;

                // playlist.Path is DB-sourced and, when a playlist's on-disk folder has drifted, may not point where we expect.
                var playlistsRoot = _playlistManager.GetPlaylistsFolder()?.Path;
                if (!string.IsNullOrEmpty(path)
                    && !string.IsNullOrEmpty(playlistsRoot)
                    && PathValidator.IsSafePath(path, playlistsRoot, _pluginLog)
                    && !string.Equals(
                        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(playlistsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    _pluginLog.LogWarning(
                        LogCategory,
                        $"Skipped recursive delete of playlist folder '{path}' (ID: {playlist.Id}): "
                        + "path is outside the playlists root or is a sensitive location.",
                        logger: _logger);
                }
            }
            catch (Exception inner) when (!inner.IsFatal() && inner is not OperationCanceledException)
            {
                _pluginLog.LogWarning(
                    LogCategory,
                    $"Fallback delete for playlist '{playlist.Name}' (ID: {playlist.Id}) partially failed.",
                    inner,
                    _logger);
            }

            if (fellBack)
            {
                _pluginLog.LogInfo(
                    LogCategory,
                    $"Removed old playlist '{playlist.Name}' (ID: {playlist.Id}) via DB-item fallback (file location was not deletable).",
                    _logger);
                return true;
            }

            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to remove playlist '{playlist.Name}' (ID: {playlist.Id}) for user {userId}.",
                ex,
                _logger);
            return false;
        }
    }
}
