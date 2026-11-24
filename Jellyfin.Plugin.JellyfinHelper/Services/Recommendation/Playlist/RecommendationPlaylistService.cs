using System;
using System.Collections.Concurrent;
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
using MediaBrowser.Controller.Persistence;
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

    /// <summary>Durable negative provenance for administrator-preserved ambiguous IDs, independent of playlist names.</summary>
    internal const string PreservedPlaylistProviderKey = "JellyfinHelperPreservedPlaylist";

    /// <summary>Attempt-specific creation provenance; a reused ID and owner do not carry this marker.</summary>
    internal const string CreationAttemptProviderKey = "JellyfinHelperPlaylistAttempt";

    private const string LogCategory = "PlaylistSync";
    private const int MaximumCreationRecoveryPasses = 4;

    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<RecommendationPlaylistService> _logger;
    private readonly IUserDataManager? _userDataManager;
    private readonly IPluginConfigurationService? _configService;
    private readonly VirtualItemDetector? _virtualItems;
    private readonly ILinkedChildrenService? _linkedChildren;
    private readonly Func<Guid, IReadOnlyList<BaseItem>>? _ownedPlaylists;
    private readonly ConcurrentDictionary<Guid, Guid> _unmarkedReplacements = new();
    private readonly ConcurrentDictionary<Guid, CreationProof> _creationProofs = new();
    private readonly AsyncLocal<Guid?> _creationExecution = new();
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _uncertainCreations = new();
    private readonly PlaylistOwnerOperations _ownerOperations = new();
    private readonly Lock _recoveryInitializationGate = new();
    private PlaylistCreationJournal? _creationJournal;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationPlaylistService"/> class.
    /// </summary>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="userDataManager">Current Jellyfin playback state for final delivery checks.</param>
    /// <param name="libraryServices">Current library configuration and virtual-media services.</param>
    public RecommendationPlaylistService(
        IPlaylistManager playlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<RecommendationPlaylistService> logger,
        IUserDataManager? userDataManager = null,
        PlaylistLibraryServices? libraryServices = null)
    {
        _playlistManager = playlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
        _userDataManager = userDataManager;
        _configService = libraryServices?.Configuration;
        _virtualItems = libraryServices?.VirtualItems;
        _linkedChildren = libraryServices?.LinkedChildren;
        _ownedPlaylists = libraryServices?.OwnedPlaylists;
        RecoveryDirectory = libraryServices?.RecoveryDirectory ?? (Plugin.Instance?.DataFolderPath is { } data ? Path.Join(data, "jellyfin-helper-playlist-recovery") : null);
    }

    private bool VerifyLiveDelivery => _userDataManager is not null || _configService is not null || _virtualItems is not null;

    /// <summary>Gets the durable attempt directory; tests provide a per-fixture path rather than shared plugin state.</summary>
    internal string? RecoveryDirectory { get; init; }

    /// <summary>Gets the durable creation-evidence write boundary.</summary>
    internal Action<string, string> WriteCreationState { get; init; } = (path, contents) => AtomicFile.WriteAllText(path, contents);

    /// <summary>Gets the durable creation-evidence completion boundary.</summary>
    internal Action<string> DeleteCreationState { get; init; } = File.Delete;

    /// <summary>Gets the fully reserved creation budget; smaller overrides exercise admission boundaries.</summary>
    internal long CreationJournalByteBudget { get; init; } = PlaylistCreationJournal.MaximumJournalBytes;

    private PlaylistCreationJournal EnsureCreationRecovery()
    {
        lock (_recoveryInitializationGate)
        {
            if (_creationJournal is not null)
            {
                return _creationJournal;
            }

            var directory = RecoveryDirectory ?? throw new IOException("A durable playlist creation recovery directory is required.");
            var journal = new PlaylistCreationJournal(directory, WriteCreationState, DeleteCreationState, byteBudget: CreationJournalByteBudget);
            foreach (var attempt in journal.Pending())
            {
                _uncertainCreations.TryAdd(attempt.Owner, attempt.BeforeIds.ToHashSet());
                if (attempt.ReturnedId is { } id && !attempt.Marked)
                {
                    _unmarkedReplacements.TryAdd(id, attempt.Owner);
                }
            }

            _creationJournal = journal;
            return journal;
        }
    }

    /// <inheritdoc />
    public async Task<PlaylistSyncResult> UpdatePlaylistsForAllUsersAsync(
        IReadOnlyList<RecommendationResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCreationRecovery();

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

            await SyncOneOwnerAsync(result, syncResult, disabledUserIds, handledDisabledUserIds, cancellationToken).ConfigureAwait(false);
        }

        disabledUserIds ??= [];

        RemoveStalePlaylistsForDisabledUsers(disabledUserIds, handledDisabledUserIds, syncResult, cancellationToken);
        if (liveUserIds is not null)
        {
            ClearInactiveUnmarkedReplacements(liveUserIds, disabledUserIds, cancellationToken);
            syncResult.OldPlaylistsRemoved += RemoveIdentifiedInactivePlaylists(liveUserIds, disabledUserIds, cancellationToken);
        }

        _pluginLog.LogInfo(
            LogCategory,
            $"Playlist sync complete: {syncResult.PlaylistsCreated} created, {syncResult.OldPlaylistsRemoved} old removed, {syncResult.PlaylistsFailed} failed, {syncResult.TotalItemsAdded} total items.",
            _logger);

        return syncResult;
    }

    private async Task SyncOneOwnerAsync(RecommendationResult result, PlaylistSyncResult syncResult, HashSet<Guid>? disabledUsers, HashSet<Guid> handledDisabledUsers, CancellationToken cancellationToken)
    {
        try
        {
            using var lease = await _ownerOperations.EnterAsync(result.UserId, cancellationToken).ConfigureAwait(false);
            if (disabledUsers?.Contains(result.UserId) == true)
            {
                SyncDisabledUserPlaylist(result, syncResult, handledDisabledUsers, cancellationToken);
            }
            else if (IsVerifiedEnabledUser(result, handledDisabledUsers, syncResult, cancellationToken))
            {
                await SyncEnabledUserPlaylistAsync(result, syncResult, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!exception.IsFatal() && exception is not OperationCanceledException)
        {
            syncResult.PlaylistsFailed++;
            _pluginLog.LogWarning(LogCategory, "Playlist owner lifecycle is unavailable; preserving existing playlists.", exception, _logger);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ResolveDeferredCreationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A playlist owner identity is required.", nameof(userId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EnsureCreationRecovery();
            using var lease = await _ownerOperations.EnterAsync(userId, cancellationToken).ConfigureAwait(false);
            if (!_uncertainCreations.TryGetValue(userId, out var before))
            {
                return false;
            }

            var inspected = before.ToHashSet();
            var attempt = EnsureCreationRecovery().Get(userId);
            if (attempt?.ReturnedId is { } returned && HasCreationProof(returned, userId))
            {
                inspected.Add(returned);
            }

            for (var pass = 0; pass < MaximumCreationRecoveryPasses; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = GetOwnedPlaylistIds(userId);
                if (current.IsSubsetOf(inspected))
                {
                    // The final ownership read has no asynchronous handoff before
                    // conditional release. Newly registered IDs force another pass.
                    return CompleteCreationAttempt(userId, before, current);
                }

                if (!await PreserveNewOwnedPlaylistsAsync(userId, current, inspected, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            _pluginLog.LogWarning(LogCategory, "Playlist ownership did not stabilize within the recovery budget; retaining the ambiguous creation gate.", logger: _logger);
            return false;
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogWarning(LogCategory, "Ambiguous playlist creation could not be released safely; existing playlists remain preserved.", ex, _logger);
            return false;
        }
    }

    private async Task<bool> PreserveNewOwnedPlaylistsAsync(Guid userId, HashSet<Guid> current, HashSet<Guid> inspected, CancellationToken cancellationToken)
    {
        foreach (var id in current.Except(inspected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((!_unmarkedReplacements.TryGetValue(id, out var receiptOwner) || receiptOwner != userId || !HasCreationProof(id, userId))
                && !await PreserveAmbiguousPlaylistAsync(id, userId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            inspected.Add(id);
        }

        return true;
    }

    private async Task<bool> PreserveAmbiguousPlaylistAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(id) is not MediaBrowser.Controller.Playlists.Playlist playlist || playlist.OwnerUserId != userId)
        {
            return false;
        }

        var parent = _playlistManager.GetPlaylistsFolder(userId) ?? _playlistManager.GetPlaylistsFolder();
        if (parent is null)
        {
            return false;
        }

        // Persist the ID-specific no-delete decision before releasing the gate.
        // It survives restarts and cannot be copied to protect a different ID.
        var previous = playlist.ProviderIds.GetValueOrDefault(PreservedPlaylistProviderKey);
        var saved = false;
        try
        {
            playlist.ProviderIds[PreservedPlaylistProviderKey] = id.ToString("N");
            await _libraryManager.UpdateItemAsync(playlist, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            saved = true;
            return true;
        }
        finally
        {
            if (!saved)
            {
                RestoreProviderId(playlist, PreservedPlaylistProviderKey, previous);
            }
        }
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
            var removedDisabled = RemoveUserPlaylists(result.UserId, cancellationToken, onlyIfDisabled: true);
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
            if (!CanCreateReplacement(result.UserId))
            {
                syncResult.PlaylistsFailed++;
                return;
            }

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
            var user = GetEnabledOwner(result.UserId);
            if (user is null)
            {
                return;
            }

            var excluded = CleanupConfigHelper.ParseCommaSeparated(_configService?.GetConfiguration().ExcludedLibraries ?? string.Empty);
            var scope = excluded.Count > 0 ? LibraryPathResolver.GetLibraryRootScope(_libraryManager, excluded) : null;
            var originatingItems = new List<(Guid Resolved, Guid Original)>();
            var itemIds = ResolvePlaylistItemIds(result.Recommendations, result.Recommendations.Count, user, scope, originatingItems, cancellationToken);

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
            var beforeCreation = GetOwnedPlaylistIds(result.UserId);
            var legacyCleanupIds = beforeCreation.Where(id => EnsureCreationRecovery().AllowsLegacyDeletion(result.UserId, id)).ToHashSet();
            var ownerAtCreation = GetEnabledOwner(result.UserId);
            if (ownerAtCreation is null)
            {
                return;
            }

            if (VerifyLiveDelivery)
            {
                itemIds = RecheckResolvedItemsAtCreation(itemIds, ownerAtCreation, originatingItems, cancellationToken);
            }

            if (itemIds.Length == 0)
            {
                syncResult.OldPlaylistsRemoved += RemoveUserPlaylists(result.UserId, cancellationToken);
                return;
            }

            request.ItemIdList = itemIds;
            var playlistResult = await CreateReplacementAsync(request, beforeCreation, originatingItems, cancellationToken).ConfigureAwait(false);
            if (playlistResult is null)
            {
                syncResult.OldPlaylistsRemoved += RemoveUserPlaylists(result.UserId, cancellationToken);
                return;
            }

            itemIds = request.ItemIdList.ToArray();

            var addedItems = ReadPersistedReplacementItems(Guid.Parse(playlistResult.Id), result.UserId, itemIds);
            if (addedItems == 0)
            {
                syncResult.PlaylistsFailed++;
                RememberUnmarkedReplacement(playlistResult.Id, result.UserId);
                ClearUnmarkedReplacements(result.UserId);
                return;
            }

            // CreateReplacementAsync returns only a verified new identity.
            if (!await MarkReplacementAsync(playlistResult.Id, result.UserId, cancellationToken).ConfigureAwait(false))
            {
                // Keep the incumbent until replacement ownership is durable. Only
                // the returned, owner-verified replacement may be rolled back.
                syncResult.PlaylistsFailed++;
                return;
            }

            PersistReplacementMark(result.UserId, Guid.Parse(playlistResult.Id));
            if (!CanReconcileUncertainCreation(result.UserId))
            {
                syncResult.PlaylistsFailed++;
                return;
            }

            // New playlist created - now safe to remove old playlists.
            var removed = RemoveUserPlaylistsExcept(result.UserId, playlistResult.Id, cancellationToken, legacyDeletionIds: legacyCleanupIds);
            syncResult.OldPlaylistsRemoved += removed;
            syncResult.PlaylistsCreated++;
            syncResult.TotalItemsAdded += addedItems;

            _pluginLog.LogDebug(
                LogCategory,
                $"Created playlist '{playlistName}' for user '{result.UserName}' with {addedItems} items.",
                _logger);
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

    private void PersistReplacementMark(Guid owner, Guid replacement)
    {
        var journal = EnsureCreationRecovery();
        var attempt = journal.Get(owner) ?? throw new IOException("The marked replacement has no durable creation attempt.");
        try
        {
            journal.RecordMarked(owner, attempt.Id);
        }
        catch
        {
            _unmarkedReplacements.TryAdd(replacement, owner);
            throw;
        }

        ((ICollection<KeyValuePair<Guid, Guid>>)_unmarkedReplacements).Remove(new(replacement, owner));
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
                using var lease = _ownerOperations.TryEnter(disabledUserId);
                if (lease is null)
                {
                    continue;
                }

                syncResult.OldPlaylistsRemoved += RemoveUserPlaylists(disabledUserId, cancellationToken, onlyIfDisabled: true);
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
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCreationRecovery();
        var totalRemoved = 0;
        var users = _userManager.GetUsers().ToList();
        foreach (var receipt in _unmarkedReplacements.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = await _ownerOperations.EnterAsync(receipt.Value, cancellationToken).ConfigureAwait(false);
            if (ClearUnmarkedReplacement(receipt).Deleted)
            {
                totalRemoved++;
            }
        }

        _pluginLog.LogInfo(
            LogCategory,
            $"Removing all recommendation playlists for {users.Count} users...",
            _logger);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var lease = await _ownerOperations.EnterAsync(user.Id, cancellationToken).ConfigureAwait(false);
                var removed = RemoveUserPlaylists(user.Id, cancellationToken, removeAll: true);
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
        => ResolvePlaylistItemIds(recommendations, maxItems, null, null, null, CancellationToken.None);

    private Guid[] ResolvePlaylistItemIds(
        IEnumerable<RecommendedItem> recommendations,
        int maxItems,
        Jellyfin.Database.Implementations.Entities.User? user,
        LibraryRootScope? scope,
        List<(Guid Resolved, Guid Original)>? originatingItems,
        CancellationToken cancellationToken)
    {
        var resolvedIds = new List<Guid>();
        var uniqueIds = new HashSet<Guid>();
        var uniqueOrigins = new HashSet<Guid>();
        var skippedCount = 0;
        var snapshot = recommendations.ToArray();
        var verifyLive = VerifyLiveDelivery;
        var permitted = user is not null && verifyLive
            ? LibraryUserAccess.GetAllowedIds(_libraryManager, user, snapshot.Select(item => item.ItemId).Distinct().ToArray())
            : null;
        var sequenceMovies = new Lazy<MovieSequenceSnapshot>(() => LoadSequenceMovies(user!, scope, cancellationToken));

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

            var resolved = ResolvePlaylistItem(rec, new DeliveryContext(user, scope, verifyLive, sequenceMovies), cancellationToken);
            if (resolved.HasValue && originatingItems is not null && uniqueOrigins.Add(rec.ItemId))
            {
                originatingItems.Add((resolved.Value, rec.ItemId));
            }

            if (resolved.HasValue && uniqueIds.Add(resolved.Value))
            {
                resolvedIds.Add(resolved.Value);
            }
            else
            {
                skippedCount++;
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

    private Guid? ResolvePlaylistItem(RecommendedItem recommendation, DeliveryContext context, CancellationToken cancellationToken)
    {
        var live = context.VerifyLive ? _libraryManager.GetItemById(recommendation.ItemId) : null;
        if (context.VerifyLive && (live is null || !IsEligibleAtDelivery(live, context.User, context.Scope)))
        {
            return null;
        }

        if (string.Equals(recommendation.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveFirstEpisodeForSeries(recommendation.ItemId, context.User, context.Scope, cancellationToken);
        }

        return live is Movie movie ? ResolveMovieAtDelivery(movie, context) : recommendation.ItemId;
    }

    private Guid? ResolveMovieAtDelivery(Movie movie, DeliveryContext context)
    {
        if (context.User is null || _userDataManager is null || !TmdbLibraryMapper.TryGetTmdbId(movie, out var movieId)
            || !MovieSequencePolicy.IsKnownMovieId(movieId))
        {
            return movie.Id;
        }

        var sequence = context.SequenceMovies.Value;
        var required = MovieSequencePolicy.RequiredMovie(movieId, sequence.CollectionIds.GetValueOrDefault(movie.Id), sequence.Completed);
        return required == movieId ? movie.Id : sequence.Unseen.GetValueOrDefault(required)?.Id;
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
        var query = user is null ? new InternalItemsQuery() : LibraryUserAccess.CreateQuery(_libraryManager, user);
        query.IncludeItemTypes = [BaseItemKind.Episode];
        query.AncestorIds = [seriesId];
        query.IsFolder = false;

        var episodes = _libraryManager.GetItemList(query);

        // Native delivery requires a numbered regular episode. The legacy
        // unverified resolver keeps its compatibility ordering for old providers.
        var first = episodes
            .OfType<Episode>()
            .Where(e =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (!VerifyLiveDelivery || (e.ParentIndexNumber is > 0 && e.IndexNumber is > 0)) && IsEligibleAtDelivery(e, user, scope);
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

    private Guid[] RecheckResolvedItemsAtCreation(Guid[] itemIds, Jellyfin.Database.Implementations.Entities.User owner, List<(Guid Resolved, Guid Original)> originatingItems, CancellationToken cancellationToken)
    {
        var excluded = CleanupConfigHelper.ParseCommaSeparated(_configService?.GetConfiguration().ExcludedLibraries ?? string.Empty);
        var scope = excluded.Count > 0 ? LibraryPathResolver.GetLibraryRootScope(_libraryManager, excluded) : null;
        var origins = originatingItems.Count > 0 ? originatingItems.ToArray() : itemIds.Select(id => (Resolved: id, Original: id)).ToArray();
        var permitted = LibraryUserAccess.GetAllowedIds(_libraryManager, owner, origins.SelectMany(origin => new[] { origin.Resolved, origin.Original }).Distinct().ToArray());
        var sequence = new Lazy<MovieSequenceSnapshot>(() => LoadSequenceMovies(owner, scope, cancellationToken));
        var context = new DeliveryContext(owner, scope, true, sequence);
        return origins.Select(origin => RecheckDeliveryItem(origin.Resolved, origin.Original, context, permitted, cancellationToken)).OfType<Guid>().Distinct().ToArray();
    }

    private Guid? RecheckDeliveryItem(Guid id, Guid originalId, DeliveryContext context, HashSet<Guid> permitted, CancellationToken cancellationToken)
    {
        var owner = context.User!;
        var scope = context.Scope;
        cancellationToken.ThrowIfCancellationRequested();
        if (!permitted.Contains(originalId))
        {
            return null;
        }

        var original = _libraryManager.GetItemById(originalId);
        if (original is Movie originalMovie)
        {
            return IsEligibleAtDelivery(originalMovie, owner, scope) ? ResolveMovieAtDelivery(originalMovie, context) : null;
        }

        if (!permitted.Contains(id) || _libraryManager.GetItemById(id) is not { } item || !IsEligibleAtDelivery(item, owner, scope))
        {
            return original is Series && IsAccessibleMediaAtDelivery(original, owner, scope)
                ? ResolveFirstEpisodeForSeries(originalId, owner, scope, cancellationToken)
                : (Guid?)null;
        }

        return item is Movie movie ? ResolveMovieAtDelivery(movie, context) : id;
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

        if (user.MaxParentalRatingScore is { } maximum && item.InheritedParentalRatingValue > maximum)
        {
            return false;
        }

        return item.InheritedParentalRatingValue.HasValue || item.IsParentalAllowed(user, skipAllowedTagsCheck: false);
    }

    private MovieSequenceSnapshot LoadSequenceMovies(
        Jellyfin.Database.Implementations.Entities.User user,
        LibraryRootScope? scope,
        CancellationToken cancellationToken)
    {
        var query = LibraryUserAccess.CreateQuery(_libraryManager, user);
        query.IncludeItemTypes = [BaseItemKind.Movie];
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

        var collectionIds = movies.OfType<Movie>().DistinctBy(movie => movie.Id).ToDictionary(movie => movie.Id, movie => ContentAffinityResolver.ResolveTmdbCollectionId(movie));
        try
        {
            var collectionsQuery = LibraryUserAccess.CreateQuery(_libraryManager, user);
            collectionsQuery.IncludeItemTypes = [BaseItemKind.BoxSet];
            var collections = _libraryManager.GetItemList(collectionsQuery).OfType<BoxSet>();
            collectionIds = CollectionMembershipResolver.CollectionIds(movies.OfType<Movie>(), collections, _linkedChildren is null ? null : id => _linkedChildren.GetLinkedChildrenIds(id, (int)LinkedChildType.Manual));
        }
        catch (Exception exception) when (!exception.IsFatal() && exception is not OperationCanceledException)
        {
            _pluginLog.LogDebug(LogCategory, "Collection membership is unavailable for movie delivery; retaining only directly corroborated identities.", _logger);
        }

        return new MovieSequenceSnapshot(unseen, completed, collectionIds);
    }

    /// <summary>
    ///     Finds and removes all recommendation playlists owned by the specified user. Identifies managed playlists by the PlaylistNamePrefix prefix.
    /// </summary>
    /// <param name="userId">The user ID whose playlists to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of playlists removed.</returns>
    /// <param name="onlyIfDisabled">Rechecks live inactivity for disabled-user sweeps only.</param>
    /// <param name="removeAll">True only for explicit remove-all, retaining the existing legacy-name policy.</param>
    private int RemoveUserPlaylists(Guid userId, CancellationToken cancellationToken, bool onlyIfDisabled = false, bool removeAll = false)
    {
        return RemoveUserPlaylistsExcept(userId, excludePlaylistId: null, cancellationToken, onlyIfDisabled, removeAll: removeAll);
    }

    /// <summary>
    ///     Finds and removes recommendation playlists, optionally excluding one.
    ///     ILibraryManager.DeleteItem is synchronous; no async overload exists.
    /// </summary>
    private int RemoveUserPlaylistsExcept(
        Guid userId,
        string? excludePlaylistId,
        CancellationToken cancellationToken,
        bool onlyIfDisabled = false,
        IReadOnlySet<Guid>? legacyDeletionIds = null,
        bool removeAll = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = _userManager.GetUserById(userId);
        if (user is null || (onlyIfDisabled && !IsDisabled(user)))
        {
            return 0;
        }

        if (!CanReconcileUncertainCreation(userId))
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
        var parsedExcludeId = ParseExcludedPlaylistId(excludePlaylistId);
        foreach (var playlist in existingPlaylists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsUserManagedPlaylist(playlist, userId, expectedName, legacyDeletionIds, removeAll))
            {
                continue;
            }

            // Skip the just-created replacement playlist
            if (parsedExcludeId is not null && playlist.Id == parsedExcludeId.Value)
            {
                continue;
            }

            if (onlyIfDisabled && !IsCurrentlyDisabled(userId))
            {
                break;
            }

            if (TryRemovePlaylist(playlist, userId))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool IsUserManagedPlaylist(BaseItem item, Guid userId, string expectedName, IReadOnlySet<Guid>? legacyDeletionIds, bool removeAll)
        => !IsPreservedPlaylist(item) && !(item is MediaBrowser.Controller.Playlists.Playlist owned && owned.OwnerUserId != Guid.Empty && owned.OwnerUserId != userId)
            && (HasManagedOwner(item, userId) || (IsManagedPlaylistName(item, expectedName)
                && (removeAll || legacyDeletionIds?.Contains(item.Id) == true || EnsureCreationRecovery().AllowsLegacyDeletion(userId, item.Id))));

    private static Guid? ParseExcludedPlaylistId(string? playlistId)
        => Guid.TryParse(playlistId, out var id) ? id : null;

    private bool IsCurrentlyDisabled(Guid userId)
    {
        var owner = _userManager.GetUserById(userId);
        return owner is not null && IsDisabled(owner);
    }

    private Jellyfin.Database.Implementations.Entities.User? GetEnabledOwner(Guid userId)
    {
        var owner = _userManager.GetUserById(userId);
        return owner is not null && !IsDisabled(owner) ? owner : null;
    }

    private async Task<bool> MarkManagedPlaylistAsync(string playlistId, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            if (!Guid.TryParse(playlistId, out var id) || _libraryManager.GetItemById(id) is not MediaBrowser.Controller.Playlists.Playlist playlist
                || playlist.OwnerUserId != userId || !HasCreationProof(id, userId, playlist))
            {
                return false;
            }

            var parent = _playlistManager.GetPlaylistsFolder(userId) ?? _playlistManager.GetPlaylistsFolder();
            if (parent is null)
            {
                return false;
            }

            var attempt = EnsureCreationRecovery().Get(userId)!;
            var previousOwner = playlist.ProviderIds.GetValueOrDefault(ManagedOwnerProviderKey);
            var previousAttempt = playlist.ProviderIds.GetValueOrDefault(CreationAttemptProviderKey);
            var saved = false;
            try
            {
                playlist.ProviderIds[ManagedOwnerProviderKey] = userId.ToString("N");
                playlist.ProviderIds[CreationAttemptProviderKey] = attempt.Id.ToString("N");
                await _libraryManager.UpdateItemAsync(playlist, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                saved = true;
                return true;
            }
            finally
            {
                if (!saved)
                {
                    RestoreProviderId(playlist, ManagedOwnerProviderKey, previousOwner);
                    RestoreProviderId(playlist, CreationAttemptProviderKey, previousAttempt);
                }
            }
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogWarning(LogCategory, "Could not persist the recommendation playlist's ownership marker.", ex, _logger);
            return false;
        }
    }

    private async Task<bool> MarkReplacementAsync(string playlistId, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            if (await MarkManagedPlaylistAsync(playlistId, userId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            RememberUnmarkedReplacement(playlistId, userId);
            ClearUnmarkedReplacements(userId);
            throw;
        }

        RememberUnmarkedReplacement(playlistId, userId);
        ClearUnmarkedReplacements(userId);
        return false;
    }

    private void RememberUnmarkedReplacement(string playlistId, Guid userId)
    {
        if (Guid.TryParse(playlistId, out var id))
        {
            _unmarkedReplacements.TryAdd(id, userId);
        }
    }

    private bool ClearUnmarkedReplacements(Guid userId)
    {
        var cleared = true;
        foreach (var receipt in _unmarkedReplacements.Where(pair => pair.Value == userId).ToArray())
        {
            cleared &= ClearUnmarkedReplacement(receipt).Cleared;
        }

        return cleared;
    }

    private (bool Cleared, bool Deleted) ClearUnmarkedReplacement(KeyValuePair<Guid, Guid> receipt)
    {
        try
        {
            if (!_unmarkedReplacements.TryGetValue(receipt.Key, out var currentOwner) || currentOwner != receipt.Value)
            {
                return (true, false);
            }

            var item = _libraryManager.GetItemById(receipt.Key);
            var owned = item is MediaBrowser.Controller.Playlists.Playlist playlist && playlist.OwnerUserId == receipt.Value;
            if (owned && !HasCreationProof(receipt.Key, receipt.Value, item))
            {
                // Restored IDs without an attempt marker, or a different current
                // object reusing the ID, are ambiguous. Never delete by owner/name.
                return (false, false);
            }

            if (owned && !TryRemovePlaylist(item!, receipt.Value))
            {
                return (false, false);
            }

            // Absent or differently owned IDs never authorize deletion. Conditional
            // removal cannot discard a concurrent receipt for another owner.
            ((ICollection<KeyValuePair<Guid, Guid>>)_unmarkedReplacements).Remove(receipt);
            _creationProofs.TryRemove(receipt.Key, out _);
            return (true, owned);
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogWarning(LogCategory, "Unmarked replacement cleanup is unavailable; its receipt and the incumbent remain retained.", ex, _logger);
            return (false, false);
        }
    }

    private void ClearInactiveUnmarkedReplacements(HashSet<Guid> liveUsers, HashSet<Guid> disabledUsers, CancellationToken cancellationToken)
    {
        foreach (var receipt in _unmarkedReplacements.Where(pair => !liveUsers.Contains(pair.Value) || disabledUsers.Contains(pair.Value)).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = _ownerOperations.TryEnter(receipt.Value);
            if (lease is null || !IsVerifiedInactiveOwner(receipt.Value))
            {
                continue;
            }

            ClearUnmarkedReplacement(receipt);
        }
    }

    private bool CanCreateReplacement(Guid userId)
    {
        if (!ClearUnmarkedReplacements(userId))
        {
            return false;
        }

        return CanReconcileUncertainCreation(userId);
    }

    private bool CanReconcileUncertainCreation(Guid userId)
    {
        if (!_uncertainCreations.TryGetValue(userId, out var before))
        {
            return true;
        }

        try
        {
            var current = GetOwnedPlaylistIds(userId);
            var returned = EnsureCreationRecovery().Get(userId)?.ReturnedId;
            if (current.Any(id => !before.Contains(id) && returned != id && !IsPreservedOwnedId(id, userId)))
            {
                // The host threw before returning an ID. A newly observed owned item
                // may also be a concurrent manual playlist; a name is not provenance.
                // Preserve both it and the incumbent, and bound further creates until
                // reconciliation confirms that no unknown new owned item remains.
                _pluginLog.LogWarning(LogCategory, "A failed playlist creation left unresolved owned items; preserving them and deferring another replacement.", logger: _logger);
                return false;
            }

            return CompleteCreationAttempt(userId, before, current);
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogWarning(LogCategory, "Failed playlist creation cannot yet be reconciled; preserving the incumbent.", ex, _logger);
            return false;
        }
    }

    private bool IsPreservedOwnedId(Guid id, Guid owner)
        => _libraryManager.GetItemById(id) is MediaBrowser.Controller.Playlists.Playlist playlist && playlist.OwnerUserId == owner && IsPreservedPlaylist(playlist);

    private bool HasCreationProof(Guid id, Guid owner, BaseItem? item = null)
    {
        var attempt = EnsureCreationRecovery().Get(owner);
        item ??= _libraryManager.GetItemById(id);
        return attempt?.ReturnedId == id && item is MediaBrowser.Controller.Playlists.Playlist playlist && playlist.OwnerUserId == owner
            && (playlist.ProviderIds.GetValueOrDefault(CreationAttemptProviderKey) == attempt.Id.ToString("N")
                || (_creationProofs.TryGetValue(id, out var proof) && proof.Attempt == attempt.Id && ReferenceEquals(proof.Item, item)));
    }

    private static void RestoreProviderId(BaseItem item, string key, string? previous)
    {
        if (previous is null)
        {
            item.ProviderIds.Remove(key);
        }
        else
        {
            item.ProviderIds[key] = previous;
        }
    }

    private int ReadPersistedReplacementItems(Guid id, Guid owner, IReadOnlyCollection<Guid> requested)
    {
        if (_libraryManager.GetItemById(id) is not MediaBrowser.Controller.Playlists.Playlist playlist || playlist.OwnerUserId != owner || !HasCreationProof(id, owner, playlist))
        {
            throw new IOException("The returned playlist no longer matches its creation receipt.");
        }

        var entries = _linkedChildren is null
            ? playlist.LinkedChildren.Where(child => child.Type == LinkedChildType.Manual && child.ItemId.HasValue).Select(child => child.ItemId.GetValueOrDefault()).ToArray()
            : _linkedChildren.GetLinkedChildrenIds(id, (int)LinkedChildType.Manual) ?? throw new IOException("Persisted playlist entries are unavailable.");
        var expected = requested.ToHashSet();
        if (entries.Count > expected.Count || entries.Any(entry => !expected.Contains(entry)))
        {
            throw new IOException("The returned playlist contains entries not established by this creation attempt.");
        }

        return entries.Distinct().Count();
    }

    private bool CompleteCreationAttempt(Guid owner, HashSet<Guid> before, HashSet<Guid> current)
    {
        var journal = EnsureCreationRecovery();
        var attempt = journal.Get(owner);
        if (attempt?.ReturnedId is { } returned && !attempt.Marked && current.Contains(returned) && !IsPreservedOwnedId(returned, owner))
        {
            return false;
        }

        // Clear the matching in-memory gate only after the same durable attempt
        // completed. A changed journal identity must leave that gate untouched.
        if (current.Any(id => !before.Contains(id) && !(attempt?.Marked == true && attempt.ReturnedId == id && HasCreationProof(id, owner)) && !IsPreservedOwnedId(id, owner)))
        {
            return false;
        }

        var completed = (attempt is null || journal.Complete(owner, attempt.Id))
            && ((ICollection<KeyValuePair<Guid, HashSet<Guid>>>)_uncertainCreations).Remove(new(owner, before));
        if (completed && attempt?.ReturnedId is { } completedId)
        {
            ((ICollection<KeyValuePair<Guid, Guid>>)_unmarkedReplacements).Remove(new(completedId, owner));
            _creationProofs.TryRemove(completedId, out _);
        }

        return completed;
    }

    private async Task<PlaylistCreationResult?> CreateReplacementAsync(PlaylistCreationRequest request, HashSet<Guid> before, List<(Guid Resolved, Guid Original)> origins, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var journal = EnsureCreationRecovery();
        var attempt = journal.Begin(request.UserId, before);
        _uncertainCreations.TryAdd(request.UserId, before);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = GetEnabledOwner(request.UserId);
            if (owner is null)
            {
                throw new IOException("The playlist owner is unavailable after durable creation admission.");
            }

            if (VerifyLiveDelivery)
            {
                // Durable admission itself can wait on I/O. Revalidate physical
                // items against current access/playback after that final wait.
                request.ItemIdList = RecheckResolvedItemsAtCreation(request.ItemIdList.ToArray(), owner, origins, cancellationToken);
                if (request.ItemIdList.Count == 0)
                {
                    if (!CanReconcileUncertainCreation(request.UserId))
                    {
                        throw new IOException("Confirmed-empty creation could not be reconciled safely.");
                    }

                    return null;
                }
            }

            var registration = await CreateWithRegistrationReceiptAsync(request, attempt, before).ConfigureAwait(false);
            var created = registration.Result;
            if (!Guid.TryParse(created.Id, out var id) || id == Guid.Empty || before.Contains(id))
            {
                throw new IOException("The host did not return a new playlist identity; the failed attempt must be reconciled.");
            }

            _unmarkedReplacements.TryAdd(id, request.UserId);
            if (registration.Registered is { } registered && registered.Id == id)
            {
                // Captured from this host call's registration event, never from
                // a later lookup that might already refer to a reused manual ID.
                _creationProofs.TryAdd(id, new CreationProof(attempt.Id, registered));
            }

            journal.RecordReturned(request.UserId, attempt.Id, id);
            return created;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Registration precedes metadata/entry persistence in Jellyfin. Never
            // retry an ID-less failed call without reconciling its ownership snapshot.
            _uncertainCreations.TryAdd(request.UserId, before);
            throw;
        }
    }

    private async Task<(PlaylistCreationResult Result, MediaBrowser.Controller.Playlists.Playlist? Registered)> CreateWithRegistrationReceiptAsync(
        PlaylistCreationRequest request,
        PlaylistCreationJournal.Attempt attempt,
        HashSet<Guid> before)
    {
        MediaBrowser.Controller.Playlists.Playlist? registered = null;
        var previous = _creationExecution.Value;
        void OnRegistered(object? sender, ItemChangeEventArgs change)
        {
            if (_creationExecution.Value == attempt.Id && change.Item is MediaBrowser.Controller.Playlists.Playlist playlist
                && playlist.OwnerUserId == request.UserId && playlist.Name == request.Name && !before.Contains(playlist.Id))
            {
                Interlocked.CompareExchange(ref registered, playlist, null);
            }
        }

        _creationExecution.Value = attempt.Id;
        _libraryManager.ItemAdded += OnRegistered;
        try
        {
            var result = await _playlistManager.CreatePlaylist(request).ConfigureAwait(false);
            return (result, registered);
        }
        finally
        {
            _libraryManager.ItemAdded -= OnRegistered;
            _creationExecution.Value = previous;
        }
    }

    private HashSet<Guid> GetOwnedPlaylistIds(Guid userId)
    {
        // GetPlaylists(userId) is a viewer-policy query in Jellyfin12.4, not an
        // ownership inventory. Hidden owned playlists must remain in both snapshots.
        IReadOnlyList<BaseItem>? playlists;
        if (_ownedPlaylists is { } loadOwned)
        {
            playlists = loadOwned(userId);
        }
        else
        {
            playlists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist],
                Recursive = true,
                GroupByPresentationUniqueKey = false
            });
        }

        if (playlists is null)
        {
            throw new IOException("Playlist ownership is unavailable.");
        }

        var ids = new HashSet<Guid>();
        foreach (var id in playlists.OfType<MediaBrowser.Controller.Playlists.Playlist>().Where(playlist => playlist.OwnerUserId == userId).Select(playlist => playlist.Id))
        {
            if (id == Guid.Empty)
            {
                throw new IOException("Playlist ownership contains an unresolved identity.");
            }

            ids.Add(id);
            if (ids.Count > 4096)
            {
                throw new IOException("The bounded playlist creation snapshot is full.");
            }
        }

        return ids;
    }

    private int RemoveIdentifiedInactivePlaylists(HashSet<Guid> liveUsers, HashSet<Guid> disabledUsers, CancellationToken cancellationToken, bool removeAll = false)
    {
        var removed = 0;
        try
        {
            var playlists = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Playlist], Recursive = true });
            foreach (var group in playlists.OfType<MediaBrowser.Controller.Playlists.Playlist>().DistinctBy(item => item.Id).GroupBy(item => item.OwnerUserId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var lease = _ownerOperations.TryEnter(group.Key);
                if (lease is null)
                {
                    continue;
                }

                removed += RemoveEligibleOwnerPlaylists(group, liveUsers, disabledUsers, removeAll, cancellationToken);
            }
        }
        catch (Exception ex) when (!ex.IsFatal() && ex is not OperationCanceledException)
        {
            _pluginLog.LogWarning(LogCategory, "Could not inspect identified recommendation playlists for inactive owners.", ex, _logger);
        }

        return removed;
    }

    private int RemoveEligibleOwnerPlaylists(IEnumerable<MediaBrowser.Controller.Playlists.Playlist> playlists, HashSet<Guid> liveUsers, HashSet<Guid> disabledUsers, bool removeAll, CancellationToken cancellationToken)
    {
        var removed = 0;
        bool? reconciled = null;
        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = playlist.OwnerUserId;
            if (!HasManagedOwner(playlist, owner) || !(removeAll || !liveUsers.Contains(owner) || disabledUsers.Contains(owner))
                || !(removeAll || IsVerifiedInactiveOwner(owner)))
            {
                continue;
            }

            reconciled ??= CanReconcileUncertainCreation(owner);
            if (reconciled == true && TryRemovePlaylist(playlist, owner))
            {
                removed++;
            }
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
        => !IsPreservedPlaylist(item) && owner != Guid.Empty && item is MediaBrowser.Controller.Playlists.Playlist playlist && playlist.OwnerUserId == owner
            && playlist.ProviderIds.TryGetValue(ManagedOwnerProviderKey, out var marker) && Guid.TryParse(marker, out var recordedOwner) && recordedOwner == owner;

    private static bool IsPreservedPlaylist(BaseItem item)
        => item.ProviderIds.TryGetValue(PreservedPlaylistProviderKey, out var marker) && marker == item.Id.ToString("N");

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

    private sealed record MovieSequenceSnapshot(Dictionary<int, Movie> Unseen, HashSet<int> Completed, Dictionary<Guid, int> CollectionIds);

    private sealed record CreationProof(Guid Attempt, BaseItem Item);

    private sealed record DeliveryContext(Jellyfin.Database.Implementations.Entities.User? User, LibraryRootScope? Scope, bool VerifyLive, Lazy<MovieSequenceSnapshot> SequenceMovies);
}
