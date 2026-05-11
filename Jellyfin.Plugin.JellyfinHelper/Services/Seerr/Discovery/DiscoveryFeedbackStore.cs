using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     File-based persistence for discovery feedback data.
///     Stores per-user interaction history (shown, dismissed, requested, watched)
///     in a JSON file for consumption by the training data builder.
/// </summary>
public sealed class DiscoveryFeedbackStore : IDiscoveryFeedbackStore
{
    private const string FileName = "jellyfin-helper-discovery-feedback.json";

    /// <summary>
    ///     Maximum allowed file size for the feedback store (30 MB).
    ///     Files exceeding this size are treated as corrupted and deleted.
    /// </summary>
    private const long MaxFileSizeBytes = 30 * 1024 * 1024;

    /// <summary>
    ///     Maximum number of feedback entries retained per user.
    ///     Prevents unbounded growth over time. Oldest entries (by ShownAtUtc) are evicted first.
    /// </summary>
    private const int MaxEntriesPerUser = 200;

    /// <summary>
    ///     Maximum age (in days) of feedback entries before they are evicted.
    ///     Entries older than this are removed during save to prevent stale data accumulation.
    /// </summary>
    private const int MaxEntryAgeDays = 365;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;
    private readonly Lock _fileLock = new();
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryFeedbackStore> _logger;

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

