using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Handles persistence of discovery results to the plugin data directory.
///     Mirrors the pattern used by <see cref="Recommendation.RecommendationCacheService"/>.
///     <para>
///         <b>Synchronisation:</b> uses a <see cref="SemaphoreSlim"/> instead of a plain
///         <c>lock</c> so both synchronous callers (scheduled tasks, <see cref="Save"/>) and
///         asynchronous request-driven callers (<see cref="MarkAsRequestedAsync"/> /
///         <see cref="RemoveItemAsync"/>) serialise through the exact same mutex. Falling back
///         to two separate primitives would allow a background task's <c>Save</c> to race with
///         a live HTTP mutation of the same in-memory cache.
///     </para>
/// </summary>
public sealed class DiscoveryCacheService : IDisposable
{
    private const string FileName = "jellyfin-helper-discovery-results.json";

    /// <summary>
    ///     Maximum allowed file size for the discovery cache file (50 MB).
    ///     Files exceeding this size are treated as corrupted and deleted.
    /// </summary>
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;

    /// <summary>
    ///     Serialises access to <see cref="_memoryCache"/> and the on-disk file. A
    ///     <see cref="SemaphoreSlim"/> (rather than <c>lock</c>) is required because the
    ///     new async request-path methods await file I/O while holding the mutex, and the
    ///     .NET <c>lock</c> statement does not permit an <c>await</c> inside the guarded region.
    ///     Initialised with a capacity of 1 so it functions as a straight mutual-exclusion lock.
    /// </summary>
    private readonly SemaphoreSlim _fileLock = new(initialCount: 1, maxCount: 1);
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryCacheService> _logger;

    /// <summary>
    ///     In-memory cache of discovery results. Avoids reading from disk on every API call.
    ///     Invalidated on <see cref="Save"/> and <see cref="MarkAsRequested"/>.
    ///     Typed as <c>List</c> (not <c>IReadOnlyList</c>) because <see cref="MarkAsRequested"/>
    ///     mutates individual recommendation items in-place.
    /// </summary>
    private List<DiscoveryResult>? _memoryCache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryCacheService"/> class.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public DiscoveryCacheService(
        IPluginLogService pluginLog,
        ILogger<DiscoveryCacheService> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;

