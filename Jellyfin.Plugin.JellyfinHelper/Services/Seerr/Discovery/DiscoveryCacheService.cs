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
///     Handles persistence of discovery results to the plugin data directory. Mirrors the pattern used by RecommendationCacheService.
/// </summary>
public sealed class DiscoveryCacheService : IDisposable
{
    private const string FileName = "jellyfin-helper-discovery-results.json";

    /// <summary>
    ///     Maximum allowed file size for the discovery cache file (50 MB).
    ///     Files exceeding this size are treated as corrupted and deleted.
    /// </summary>
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    /// <summary>
    ///     Log category used for all discovery-cache log entries.
    /// </summary>
    private const string LogCategory = "DiscoveryCache";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;

    /// <summary>
    ///     Serialises access to _memoryCache and the on-disk file.
    /// </summary>
    private readonly SemaphoreSlim _fileLock = new(initialCount: 1, maxCount: 1);
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryCacheService> _logger;

    /// <summary>
    ///     In-memory cache of discovery results. Avoids reading from disk on every API call.
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
        : this(pluginLog, logger, filePath: null)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryCacheService"/> class with an
    ///     explicit file path. Intended for testing - avoids the <see cref="Plugin.Instance"/>
    ///     requirement.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="filePath">Explicit cache file path, or <c>null</c> to resolve from <see cref="Plugin.Instance"/>.</param>
    internal DiscoveryCacheService(
        IPluginLogService pluginLog,
        ILogger<DiscoveryCacheService> logger,
        string? filePath)
    {
        _pluginLog = pluginLog;
        _logger = logger;

        if (filePath != null)
        {
            _filePath = filePath;
            return;
        }

        var dataPath = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataPath))
        {
            throw new InvalidOperationException(
                "DiscoveryCacheService: Plugin.Instance is not initialized; cannot resolve data folder path.");
        }

        _filePath = Path.Join(dataPath, FileName);
    }

    /// <summary>
    ///     Loads cached discovery results. Returns a deep-copied snapshot of the in-memory cache if available, otherwise reads from disk and populates the cache first.
    /// </summary>
    /// <returns>A deep-copied list of discovery results, or an empty list if the file does not exist or is invalid.</returns>
    public IReadOnlyList<DiscoveryResult> Load()
    {
        _fileLock.Wait();
        try
        {
            try
            {
                EnsureLoadedLocked();
                var cache = _memoryCache ??= [];
                return cache.ConvertAll(r => r.Clone());
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    LogCategory,
                    $"Could not load discovery results from {_filePath}: {ex.Message}",
                    ex,
                    _logger);
                // Cache empty result to prevent repeated failed disk reads on every API call.
                // Next Save() will repopulate the cache with fresh data.
                _memoryCache = [];
                return [];
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Removes a specific TMDb item from the specified user's cached recommendation list.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the item to remove.</param>
    /// <param name="mediaType">The media type ("movie" or "tv").</param>
    /// <param name="userId">The Jellyfin user ID. Only removes from this user's list.</param>
    /// <remarks>
    ///     Uses _fileLock synchronously (matching the MarkAsRequested pattern) and calls synchronous I/O, avoiding the sync-over-async pitfall that the previous Task.Run wrapper was designed to work around.
    /// </remarks>
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
    ///     Asynchronous counterpart of RemoveItem. Preferred for callers on request-driven paths (e.g.
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
    ///     Core removal logic shared by RemoveItem and RemoveItemAsync. Must be called while holding _fileLock.
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

            // Identify items to remove WITHOUT mutating the live cache yet. This avoids leaving _memoryCache in an inconsistent state if persistence fails.
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

            // Apply removal in DESCENDING index order so each Remove call does not shift the indices of items we still need to remove.
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
                    // cancellationToken is passed by name because AtomicFile.WriteAllTextAsync orders parameters as (path, contents, maxAttempts, cancellationToken) to satisfy the CA1068 "CancellationToken is last" convention.
                    await AtomicFile.WriteAllTextAsync(_filePath, updatedJson, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // This branch is only ever taken when the caller is the synchronous RemoveItem overload, which invokes us via .GetAwaiter().GetResult() on a background scheduled-task thread.
#pragma warning disable CA1849 // Call async methods when in an async method
                    AtomicFile.WriteAllText(_filePath, updatedJson);
#pragma warning restore CA1849
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation on the async path: roll back the in-memory removal so the caller's view of the cache is consistent with the on-disk state, then propagate.
                ReinsertAtOriginalIndices(recommendations, itemsToRemove);
                throw;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                // Rollback: re-insert removed items at their ORIGINAL positions so the ranking order is preserved.
                ReinsertAtOriginalIndices(recommendations, itemsToRemove);

                _pluginLog.LogWarning(
                    LogCategory,
                    $"Could not persist removal of TMDb#{tmdbId} from cache: {ex.Message}",
                    ex,
                    _logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                       and not OutOfMemoryException
                                       and not StackOverflowException)
        {
            // Broad filter matches MarkAsRequestedLocked: EnsureLoadedLocked and JsonSerializer can surface SecurityException, NotSupportedException, and ArgumentException in addition to IOException / UnauthorizedAccessException / JsonException.
            _pluginLog.LogWarning(
                LogCategory,
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
    /// <remarks>
    ///     <b>Do not call from a thread with a synchronization context (e.g. an ASP.NET request thread).</b> The underlying MarkAsRequestedLocked is async; bridging async-to-sync via GetAwaiter().GetResult() inside a SemaphoreSlim can deadlock.
    /// </remarks>
    public void MarkAsRequested(int tmdbId, string mediaType)
    {
        _fileLock.Wait();
        try
        {
            MarkAsRequestedLocked(tmdbId, mediaType, userId: null, useAsyncWrite: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Asynchronous counterpart of MarkAsRequested. Marks the item as requested for ALL users (admin path).
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the requested item.</param>
    /// <param name="mediaType">The media type ("movie" or "tv") to match against.</param>
    /// <param name="cancellationToken">Cancellation token honoured between retry attempts of the atomic write.</param>
    /// <returns>A task that completes once the mark (and its persistence attempt) have finished.</returns>
    public Task MarkAsRequestedAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default)
        => MarkAsRequestedAsync(tmdbId, mediaType, userId: null, cancellationToken);

    /// <summary>
    ///     Asynchronous counterpart of MarkAsRequested with optional per-user scoping. When userId has a value, only cache entries belonging to that user are marked; when null, all users' entries are marked (admin path).
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the requested item.</param>
    /// <param name="mediaType">The media type ("movie" or "tv") to match against.</param>
    /// <param name="userId">
    ///     When set, restricts the mark to the cache entry whose <see cref="DiscoveryResult.UserId"/>
    ///     matches this value. Pass <c>null</c> to mark all users (existing admin path).
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured between retry attempts of the atomic write.</param>
    /// <returns>A task that completes once the mark (and its persistence attempt) have finished.</returns>
    public async Task MarkAsRequestedAsync(int tmdbId, string mediaType, Guid? userId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MarkAsRequestedLocked(tmdbId, mediaType, userId, useAsyncWrite: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Core mark-as-requested logic shared by MarkAsRequested and MarkAsRequestedAsync(int,string,Guid?,CancellationToken).
    /// </summary>
    private async Task MarkAsRequestedLocked(int tmdbId, string mediaType, Guid? userId, bool useAsyncWrite, CancellationToken cancellationToken)
    {
        EnsureLoadedLocked();
        var cache = _memoryCache ??= [];

        if (cache.Count == 0)
        {
            return;
        }

        // Determine which items need updating WITHOUT mutating the live cache yet.
        // This avoids leaving _memoryCache in an inconsistent state if persistence fails.
        var indicesToMark = CollectIndicesToMark(cache, tmdbId, mediaType, userId);

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
                // cancellationToken is passed by name because AtomicFile.WriteAllTextAsync orders parameters as (path, contents, maxAttempts, cancellationToken) to satisfy the CA1068 "CancellationToken is last" convention.
                await AtomicFile.WriteAllTextAsync(_filePath, updatedJson, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Same rationale as the identical branch in RemoveItemLocked - the sync branch is only entered from the synchronous MarkAsRequested overload via .GetAwaiter().GetResult() on a background thread.
#pragma warning disable CA1849 // Call async methods when in an async method
                AtomicFile.WriteAllText(_filePath, updatedJson);
#pragma warning restore CA1849
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation on the async path: roll back the in-memory mutations so the caller's view of the cache is consistent with the on-disk state, then propagate.
            foreach (var (userIdx, recIdx) in indicesToMark)
            {
                cache[userIdx].Recommendations[recIdx].AlreadyRequested = false;
            }

            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Rollback in-memory mutations on ANY persistence failure. AtomicFile.WriteAllText can surface more than IOException/UnauthorizedAccessException (e.g.
            foreach (var (userIdx, recIdx) in indicesToMark)
            {
                cache[userIdx].Recommendations[recIdx].AlreadyRequested = false;
            }

            _pluginLog.LogWarning(
                LogCategory,
                $"Could not persist mark-as-requested for TMDb#{tmdbId} in cache: {ex.Message}",
                ex,
                _logger);
        }
    }

    /// <summary>
    ///     Scans the cache for recommendation entries matching tmdbId and mediaType that are not yet marked as requested, returning their (user index, recommendation index) coordinates WITHOUT mutating the cache.
    /// </summary>
    /// <param name="cache">The live memory cache to scan.</param>
    /// <param name="tmdbId">The TMDb id to match.</param>
    /// <param name="mediaType">The media type to match (case-insensitive).</param>
    /// <param name="userId">When set, restricts the scan to the given user's entries.</param>
    /// <returns>The list of (user index, recommendation index) pairs to mark.</returns>
    private static List<(int UserIdx, int RecIdx)> CollectIndicesToMark(
        List<DiscoveryResult> cache,
        int tmdbId,
        string mediaType,
        Guid? userId)
    {
        var indicesToMark = new List<(int UserIdx, int RecIdx)>();
        for (var u = 0; u < cache.Count; u++)
        {
            // When userId is specified, skip entries that belong to a different user.
            if (userId.HasValue && cache[u].UserId != userId.Value)
            {
                continue;
            }

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

        return indicesToMark;
    }

    /// <summary>
    ///     Reinserts previously-removed items back into recommendations at their original indices.
    /// </summary>
    /// <param name="recommendations">The recommendations list currently missing the removed items.</param>
    /// <param name="itemsToRemove">The (originalIndex, item) pairs captured before the removal.</param>
    private static void ReinsertAtOriginalIndices(
        List<DiscoveryRecommendation> recommendations,
        List<(int OriginalIndex, DiscoveryRecommendation Item)> itemsToRemove)
    {
        // Ascending order matters - see the XML doc above for why.
        foreach (var (originalIndex, item) in itemsToRemove)
        {
            // Clamp to Count as a defensive guard: if some other mutation has trimmed the list since we captured the indices (should be impossible under _fileLock, but the cost is a single comparison), append instead of throwing an ArgumentOutOfRangeException.
            var targetIndex = originalIndex <= recommendations.Count ? originalIndex : recommendations.Count;
            recommendations.Insert(targetIndex, item);
        }
    }

    /// <summary>
    ///     Ensures _memoryCache is populated from disk if not already loaded. Must be called while holding _fileLock.
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

        // Reject oversized files (likely corrupted or tampered).
        var fileInfo = new FileInfo(_filePath);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Discovery cache file exceeds {MaxFileSizeBytes / (1024 * 1024)}MB ({fileInfo.Length} bytes). Deleting and returning empty.",
                null,
                _logger);
            try
            {
                File.Delete(_filePath);
            }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
            {
                // Best effort - file may be locked by another process.
            }

            _memoryCache = [];
            return;
        }

        var json = File.ReadAllText(_filePath);
        _memoryCache = JsonSerializer.Deserialize<List<DiscoveryResult>>(json, JsonOptions)
                           ?.Where(r => r != null).ToList() ?? [];
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

                // Snapshot first so the serialized JSON and the in-memory cache are guaranteed to be the same object graph, even if the caller mutates one of the passed-in DiscoveryResult objects between Serialize and Clone.
                var snapshot = results.Select(r => r.Clone()).ToList();
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);

                // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
                AtomicFile.WriteAllText(_filePath, json);

                _memoryCache = snapshot;

                _pluginLog.LogDebug(
                    LogCategory,
                    $"Saved {results.Count} discovery results to {_filePath}",
                    _logger);

                return true;
            }

            // Broader filter than IOException/JsonException/UnauthorizedAccessException because AtomicFile.WriteAllText can also surface SecurityException, NotSupportedException, and ArgumentException from the OS path layer.
            catch (Exception ex) when (ex is IOException
                                        or JsonException
                                        or UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or NotSupportedException
                                        or ArgumentException)
            {
                _pluginLog.LogWarning(
                    LogCategory,
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
    ///     Releases the SemaphoreSlim used to serialise cache access. The DI container (Microsoft.Extensions.DependencyInjection) owns this service's lifetime and will call Dispose when the plugin is torn down.
    /// </summary>
    public void Dispose()
    {
        _fileLock.Dispose();
    }
}
