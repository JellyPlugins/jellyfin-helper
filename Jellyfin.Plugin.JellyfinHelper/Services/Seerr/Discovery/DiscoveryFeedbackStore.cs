using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     File-based persistence for discovery feedback data. Stores per-user interaction history (shown, dismissed, requested, watched) in a JSON file for consumption by the training data builder.
/// </summary>
public sealed class DiscoveryFeedbackStore : IDiscoveryFeedbackStore
{
    private const string LogSource = "DiscoveryFeedback";
    private const string FileName = "jellyfin-helper-discovery-feedback.json";

    /// <summary>
    ///     Maximum allowed file size for the feedback store (30 MB).
    ///     Oversized valid stores are compacted without dropping all users' feedback.
    /// </summary>
    private const long MaxFileSizeBytes = 30 * 1024 * 1024;

    private const long MaxRecoveryFileSizeBytes = 4 * MaxFileSizeBytes;

    /// <summary>
    ///     Maximum number of feedback entries retained per user.
    ///     Prevents unbounded growth over time. Oldest entries (by ShownAtUtc) are evicted first.
    /// </summary>
    private const int MaxEntriesPerUser = 200;

    /// <summary>
    ///     Maximum age (in days) of feedback entries before they are evicted. Entries older than this are removed during save to prevent stale data accumulation.
    /// </summary>
    private const int MaxEntryAgeDays = 365;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;
    private readonly Lock _fileLock = new();
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryFeedbackStore> _logger;
    private readonly long _sizeLimit = MaxFileSizeBytes;
    private bool _recoveryBlocked;

    /// <summary>
    ///     In-memory cache of feedback data. Avoids repeated disk reads.
    ///     Invalidated on mutation operations.
    /// </summary>
    private List<DiscoveryFeedbackResult>? _memoryCache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryFeedbackStore"/> class.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public DiscoveryFeedbackStore(
        IPluginLogService pluginLog,
        ILogger<DiscoveryFeedbackStore> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;