        var dataPath = Plugin.Instance?.DataFolderPath ?? string.Empty;
        _filePath = Path.Join(dataPath, FileName);
    }

    /// <summary>
    ///     Loads cached discovery results. Returns a read-only wrapper of the in-memory cache if available,
    ///     otherwise reads from disk and populates the cache.
    ///     <para>
    ///         <b>Important:</b> The returned list is a shallow read-only wrapper (<see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>).
    ///         The outer list cannot be modified, but the <see cref="DiscoveryResult"/> objects within
    ///         are shared references to the cache. Callers MUST NOT mutate properties of the returned items.
    ///         Mutation operations (e.g., <see cref="MarkAsRequested"/>) must go through this service
    ///         under <see cref="_fileLock"/> to ensure thread-safe, atomic updates.
    ///     </para>
    /// </summary>
    /// <returns>The deserialized results, or an empty list if the file does not exist or is invalid.</returns>
    public IReadOnlyList<DiscoveryResult> Load()
    {
        _fileLock.Wait();
        try
        {
            try
            {
                EnsureLoadedLocked();
                var cache = _memoryCache ??= [];
                return cache.AsReadOnly();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not load discovery results from {_filePath}: {ex.Message}",
                    ex,
                    _logger);
                // Cache empty result to prevent repeated failed disk reads on every API call.
                // Next Save() will repopulate the cache with fresh data.
                _memoryCache = [];
                return _memoryCache.AsReadOnly();
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Removes a specific TMDb item from all users' cached recommendation lists.
    ///     Called when a user dismisses an item — ensures the item disappears immediately
    ///     (not just on the next scheduled task run) and stays gone across page reloads.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the item to remove.</param>
    /// <param name="mediaType">The media type ("movie" or "tv").</param>
    /// <param name="userId">The Jellyfin user ID. Only removes from this user's list.</param>
    public void RemoveItem(int tmdbId, string mediaType, Guid userId)
    {
        _fileLock.Wait();
        try
        {
            RemoveItemLocked(tmdbId, mediaType, userId, useAsyncWrite: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Asynchronous counterpart of <see cref="RemoveItem"/>. Preferred for callers on
    ///     request-driven paths (e.g. HTTP dismissal handlers) because the underlying atomic
    ///     write yields to the thread pool during transient-IO retries instead of blocking
    ///     the caller's request thread with <see cref="Thread.Sleep(int)"/>.
    ///     <para>
    ///         Serialises through the exact same <see cref="_fileLock"/> as the synchronous
    ///         overload, guaranteeing that a background <see cref="Save"/> and a live HTTP
    ///         dismissal cannot interleave partial writes on the same cache file.
    ///     </para>
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the item to remove.</param>
    /// <param name="mediaType">The media type ("movie" or "tv").</param>
    /// <param name="userId">The Jellyfin user ID. Only removes from this user's list.</param>
    /// <param name="cancellationToken">Cancellation token honoured between retry attempts of the atomic write.</param>
    /// <returns>A task that completes once the removal (and its persistence attempt) have finished.</returns>
    public async Task RemoveItemAsync(int tmdbId, string mediaType, Guid userId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RemoveItemLocked(tmdbId, mediaType, userId, useAsyncWrite: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Core removal logic shared by <see cref="RemoveItem"/> and <see cref="RemoveItemAsync"/>.
    ///     Must be called while holding <see cref="_fileLock"/>. Switches between the sync and
    ///     async atomic-write paths based on <paramref name="useAsyncWrite"/> so the retry
    ///     back-off in <see cref="AtomicFile"/> yields correctly on the request-driven path.
    /// </summary>
    private async Task RemoveItemLocked(int tmdbId, string mediaType, Guid userId, bool useAsyncWrite, CancellationToken cancellationToken)
    {
        try
        {
            EnsureLoadedLocked();
            var cache = _memoryCache ??= [];

            if (cache.Count == 0)
            {
                return;
            }

            var userResult = cache.FirstOrDefault(r => r.UserId == userId);
            if (userResult == null)
            {
                return;
            }

            // Identify items to remove WITHOUT mutating the live cache yet.
            // This avoids leaving _memoryCache in an inconsistent state if persistence fails.
            //
            // We capture each removal candidate's ORIGINAL INDEX alongside the item itself so
            // that a rollback (transient IO error, cancellation) can reinsert the items at
            // their original ranking positions. AddRange-based rollback would silently
            // reorder recommendations — a subsequent Save() would then persist that shuffled
            // ranking, permanently degrading recommendation quality after a single failure.
            var recommendations = userResult.Recommendations;
            var itemsToRemove = new List<(int OriginalIndex, DiscoveryRecommendation Item)>();
            for (var i = 0; i < recommendations.Count; i++)
            {
                var rec = recommendations[i];
                if (rec.TmdbId == tmdbId &&
                    string.Equals(rec.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                {
                    itemsToRemove.Add((i, rec));
                }
            }

            if (itemsToRemove.Count == 0)
            {
                return;
            }

            // Apply removal in DESCENDING index order so each Remove call does not shift the
            // indices of items we still need to remove. Ascending order would need index
            // arithmetic to compensate for the shift and would be trickier to reason about.
            for (var i = itemsToRemove.Count - 1; i >= 0; i--)
            {
                recommendations.RemoveAt(itemsToRemove[i].OriginalIndex);
            }

            try
            {
                var updatedJson = JsonSerializer.Serialize(cache, JsonOptions);

                // Use AtomicFile: bounded retry on transient AV/indexer locks +
                // internal temp-file cleanup, so no manual .tmp handling required.
                if (useAsyncWrite)
                {
                    // cancellationToken is passed by name because AtomicFile.WriteAllTextAsync
                    // orders parameters as (path, contents, maxAttempts, cancellationToken)
                    // to satisfy the CA1068 "CancellationToken is last" convention.
                    await AtomicFile.WriteAllTextAsync(_filePath, updatedJson, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // CA1849 is intentionally suppressed here: this branch is only ever taken
                    // when the caller is the synchronous RemoveItem overload, which invokes us
                    // via .GetAwaiter().GetResult() on a background scheduled-task thread.
                    // Awaiting WriteAllTextAsync in that path would sync-over-async block on
                    // the same wait handle we're already holding via GetResult, which is both
                    // pointless (we're already sync) and marginally more expensive than the
                    // Thread.Sleep-based sync WriteAllText. The async request-driven path uses
                    // the async branch above.
#pragma warning disable CA1849 // Call async methods when in an async method
                    AtomicFile.WriteAllText(_filePath, updatedJson);
#pragma warning restore CA1849
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation on the async path: roll back the in-memory removal so the
                // caller's view of the cache is consistent with the on-disk state, then
                // propagate. Same rollback shape as the transient-IO catch below.
                ReinsertAtOriginalIndices(recommendations, itemsToRemove);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Rollback: re-insert removed items at their ORIGINAL positions so the
                // ranking order is preserved. On next restart the disk state (which still
                // has the items in their original order) will be loaded, so the user will
                // see the item again — acceptable for a transient IO failure vs. silent
                // data loss AND silent reordering.
                ReinsertAtOriginalIndices(recommendations, itemsToRemove);

                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not persist removal of TMDb#{tmdbId} from cache: {ex.Message}",
                    ex,
                    _logger);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                "DiscoveryCache",
                $"Could not remove TMDb#{tmdbId} from cache: {ex.Message}",
                ex,
                _logger);
            _memoryCache ??= [];
        }
    }

    /// <summary>
    ///     Marks a specific TMDb item as already requested in the cached results.
    ///     Updates both the in-memory cache and the on-disk file.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the requested item.</param>
    /// <param name="mediaType">The media type ("movie" or "tv") to match against. Required because TMDb movie and TV IDs are separate namespaces.</param>
    public void MarkAsRequested(int tmdbId, string mediaType)
    {
        _fileLock.Wait();
        try
        {
            MarkAsRequestedLocked(tmdbId, mediaType, useAsyncWrite: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Asynchronous counterpart of <see cref="MarkAsRequested"/>. Preferred for callers on
    ///     request-driven paths (e.g. the HTTP <c>SubmitRequest</c> / <c>SubmitMyRequest</c>
    ///     handlers) because the underlying atomic write yields to the thread pool during
    ///     transient-IO retries instead of blocking the caller's request thread with
    ///     <see cref="Thread.Sleep(int)"/>.
    ///     <para>
    ///         Serialises through the exact same <see cref="_fileLock"/> as the synchronous
    ///         overload, guaranteeing that a background <see cref="Save"/> and a live HTTP
    ///         request-completion cannot interleave partial writes on the same cache file.
    ///     </para>
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the requested item.</param>
    /// <param name="mediaType">The media type ("movie" or "tv") to match against.</param>
    /// <param name="cancellationToken">Cancellation token honoured between retry attempts of the atomic write.</param>
    /// <returns>A task that completes once the mark (and its persistence attempt) have finished.</returns>
    public async Task MarkAsRequestedAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MarkAsRequestedLocked(tmdbId, mediaType, useAsyncWrite: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Core mark-as-requested logic shared by <see cref="MarkAsRequested"/> and
    ///     <see cref="MarkAsRequestedAsync"/>. Must be called while holding <see cref="_fileLock"/>.
    ///     Switches between the sync and async atomic-write paths based on
    ///     <paramref name="useAsyncWrite"/> so the retry back-off in <see cref="AtomicFile"/>
    ///     yields correctly on the request-driven path.
    /// </summary>
    private async Task MarkAsRequestedLocked(int tmdbId, string mediaType, bool useAsyncWrite, CancellationToken cancellationToken)
    {
        try
        {
            EnsureLoadedLocked();
            var cache = _memoryCache ??= [];

            if (cache.Count == 0)
            {
                return;
            }

            // Determine which items need updating WITHOUT mutating the live cache yet.
            // This avoids leaving _memoryCache in an inconsistent state if persistence fails.
            var indicesToMark = new List<(int UserIdx, int RecIdx)>();
            for (var u = 0; u < cache.Count; u++)
            {
                var recs = cache[u].Recommendations;
                for (var r = 0; r < recs.Count; r++)
                {
                    if (recs[r].TmdbId == tmdbId
                        && string.Equals(recs[r].MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
                        && !recs[r].AlreadyRequested)
                    {
                        indicesToMark.Add((u, r));
                    }
                }
            }

            if (indicesToMark.Count == 0)
            {
                return;
            }

            // Apply mutations
            foreach (var (userIdx, recIdx) in indicesToMark)
            {
                cache[userIdx].Recommendations[recIdx].AlreadyRequested = true;
            }

            try
            {
                var updatedJson = JsonSerializer.Serialize(cache, JsonOptions);

                // Use AtomicFile: bounded retry on transient AV/indexer locks +
                // internal temp-file cleanup, so no manual .tmp handling required.
                if (useAsyncWrite)
                {
                    // cancellationToken is passed by name because AtomicFile.WriteAllTextAsync
                    // orders parameters as (path, contents, maxAttempts, cancellationToken)
                    // to satisfy the CA1068 "CancellationToken is last" convention.
                    await AtomicFile.WriteAllTextAsync(_filePath, updatedJson, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // CA1849 is intentionally suppressed here: same rationale as the identical
                    // branch in RemoveItemLocked — the sync branch is only entered from the
                    // synchronous MarkAsRequested overload via .GetAwaiter().GetResult() on a
                    // background thread. Sync-over-async-over-sync would gain nothing.
#pragma warning disable CA1849 // Call async methods when in an async method
                    AtomicFile.WriteAllText(_filePath, updatedJson);
#pragma warning restore CA1849
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation on the async path: roll back the in-memory mutations so the
                // caller's view of the cache is consistent with the on-disk state, then
                // propagate. Matches the transient-IO rollback below in shape and intent.
                foreach (var (userIdx, recIdx) in indicesToMark)
                {
                    cache[userIdx].Recommendations[recIdx].AlreadyRequested = false;
                }

                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Rollback in-memory mutations on persistence failure
                foreach (var (userIdx, recIdx) in indicesToMark)
                {
                    cache[userIdx].Recommendations[recIdx].AlreadyRequested = false;
                }

                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                "DiscoveryCache",
                $"Could not mark TMDb#{tmdbId} as requested in cache: {ex.Message}",
                ex,
                _logger);

            // Match Load() behavior: prevent repeated failed disk reads on subsequent calls.
            _memoryCache ??= [];
        }
    }

    /// <summary>
    ///     Reinserts previously-removed items back into <paramref name="recommendations"/> at
    ///     their original indices. Used by the rollback paths of <see cref="RemoveItemLocked"/>
    ///     to preserve ranking order when a persistence failure (or cancellation) forces us to
    ///     undo an in-memory removal.
    ///     <para>
    ///         Iterates in <b>ascending</b> index order because the <c>OriginalIndex</c> values
    ///         were captured BEFORE any removals. When we reinsert item A at index 3, then item
    ///         B at index 7, index 7 already refers to the shifted position that includes A —
    ///         which is exactly what we want. If we iterated in descending order, we'd have to
    ///         compensate for the shift caused by later reinserts and the arithmetic would drift.
    ///     </para>
    ///     <para>
    ///         Precondition: <paramref name="itemsToRemove"/> was produced by a linear scan of
    ///         the source list, so its entries are already in ascending index order — no sort
    ///         needed. The <see cref="List{T}.Insert(int, T)"/> at each captured original index
    ///         restores the exact pre-removal state.
    ///     </para>
    /// </summary>
    /// <param name="recommendations">The recommendations list currently missing the removed items.</param>
    /// <param name="itemsToRemove">The (originalIndex, item) pairs captured before the removal.</param>
    private static void ReinsertAtOriginalIndices(
        List<DiscoveryRecommendation> recommendations,
        List<(int OriginalIndex, DiscoveryRecommendation Item)> itemsToRemove)
    {
        // Ascending order matters — see the XML doc above for why.
        foreach (var (originalIndex, item) in itemsToRemove)
        {
            // Clamp to Count as a defensive guard: if some other mutation has trimmed the list
            // since we captured the indices (should be impossible under _fileLock, but the cost
            // is a single comparison), append instead of throwing an ArgumentOutOfRangeException.
            var targetIndex = originalIndex <= recommendations.Count ? originalIndex : recommendations.Count;
            recommendations.Insert(targetIndex, item);
        }
    }

    /// <summary>
    ///     Ensures <see cref="_memoryCache"/> is populated from disk if not already loaded.
    ///     Must be called while holding <see cref="_fileLock"/>.
    ///     <para>
    ///         On missing file: initializes to empty list.<br/>
    ///         On oversized file (&gt; <see cref="MaxFileSizeBytes"/>): deletes the file (best-effort)
    ///         and initializes to empty list.<br/>
    ///         On successful read: deserializes JSON and assigns to <see cref="_memoryCache"/>.
    ///     </para>
    ///     Callers are responsible for catching <see cref="IOException"/>,
    ///     <see cref="JsonException"/>, and <see cref="UnauthorizedAccessException"/>
    ///     if the disk read fails (propagated from <see cref="File.ReadAllText(string)"/>
    ///     or <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions?)"/>).
    /// </summary>
    private void EnsureLoadedLocked()
    {
        if (_memoryCache != null)
        {
            return;
        }

        if (!File.Exists(_filePath))
        {
            _memoryCache = [];
            return;
        }

        // Hardening: reject oversized files (likely corrupted or tampered).
        var fileInfo = new FileInfo(_filePath);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            _pluginLog.LogWarning(
                "DiscoveryCache",
                $"Discovery cache file exceeds {MaxFileSizeBytes / (1024 * 1024)}MB ({fileInfo.Length} bytes). Deleting and returning empty.",
                null,
                _logger);
            try
            {
                File.Delete(_filePath);
            }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
            {
                // Best effort — file may be locked by another process.
            }

            _memoryCache = [];
            return;
        }

        var json = File.ReadAllText(_filePath);
        _memoryCache = JsonSerializer.Deserialize<List<DiscoveryResult>>(json, JsonOptions) ?? [];
    }

    /// <summary>
    ///     Saves discovery results to disk using atomic write (temp file + move).
    /// </summary>
    /// <param name="results">The results to persist.</param>
    /// <returns><c>true</c> if the results were successfully persisted to disk; <c>false</c> if a write error occurred.</returns>
    public bool Save(IReadOnlyList<DiscoveryResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        _fileLock.Wait();
        try
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(results, JsonOptions);

                // Use AtomicFile so a transient sharing violation on the final File.Move
                // (typical when an AV scanner or the Search indexer briefly holds the file
                // handle) gets a bounded retry with backoff. AtomicFile also handles
                // temp-file cleanup internally.
                AtomicFile.WriteAllText(_filePath, json);

                // Update in-memory cache to match persisted state.
                // Always create a detached copy — never alias the caller's list to prevent
                // external mutation bypassing _fileLock synchronization.
                _memoryCache = new List<DiscoveryResult>(results);

                _pluginLog.LogDebug(
                    "DiscoveryCache",
                    $"Saved {results.Count} discovery results to {_filePath}",
                    _logger);

                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not save discovery results to {_filePath}: {ex.Message}",
                    ex,
                    _logger);

                return false;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Releases the <see cref="SemaphoreSlim"/> used to serialise cache access.
    ///     <para>
    ///         The DI container (<c>Microsoft.Extensions.DependencyInjection</c>) owns this
    ///         service's lifetime and will call <see cref="Dispose"/> when the plugin is
    ///         torn down. No unmanaged resources are held, so a straight <c>SemaphoreSlim.Dispose()</c>
    ///         is sufficient. Required by CA1001 because we now hold an <see cref="IDisposable"/>
    ///         field (<see cref="_fileLock"/>) since the switch from <c>Lock</c> to
    ///         <see cref="SemaphoreSlim"/> to permit async request-path callers.
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        _fileLock.Dispose();
    }
}
