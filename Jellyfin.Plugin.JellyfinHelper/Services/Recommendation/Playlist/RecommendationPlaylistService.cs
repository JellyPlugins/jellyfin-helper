using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>
///     Creates and manages per-user recommendation playlists in Jellyfin.
///     Playlists are identified by a well-known name prefix so they can be
///     found and replaced on each scheduled run.
/// </summary>
public sealed class RecommendationPlaylistService : IRecommendationPlaylistService
{
    /// <summary>
    ///     The prefix used to identify recommendation playlists managed by this plugin.
    ///     This prefix is used for searching existing playlists to delete before recreation.
    ///     The full playlist name includes a dynamic date suffix.
    /// </summary>
    internal const string PlaylistNamePrefix = "🎬 Recommended";

    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<RecommendationPlaylistService> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationPlaylistService"/> class.
    /// </summary>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public RecommendationPlaylistService(
        IPlaylistManager playlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<RecommendationPlaylistService> logger)
    {
        _playlistManager = playlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PlaylistSyncResult> UpdatePlaylistsForAllUsersAsync(
        IReadOnlyList<RecommendationResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        var syncResult = new PlaylistSyncResult();

        _pluginLog.LogInfo(
            "PlaylistSync",
            $"Starting playlist sync for {results.Count} users.",
            _logger);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Skip playlist creation if there are no recommendations
                if (result.Recommendations.Count == 0)
                {
                    // Still clean up old playlists when there are no new recommendations
                    var removedEmpty = await RemoveUserPlaylistsAsync(result.UserId, cancellationToken).ConfigureAwait(false);
                    syncResult.OldPlaylistsRemoved += removedEmpty;

                    _pluginLog.LogDebug(
                        "PlaylistSync",
                        $"No recommendations for user '{result.UserName}' — skipping playlist creation.",
                        _logger);
                    continue;
                }

                // Create new playlist with items in score-ranked order.
                // Series items are resolved to their first episode to prevent Jellyfin's
                // PlaylistManager from expanding the entire series into individual episodes.
                // Items that cannot be resolved (e.g., empty series) are skipped,
                // so we pass the full list and let the resolver collect up to maxResults playable items.
                var itemIds = ResolvePlaylistItemIds(result.Recommendations, result.Recommendations.Count);

                if (itemIds.Length == 0)
                {
                    // Clean up stale playlists when no playable items resolve,
                    // so users don't keep seeing outdated recommendations.
                    var removedStale = await RemoveUserPlaylistsAsync(result.UserId, cancellationToken).ConfigureAwait(false);
                    syncResult.OldPlaylistsRemoved += removedStale;

                    _pluginLog.LogDebug(
                        "PlaylistSync",
                        $"No playable items resolved for user '{result.UserName}' — skipping playlist creation.",
                        _logger);
                    continue;
                }

                // Build a personalized playlist name per user to avoid filesystem name collisions
                var playlistName = BuildPlaylistName(result.UserName);

                var request = new PlaylistCreationRequest
                {
                    Name = playlistName,
                    UserId = result.UserId,
                    ItemIdList = itemIds,
                    MediaType = MediaType.Unknown // Mixed content (movies + series)
                };

                // Create the new playlist BEFORE removing old ones so the user is never left
                // without a recommendation playlist if creation fails.
                var playlistResult = await _playlistManager.CreatePlaylist(request).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(playlistResult.Id))
                {
                    // New playlist created — now safe to remove old playlists.
                    var removed = await RemoveUserPlaylistsExceptAsync(
                        result.UserId, playlistResult.Id, cancellationToken).ConfigureAwait(false);
                    syncResult.OldPlaylistsRemoved += removed;

                    syncResult.PlaylistsCreated++;
                    syncResult.TotalItemsAdded += itemIds.Length;

                    _pluginLog.LogDebug(
                        "PlaylistSync",
                        $"Created playlist '{playlistName}' for user '{result.UserName}' with {itemIds.Length} items.",
                        _logger);
                }
                else
                {
                    syncResult.PlaylistsFailed++;
                    _pluginLog.LogWarning(
                        "PlaylistSync",
                        $"Playlist creation returned empty ID for user '{result.UserName}'.",
                        logger: _logger);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                syncResult.PlaylistsFailed++;
                _pluginLog.LogWarning(
                    "PlaylistSync",
                    $"Failed to sync playlist for user '{result.UserName}'.",
                    ex,
                    _logger);
            }
        }

        _pluginLog.LogInfo(
            "PlaylistSync",
            $"Playlist sync complete: {syncResult.PlaylistsCreated} created, {syncResult.OldPlaylistsRemoved} old removed, {syncResult.PlaylistsFailed} failed, {syncResult.TotalItemsAdded} total items.",
            _logger);

        return syncResult;
    }

    /// <inheritdoc />
    public async Task<int> RemoveAllRecommendationPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var totalRemoved = 0;
        var users = _userManager.Users.ToList();

        _pluginLog.LogInfo(
            "PlaylistSync",
            $"Removing all recommendation playlists for {users.Count} users...",
            _logger);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var removed = await RemoveUserPlaylistsAsync(user.Id, cancellationToken).ConfigureAwait(false);
                totalRemoved += removed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _pluginLog.LogWarning(
                    "PlaylistSync",
                    $"Failed to remove playlists for user '{user.Username}'.",
                    ex,
                    _logger);
            }
        }

        _pluginLog.LogInfo(
            "PlaylistSync",
            $"Removed {totalRemoved} recommendation playlists.",
            _logger);

        return totalRemoved;
    }

    /// <summary>
    ///     Builds the playlist name personalized with the user's display name.
    ///     This ensures each user gets a uniquely named playlist on disk,
    ///     preventing Jellyfin from auto-suffixing duplicate folder names.
    ///     Example: "🎬 Recommended for Alice".
    /// </summary>
    /// <param name="userName">The user's display name.</param>
    /// <returns>The full playlist name.</returns>
    internal static string BuildPlaylistName(string userName)
    {
        return PlaylistNamePrefix + " for " + (string.IsNullOrWhiteSpace(userName) ? "you" : userName);
    }

    /// <summary>
    ///     Resolves recommendation item IDs into playable playlist item IDs.
    ///     <para>
    ///         Jellyfin's <see cref="IPlaylistManager"/> expands container items (Series, Seasons)
    ///         into all their child episodes when added to a playlist. This means adding a Series ID
    ///         results in every single episode appearing individually in the playlist.
    ///     </para>
    ///     <para>
    ///         To prevent this, Series recommendations are resolved to their first episode (S01E01).
    ///         This gives the user a single representative entry per series that they can navigate
    ///         from, rather than flooding the playlist with hundreds of episodes.
    ///     </para>
    ///     <para>
    ///         Movies and other non-series items are passed through unchanged.
    ///         Items that cannot be resolved (empty series, missing media) are skipped.
    ///         The method iterates through the full ranked list until <paramref name="maxItems"/>
    ///         playable items have been collected, ensuring the playlist always reaches the
    ///         desired count when enough candidates are available.
    ///     </para>
    /// </summary>
    /// <param name="recommendations">The score-ranked recommendations to resolve.</param>
    /// <param name="maxItems">Maximum number of playable items to collect.</param>
    /// <returns>An array of playable item IDs suitable for playlist creation.</returns>
    internal Guid[] ResolvePlaylistItemIds(IEnumerable<RecommendedItem> recommendations, int maxItems)
    {
        var resolvedIds = new List<Guid>();
        var skippedCount = 0;

        foreach (var rec in recommendations)
        {
            if (resolvedIds.Count >= maxItems)
            {
                break;
            }

            if (string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve series to its first episode to avoid playlist explosion.
                // Query for the first episode (sorted by season/episode index) of this series.
                var firstEpisode = ResolveFirstEpisodeForSeries(rec.ItemId);

                if (firstEpisode.HasValue)
                {
                    resolvedIds.Add(firstEpisode.Value);
                    _pluginLog.LogDebug(
                        "PlaylistSync",
                        $"Resolved series '{rec.Name}' to first episode (ID: {firstEpisode.Value}).",
                        _logger);
                }
                else
                {
                    // Skip unresolvable series and let the loop pick the next candidate.
                    skippedCount++;
                    _pluginLog.LogDebug(
                        "PlaylistSync",
                        $"Could not resolve first episode for series '{rec.Name}' (ID: {rec.ItemId}) — skipping, will backfill.",
                        _logger);
                }
            }
            else
            {
                // Movies and other playable items — use directly
                resolvedIds.Add(rec.ItemId);
            }
        }

        if (skippedCount > 0)
        {
            _pluginLog.LogInfo(
                "PlaylistSync",
                $"Skipped {skippedCount} unresolvable items during playlist resolution. Resolved {resolvedIds.Count}/{maxItems} items.",
                _logger);
        }

        return resolvedIds.ToArray();
    }

    /// <summary>
    ///     Finds the first episode of a series by querying the library for all episodes
    ///     belonging to the given series ID, then sorting by season and episode index in memory.
    ///     In-memory sorting is used because Jellyfin's <c>InternalItemsQuery</c> does not
    ///     reliably support <c>OrderBy</c> with <c>ParentIndexNumber</c> across all database backends.
    /// </summary>
    /// <param name="seriesId">The Jellyfin series item ID.</param>
    /// <returns>The ID of the first episode, or null if no episodes exist.</returns>
    internal Guid? ResolveFirstEpisodeForSeries(Guid seriesId)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            IsFolder = false
        });

        // Filter to real Episode objects and deprioritize specials (season 0) so the playlist
        // resolves to an actual playable pilot episode (e.g. S01E01) rather than S00E01.
        // The query already uses IsFolder=false which excludes container items.
        var first = episodes
            .OfType<Episode>()
            .OrderBy(e => e.ParentIndexNumber.GetValueOrDefault() <= 0 ? 1 : 0) // Deprioritize season 0 / specials
            .ThenBy(e => e.ParentIndexNumber.GetValueOrDefault() <= 0 ? int.MaxValue : e.ParentIndexNumber!.Value)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .FirstOrDefault();

        return first?.Id;
    }

    /// <summary>
    ///     Finds and removes all recommendation playlists owned by the specified user.
    ///     Identifies managed playlists by the <see cref="PlaylistNamePrefix"/> prefix.
    /// </summary>
    /// <param name="userId">The user ID whose playlists to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of playlists removed.</returns>
    private Task<int> RemoveUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return RemoveUserPlaylistsExceptAsync(userId, excludePlaylistId: null, cancellationToken);
    }

    /// <summary>
    ///     Finds and removes recommendation playlists, optionally excluding one.
    /// </summary>
    private Task<int> RemoveUserPlaylistsExceptAsync(
        Guid userId, string? excludePlaylistId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Task.FromResult(0);
        }

        var expectedName = BuildPlaylistName(user.Username);

        // Load ALL playlists visible to this user without SearchTerm filtering.
        // Jellyfin's search index does not reliably match Unicode characters (emoji prefix),
        // which caused old playlists to survive deletion and accumulate with suffixed names
        // like "Recommended for you1", "Recommended for you11", etc.
        var existingPlaylists = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Playlist],
            User = user
        });

        var removed = 0;
        foreach (var playlist in existingPlaylists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Match our managed playlists by exact name or exact name + numeric dedupe suffix
            // (e.g. "🎬 Recommended for Alice1"). Using StartsWith alone would match
            // "🎬 Recommended for Al" against "🎬 Recommended for Alice", potentially
            // deleting another user's playlist.
            var isManagedName =
                playlist.Name is not null
                && (
                    string.Equals(playlist.Name, expectedName, StringComparison.Ordinal)
                    || (
                        playlist.Name.StartsWith(expectedName, StringComparison.Ordinal)
                        && playlist.Name.Length > expectedName.Length
                        && playlist.Name[expectedName.Length..].All(char.IsDigit)
                    )
                );

            if (!isManagedName)
            {
                continue;
            }

            // Skip the just-created replacement playlist
            if (excludePlaylistId is not null
                && string.Equals(playlist.Id.ToString("N"), excludePlaylistId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true });
            removed++;

            _pluginLog.LogDebug(
                "PlaylistSync",
                $"Removed old playlist '{playlist.Name}' (ID: {playlist.Id}) for user {userId}.",
                _logger);
        }

        return Task.FromResult(removed);
    }
}