        var dataPath = Plugin.Instance?.DataFolderPath ?? string.Empty;
        _filePath = Path.Join(dataPath, FileName);
    }

    /// <inheritdoc />
    public void RecordShown(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, userName);

            // Build a lookup of existing entries by composite key (TmdbId, MediaType) for O(1) dedup checks.
            // This prevents TMDb ID collisions between movies and TV shows (e.g., Movie #550 vs TV #550).
            var existingKeys = new HashSet<(int TmdbId, string MediaType)>(
                userResult.Entries.Select(e => (e.TmdbId, e.MediaType)));
            var now = DateTime.UtcNow;

            foreach (var item in items)
            {
                var key = (item.TmdbId, item.MediaType);
                if (existingKeys.Contains(key))
                {
                    // Backfill metadata on existing placeholder entries (created by RecordDismissed/RecordRequested
                    // before RecordShown ran). This ensures training examples have full feature data.
                    var existing = userResult.Entries.First(e => e.TmdbId == item.TmdbId && e.MediaType == item.MediaType);
                    if (string.IsNullOrEmpty(existing.Title))
                    {
                        existing.Title = item.Title;
                        existing.Year = item.Year;
                        existing.Genres = item.Genres;
                        existing.TmdbRating = item.TmdbRating;
                        existing.Score = item.Score;
                    }

                    continue;
                }

                userResult.Entries.Add(new DiscoveryFeedbackEntry
                {
                    TmdbId = item.TmdbId,
                    MediaType = item.MediaType,
                    Title = item.Title,
                    Year = item.Year,
                    Genres = item.Genres,
                    TmdbRating = item.TmdbRating,
                    Score = item.Score,
                    ShownAtUtc = now
                });
                existingKeys.Add(key);
            }

            SaveInternal(data);
        }
    }

    /// <inheritdoc />
    public void RecordDismissed(Guid userId, int tmdbId, string mediaType)
    {
        if (tmdbId <= 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = data.FirstOrDefault(r => r.UserId == userId);
            if (userResult == null)
            {
                userResult = new DiscoveryFeedbackResult { UserId = userId };
                data.Add(userResult);
            }

            var entry = userResult.Entries.FirstOrDefault(e => e.TmdbId == tmdbId && e.MediaType == mediaType);
            if (entry == null)
            {
                entry = new DiscoveryFeedbackEntry
                {
                    TmdbId = tmdbId,
                    MediaType = mediaType,
                    ShownAtUtc = DateTime.UtcNow
                };
                userResult.Entries.Add(entry);
            }

            entry.DismissedAtUtc = DateTime.UtcNow;

            SaveInternal(data);
        }
    }

    /// <inheritdoc />
    public void RecordRequested(Guid userId, int tmdbId, string mediaType)
    {
        if (tmdbId <= 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = data.FirstOrDefault(r => r.UserId == userId);
            if (userResult == null)
            {
                userResult = new DiscoveryFeedbackResult { UserId = userId };
                data.Add(userResult);
            }

            var entry = userResult.Entries.FirstOrDefault(e => e.TmdbId == tmdbId && e.MediaType == mediaType);
            if (entry == null)
            {
                entry = new DiscoveryFeedbackEntry
                {
                    TmdbId = tmdbId,
                    MediaType = mediaType,
                    ShownAtUtc = DateTime.UtcNow
                };
                userResult.Entries.Add(entry);
            }

            entry.RequestedAtUtc = DateTime.UtcNow;

            SaveInternal(data);
        }
    }

    /// <inheritdoc />
    public void MarkWatched(Guid userId, IReadOnlySet<(int TmdbId, string MediaType)> watchedItems)
    {
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

            var modified = false;
            foreach (var entry in userResult.Entries.Where(
                entry => entry.RequestedAtUtc.HasValue
                         && !entry.WasWatched
                         && watchedItems.Contains((entry.TmdbId, entry.MediaType))))
            {
                entry.WasWatched = true;
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
                Score = e.Score,
                KnownPeople = e.KnownPeople?.ToList() ?? [],
                ShownAtUtc = e.ShownAtUtc,
                DismissedAtUtc = e.DismissedAtUtc,
                RequestedAtUtc = e.RequestedAtUtc,
                WasWatched = e.WasWatched
            }).ToList()
        };
    }

    /// <summary>
    ///     Loads the feedback data from the in-memory cache or disk.
    ///     Must be called under <see cref="_fileLock"/>.
    /// </summary>
    private List<DiscoveryFeedbackResult> LoadInternal()
    {
        if (_memoryCache != null)
        {
            return _memoryCache;
        }

        try
        {
            if (!File.Exists(_filePath))
            {
                _memoryCache = [];
                return _memoryCache;
            }

            var fileInfo = new FileInfo(_filePath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                _pluginLog.LogWarning(
                    "DiscoveryFeedback",
                    $"Feedback file exceeds {MaxFileSizeBytes / (1024 * 1024)}MB ({fileInfo.Length} bytes). Deleting and returning empty.",
                    null,
                    _logger);
                TryDeleteFile();
                _memoryCache = [];
                return _memoryCache;
            }

            var json = File.ReadAllText(_filePath);
            _memoryCache = JsonSerializer.Deserialize<List<DiscoveryFeedbackResult>>(json, JsonOptions) ?? [];
            return _memoryCache;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                "DiscoveryFeedback",
                $"Could not load discovery feedback from {_filePath}: {ex.Message}",
                ex,
                _logger);
            if (ex is JsonException)
            {
                TryDeleteFile();
            }

            _memoryCache = [];
            return _memoryCache;
        }
    }

    /// <summary>
    ///     Saves the feedback data to disk using atomic write (temp file + move).
    ///     Applies eviction rules before saving.
    ///     Must be called under <see cref="_fileLock"/>.
    /// </summary>
    private void SaveInternal(List<DiscoveryFeedbackResult> data)
    {
        // Eviction: remove entries older than MaxEntryAgeDays and cap per-user count
        var cutoff = DateTime.UtcNow.AddDays(-MaxEntryAgeDays);
        foreach (var userResult in data)
        {
            // Remove expired entries
            userResult.Entries.RemoveAll(e => e.ShownAtUtc < cutoff);

            // Cap per-user count (keep most recent by ShownAtUtc)
            if (userResult.Entries.Count > MaxEntriesPerUser)
            {
                userResult.Entries = userResult.Entries
                    .OrderByDescending(e => e.ShownAtUtc)
                    .Take(MaxEntriesPerUser)
                    .ToList();
            }
        }

        // Remove users with zero entries after eviction
        data.RemoveAll(r => r.Entries.Count == 0);

        var tempFilePath = _filePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _filePath, overwrite: true);

            // Update memory cache
            _memoryCache = data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            TryDeleteTempFile(tempFilePath);
            // Invalidate the in-memory cache so the next LoadInternal() re-reads from disk.
            _memoryCache = null;
            _pluginLog.LogWarning(
                "DiscoveryFeedback",
                $"Could not save discovery feedback to {_filePath}: {ex.Message}",
                ex,
                _logger);
        }
    }

    /// <summary>
    ///     Gets the existing user result or creates a new one.
    ///     Must be called under <see cref="_fileLock"/>.
    /// </summary>
    private static DiscoveryFeedbackResult GetOrCreateUserResult(
        List<DiscoveryFeedbackResult> data,
        Guid userId,
        string userName)
    {
        var existing = data.FirstOrDefault(r => r.UserId == userId);
        if (existing != null)
        {
            // Update username in case it changed
            existing.UserName = userName;
            return existing;
        }

        var newResult = new DiscoveryFeedbackResult
        {
            UserId = userId,
            UserName = userName
        };
        data.Add(newResult);
        return newResult;
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

    private static void TryDeleteTempFile(string tempFilePath)
    {
        try
        {
            File.Delete(tempFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup
        }
    }
}