        var dataPath = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("Plugin.Instance is null; DiscoveryFeedbackStore cannot determine its data folder path.");
        _filePath = Path.Join(dataPath, FileName);
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryFeedbackStore"/> class
    ///     with an explicit data directory path. Used for test isolation.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="dataFolderPath">The directory path where the feedback file will be stored.</param>
    /// <param name="sizeLimit">Maximum serialized UTF-8 size; the smaller override supports isolated boundary tests.</param>
    internal DiscoveryFeedbackStore(
        IPluginLogService pluginLog,
        ILogger<DiscoveryFeedbackStore> logger,
        string dataFolderPath,
        long sizeLimit = MaxFileSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeLimit);
        _pluginLog = pluginLog;
        _logger = logger;
        _filePath = Path.Join(dataFolderPath, FileName);
        _sizeLimit = sizeLimit;
    }

    /// <inheritdoc />
    public void RecordShown(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items)
        => RecordGenerated(userId, userName, items);

    /// <inheritdoc />
    public void RecordGenerated(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items, DateTime? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, userName);

            // Build a lookup of existing entries by composite key (TmdbId, MediaType) for O(1) dedup + backfill.
            // This prevents TMDb ID collisions between movies and TV shows (e.g., Movie #550 vs TV #550).
            var entryLookup = new Dictionary<(int TmdbId, string MediaType), DiscoveryFeedbackEntry>(
                userResult.Entries.Count);
            foreach (var entry in userResult.Entries)
            {
                entryLookup.TryAdd((entry.TmdbId, NormalizeMediaType(entry.MediaType)), entry);
            }

            var now = DateTimeNormalization.ToUtc(generatedAtUtc ?? DateTime.UtcNow);
            var modified = false;

            foreach (var item in items)
            {
                var normalizedType = NormalizeMediaType(item.MediaType);
                var key = (item.TmdbId, normalizedType);
                if (entryLookup.TryGetValue(key, out var existing))
                {
                    if (existing.GetStatus() == DiscoveryInteractionStatus.Generated)
                    {
                        existing.ScoringFeatures = null;
                        existing.ShownAtUtc = now;
                        modified = true;
                    }

                    modified |= BackfillExistingEntry(existing, item);
                    continue;
                }

                var newEntry = new DiscoveryFeedbackEntry
                {
                    TmdbId = item.TmdbId,
                    MediaType = normalizedType,
                    Title = item.Title,
                    Year = item.Year,
                    Genres = item.Genres?.ToArray() ?? [],
                    TmdbRating = item.TmdbRating,
                    Popularity = item.Popularity,
                    Score = item.Score,
                    // Exact card features already live in the recommendation cache and delivery evidence.
                    // An unseen generation is not training evidence and need not duplicate those features here.
                    ScoringFeatures = null,
                    ShownAtUtc = now,
                    KnownPeople = item.KnownPeople?.ToList()
                };
                userResult.Entries.Add(newEntry);
                entryLookup.TryAdd(key, newEntry);
                modified = true;
            }

            if (modified)
            {
                SaveInternal(data);
            }
        }
    }

    /// <inheritdoc />
    public void RecordImpressions(Guid userId, IReadOnlyList<DiscoveryRecommendation> items, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, string.Empty);
            var modified = false;
            foreach (var item in items)
            {
                var mediaType = NormalizeMediaType(item.MediaType);
                var entry = userResult.Entries.FirstOrDefault(e => e.TmdbId == item.TmdbId && e.MediaType == mediaType);
                if (entry is null)
                {
                    entry = new DiscoveryFeedbackEntry { TmdbId = item.TmdbId, MediaType = mediaType };
                    userResult.Entries.Add(entry);
                }

                if (entry.ImpressionAtUtc.HasValue)
                {
                    continue;
                }

                // A verified action can establish the prediction before any viewport acknowledgement.
                // Later impressions may record visibility but must not rewrite that action's evidence.
                ConfirmPrediction(entry, item, generatedAtUtc);
                entry.ImpressionAtUtc = DateTime.UtcNow;
                modified = true;
            }

            if (modified && !SaveInternal(data))
            {
                throw new IOException("Could not persist confirmed discovery impressions.");
            }
        }
    }

    /// <summary>
    ///     Backfills metadata on an existing placeholder entry (created by RecordDismissed/RecordRequested before RecordShown ran) or an entry missing enriched data (e.g., KnownPeople not available on first generation but enriched on a subsequent run).
    /// </summary>
    /// <param name="existing">The existing feedback entry to backfill in place.</param>
    /// <param name="item">The freshly shown recommendation providing candidate values.</param>
    /// <returns><see langword="true"/> if any field was modified; otherwise <see langword="false"/>.</returns>
    private static bool BackfillExistingEntry(DiscoveryFeedbackEntry existing, DiscoveryRecommendation item)
    {
        var modified = false;

        if (string.IsNullOrEmpty(existing.Title) && !string.IsNullOrEmpty(item.Title))
        {
            existing.Title = item.Title;
            modified = true;
        }

        if (existing.Year is null or 0 && item.Year is > 0)
        {
            existing.Year = item.Year;
            modified = true;
        }

        if ((existing.Genres == null || existing.Genres.Count == 0) && item.Genres is { Count: > 0 })
        {
            existing.Genres = item.Genres.ToArray();
            modified = true;
        }

        // Zero is an exact missing-metadata sentinel; a small positive rating is still populated.
        if (existing.TmdbRating.Equals(0.0) && item.TmdbRating > 0)
        {
            existing.TmdbRating = item.TmdbRating;
            modified = true;
        }

        modified |= BackfillNumericAndPeople(existing, item);

        return modified;
    }

    /// <summary>
    ///     Backfills missing score, popularity, and credits without replacing an observed empty cast.
    /// </summary>
    /// <param name="existing">The existing feedback entry to backfill in place.</param>
    /// <param name="item">The freshly shown recommendation providing candidate values.</param>
    /// <returns><see langword="true"/> if any field was modified; otherwise <see langword="false"/>.</returns>
    private static bool BackfillNumericAndPeople(DiscoveryFeedbackEntry existing, DiscoveryRecommendation item)
    {
        var modified = false;

        // Only the exact zero sentinel may be backfilled, not valid near-zero scores or popularity.
        if (existing.Score.Equals(0.0) && item.Score > 0)
        {
            existing.Score = item.Score;
            modified = true;
        }

        if (existing.Popularity.Equals(0.0) && item.Popularity > 0)
        {
            existing.Popularity = item.Popularity;
            modified = true;
        }

        if (existing.KnownPeople is null && item.KnownPeople is not null)
        {
            existing.KnownPeople = item.KnownPeople.ToList();
            modified = true;
        }

        return modified;
    }

    /// <inheritdoc />
    public void RecordDismissed(Guid userId, int tmdbId, string mediaType)
        => RecordAction(userId, tmdbId, mediaType, requested: false, item: null, generatedAtUtc: null);

    /// <inheritdoc />
    public void RecordDismissed(Guid userId, DiscoveryRecommendation item, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        RecordAction(userId, item.TmdbId, item.MediaType, requested: false, item, generatedAtUtc);
    }

    /// <inheritdoc />
    public void RecordRequested(Guid userId, int tmdbId, string mediaType)
        => RecordAction(userId, tmdbId, mediaType, requested: true, item: null, generatedAtUtc: null);

    /// <inheritdoc />
    public void RecordRequested(Guid userId, DiscoveryRecommendation item, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        RecordAction(userId, item.TmdbId, item.MediaType, requested: true, item, generatedAtUtc);
    }

    private void RecordAction(Guid userId, int tmdbId, string mediaType, bool requested, DiscoveryRecommendation? item, DateTime? generatedAtUtc)
    {
        if (tmdbId <= 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, userName: null);
            var normalizedType = NormalizeMediaType(mediaType);
            var entry = GetOrCreateEntry(userResult, tmdbId, normalizedType);
            if (item is not null && generatedAtUtc.HasValue)
            {
                ConfirmPrediction(entry, item, generatedAtUtc.Value);
            }
            else if (!entry.HasConfirmedPrediction)
            {
                // Legacy/non-card actions have no delivery proof. Do not attribute them to an unseen generation.
                entry.ScoringFeatures = null;
                entry.ShownAtUtc = DateTime.UtcNow;
            }

            if (requested)
            {
                entry.RequestedAtUtc = DateTime.UtcNow;
            }
            else
            {
                entry.DismissedAtUtc = DateTime.UtcNow;
            }

            SaveInternal(data);
        }
    }

    private static void ConfirmPrediction(DiscoveryFeedbackEntry entry, DiscoveryRecommendation item, DateTime generatedAtUtc)
    {
        if (entry.HasConfirmedPrediction)
        {
            return;
        }

        entry.Title = item.Title;
        entry.Year = item.Year;
        entry.Genres = item.Genres.ToArray();
        entry.KnownPeople = item.KnownPeople?.ToList();
        entry.TmdbRating = item.TmdbRating;
        entry.Popularity = item.Popularity;
        entry.Score = item.Score;
        entry.ScoringFeatures = item.ScoringFeatures?.Copy();
        entry.ShownAtUtc = DateTimeNormalization.ToUtc(generatedAtUtc);
        entry.PredictionEvidenceAtUtc = DateTime.UtcNow;
    }

    /// <inheritdoc />
    public void MarkWatched(Guid userId, IReadOnlySet<(int TmdbId, string MediaType)> watchedItems)
    {
        ArgumentNullException.ThrowIfNull(watchedItems);

        if (watchedItems.Count == 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = data.FirstOrDefault(r => r.UserId == userId);
            if (userResult == null)
            {
                return;
            }

            // Normalize incoming media types for case-insensitive matching.
            // Stored entries use NormalizeMediaType (lowercase), but callers may pass mixed case.
            var normalizedWatched = new HashSet<(int TmdbId, string MediaType)>(
                watchedItems.Select(w => (w.TmdbId, NormalizeMediaType(w.MediaType))));

            var now = DateTime.UtcNow;
            var modified = false;
            foreach (var entry in userResult.Entries.Where(
                entry => entry.RequestedAtUtc.HasValue
                         && !entry.WasWatched
                         && normalizedWatched.Contains((entry.TmdbId, entry.MediaType))))
            {
                entry.WasWatched = true;
                entry.WatchedAtUtc = now;
                modified = true;
            }

            if (modified)
            {
                SaveInternal(data);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveryFeedbackResult> LoadAll()
    {
        lock (_fileLock)
        {
            var data = LoadInternal();
            return data.Select(CloneResult).ToList().AsReadOnly();
        }
    }

    /// <inheritdoc />
    public DiscoveryFeedbackResult? LoadForUser(Guid userId)
    {
        lock (_fileLock)
        {
            var data = LoadInternal();
            var result = data.FirstOrDefault(r => r.UserId == userId);
            return result == null ? null : CloneResult(result);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<(int TmdbId, string MediaType)> GetDismissedItems(Guid userId)
    {
        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = data.FirstOrDefault(r => r.UserId == userId);
            if (userResult == null)
            {
                return new HashSet<(int, string)>();
            }

            var dismissed = new HashSet<(int TmdbId, string MediaType)>();
            foreach (var entry in userResult.Entries.Where(e => e.DismissedAtUtc.HasValue))
            {
                dismissed.Add((entry.TmdbId, entry.MediaType));
            }

            return dismissed;
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<(int TmdbId, string MediaType)> GetRequestedItems(Guid userId)
    {
        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = data.FirstOrDefault(r => r.UserId == userId);
            if (userResult == null)
            {
                return new HashSet<(int, string)>();
            }

            var requested = new HashSet<(int TmdbId, string MediaType)>();
            foreach (var entry in userResult.Entries.Where(e => e.RequestedAtUtc.HasValue))
            {
                requested.Add((entry.TmdbId, entry.MediaType));
            }

            return requested;
        }
    }

    /// <summary>
    ///     Creates a defensive copy of a feedback result to avoid exposing internal mutable state.
    /// </summary>
    private static DiscoveryFeedbackResult CloneResult(DiscoveryFeedbackResult source)
    {
        return new DiscoveryFeedbackResult
        {
            UserId = source.UserId,
            UserName = source.UserName,
            Entries = source.Entries.Select(e => new DiscoveryFeedbackEntry
            {
                TmdbId = e.TmdbId,
                MediaType = e.MediaType,
                Title = e.Title,
                Year = e.Year,
                Genres = e.Genres?.ToArray() ?? [],
                TmdbRating = e.TmdbRating,
                Popularity = e.Popularity,
                Score = e.Score,
                ScoringFeatures = e.ScoringFeatures?.Copy(),
                ImpressionAtUtc = e.ImpressionAtUtc,
                PredictionEvidenceAtUtc = e.PredictionEvidenceAtUtc,
                PredictionInputsCompacted = e.PredictionInputsCompacted,
                KnownPeople = e.KnownPeople?.ToList(),
                ShownAtUtc = e.ShownAtUtc,
                DismissedAtUtc = e.DismissedAtUtc,
                RequestedAtUtc = e.RequestedAtUtc,
                WasWatched = e.WasWatched,
                WatchedAtUtc = e.WatchedAtUtc
            }).ToList()
        };
    }

    /// <summary>
    ///     Loads the feedback data from the in-memory cache or disk and returns a deep copy.
    /// </summary>
    private List<DiscoveryFeedbackResult> LoadInternal()
    {
        if (_memoryCache != null)
        {
            return _memoryCache.Select(CloneResult).ToList();
        }

        try
        {
            if (!File.Exists(_filePath))
            {
                _memoryCache = [];
                return [];
            }

            var fileInfo = new FileInfo(_filePath);
            if (fileInfo.Length > MaxRecoveryFileSizeBytes)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Feedback file exceeds the bounded recovery limit ({fileInfo.Length} bytes); preserving it and blocking writes.",
                    null,
                    _logger);
                _recoveryBlocked = true;
                _memoryCache = [];
                return [];
            }

            using (var stream = File.OpenRead(_filePath))
            {
                _memoryCache = JsonSerializer.Deserialize<List<DiscoveryFeedbackResult>>(stream, JsonOptions) ?? [];
            }

            if (fileInfo.Length > _sizeLimit)
            {
                _pluginLog.LogWarning(LogSource, $"Feedback file exceeds the size limit ({fileInfo.Length} bytes); recovering and compacting valid feedback.", null, _logger);
                var recovered = _memoryCache;
                SaveInternal(recovered);
                _memoryCache ??= recovered;
            }

            return _memoryCache.Select(CloneResult).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Could not load discovery feedback from {_filePath}: {ex.Message}",
                ex,
                _logger);
            if (ex is JsonException)
            {
                TryDeleteFile();
            }

            _memoryCache = [];
            return [];
        }
    }

    /// <summary>
    ///     Saves the feedback data to disk using atomic write (temp file + move). Applies eviction rules before saving.
    /// </summary>
    private bool SaveInternal(List<DiscoveryFeedbackResult> data)
    {
        if (_recoveryBlocked)
        {
            return false;
        }

        // Work on a deep copy so eviction mutations never touch the live _memoryCache.
        // The copy is swapped into _memoryCache atomically after a successful disk write.
        var workingCopy = data.Select(CloneResult).ToList();

        // Eviction: remove entries older than MaxEntryAgeDays and cap per-user count. Use the latest interaction timestamp (not just ShownAtUtc) to prevent evicting entries that were recently dismissed or requested but originally shown long ago.
        var cutoff = DateTime.UtcNow.AddDays(-MaxEntryAgeDays);
        foreach (var userResult in workingCopy)
        {
            foreach (var entry in userResult.Entries.Where(entry => !entry.HasConfirmedPrediction))
            {
                entry.ScoringFeatures = null;
            }

            // Remove expired entries based on their most recent activity
            userResult.Entries.RemoveAll(e => GetLatestActivityUtc(e) < cutoff);

            // Cap per-user count (keep most recently active entries)
            if (userResult.Entries.Count > MaxEntriesPerUser)
            {
                userResult.Entries = userResult.Entries
                    .OrderByDescending(GetLatestActivityUtc)
                    .Take(MaxEntriesPerUser)
                    .ToList();
            }
        }

        // Remove users with zero entries after eviction
        workingCopy.RemoveAll(r => r.Entries.Count == 0);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = SerializeWithinSizeLimit(workingCopy);
            if (bytes is null)
            {
                _pluginLog.LogWarning(LogSource, "Explicit feedback cannot fit the size limit; preserving the existing file instead of overwriting it.", null, _logger);
                return false;
            }

            // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
            AtomicFile.WriteAllText(_filePath, Encoding.UTF8.GetString(bytes));

            // Atomically replace the live cache with the post-eviction copy.
            _memoryCache = workingCopy;
            return true;
        }

        // Broader filter than plain IOException / UnauthorizedAccessException / JsonException because AtomicFile.WriteAllText can also surface SecurityException, NotSupportedException and ArgumentException (malformed path characters from OS layer).
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or JsonException
                                    or System.Security.SecurityException
                                    or NotSupportedException
                                    or ArgumentException)
        {
            // Invalidate the in-memory cache so the next LoadInternal() re-reads from disk.
            _memoryCache = null;
            _pluginLog.LogWarning(
                LogSource,
                $"Could not save discovery feedback to {_filePath}: {ex.Message}",
                ex,
                _logger);
        }

        return false;
    }

    /// <summary>Evicts generated-only rows first, then compacts older metadata while preserving explicit outcomes and identities.</summary>
    private byte[]? SerializeWithinSizeLimit(List<DiscoveryFeedbackResult> data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        if (bytes.LongLength <= _sizeLimit)
        {
            return bytes;
        }

        foreach (var result in data)
        {
            result.Entries.RemoveAll(entry => entry.GetStatus() == DiscoveryInteractionStatus.Generated);
        }

        data.RemoveAll(result => result.Entries.Count == 0);
        bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        var estimatedSize = bytes.LongLength;
        foreach (var entry in data.SelectMany(result => result.Entries)
                     .OrderBy(entry => entry.RequestedAtUtc.HasValue || entry.DismissedAtUtc.HasValue)
                     .ThenBy(GetLatestActivityUtc))
        {
            if (estimatedSize <= _sizeLimit)
            {
                break;
            }

            var previousSize = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions).LongLength;
            entry.PredictionInputsCompacted = true;
            entry.ScoringFeatures = null;
            entry.Title = string.Empty;
            entry.Year = null;
            entry.Genres = [];
            entry.KnownPeople = null;
            estimatedSize -= previousSize - JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions).LongLength;
        }

        bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        return bytes.LongLength <= _sizeLimit ? bytes : null;
    }

    /// <summary>
    ///     Gets the existing user result or creates a new one.
    ///     Must be called under <see cref="_fileLock"/>.
    /// </summary>
    private static DiscoveryFeedbackResult GetOrCreateUserResult(
        List<DiscoveryFeedbackResult> data,
        Guid userId,
        string? userName)
    {
        var existing = data.FirstOrDefault(r => r.UserId == userId);
        if (existing != null)
        {
            // Update username in case it changed (only when caller supplies one)
            if (userName != null)
            {
                existing.UserName = userName;
            }

            return existing;
        }

        var newResult = new DiscoveryFeedbackResult
        {
            UserId = userId,
            UserName = userName ?? string.Empty
        };
        data.Add(newResult);
        return newResult;
    }

    /// <summary>
    ///     Returns the existing feedback entry for (tmdbId, mediaType), or creates and registers a new one.
    ///     Must be called under <see cref="_fileLock"/>.
    /// </summary>
    private static DiscoveryFeedbackEntry GetOrCreateEntry(
        DiscoveryFeedbackResult userResult,
        int tmdbId,
        string normalizedMediaType)
    {
        foreach (var e in userResult.Entries)
        {
            if (e.TmdbId == tmdbId && e.MediaType == normalizedMediaType)
            {
                return e;
            }
        }

        var entry = new DiscoveryFeedbackEntry
        {
            TmdbId = tmdbId,
            MediaType = normalizedMediaType,
            ShownAtUtc = DateTime.UtcNow
        };
        userResult.Entries.Add(entry);
        return entry;
    }

    /// <summary>
    ///     Returns the most recent activity timestamp for a feedback entry. Used by eviction logic to retain entries with recent interactions even if their original ShownAtUtc is old.
    /// </summary>
    private static DateTime GetLatestActivityUtc(DiscoveryFeedbackEntry entry)
    {
        var latest = entry.ShownAtUtc;
        if (entry.ImpressionAtUtc.HasValue && entry.ImpressionAtUtc.Value > latest)
        {
            latest = entry.ImpressionAtUtc.Value;
        }

        if (entry.DismissedAtUtc.HasValue && entry.DismissedAtUtc.Value > latest)
        {
            latest = entry.DismissedAtUtc.Value;
        }

        if (entry.RequestedAtUtc.HasValue && entry.RequestedAtUtc.Value > latest)
        {
            latest = entry.RequestedAtUtc.Value;
        }

        if (entry.WatchedAtUtc.HasValue && entry.WatchedAtUtc.Value > latest)
        {
            latest = entry.WatchedAtUtc.Value;
        }

        return latest;
    }

    /// <summary>
    ///     Normalizes a media type string to lowercase for consistent lookup/dedup.
    ///     Defensive: handles null/whitespace gracefully.
    /// </summary>
    private string NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return "movie";
        }

        var normalized = mediaType.Trim().ToLowerInvariant();

        if (normalized != "movie" && normalized != "tv")
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Unexpected mediaType value '{Value}' normalized to 'movie'.", mediaType.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' '));
            }

            return "movie";
        }

        return normalized;
    }

    private void TryDeleteFile()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort
        }
    }
}
