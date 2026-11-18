using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>One shared collector for measured playback and observed Jellyfin state changes.</summary>
internal sealed class UserActivityHistoryService : BackgroundService, IUserActivityHistoryRecorder
{
    private readonly IPluginConfigurationService _configuration;
    private readonly ISessionManager _sessions;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly VirtualItemDetector _virtualItems;
    private readonly IUserActivityInsightsService _insights;
    private readonly IUserActivityCacheService _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserActivityHistoryService> _logger;
    private readonly UserActivityHistoryStore _store;
    private readonly IUserActivityHistoryConsumer[] _consumers;
    private readonly UserPlaybackTracker _tracker = new();
    private readonly Dictionary<Guid, UserActivityHistory> _histories = [];
    private readonly Dictionary<Guid, DateTimeOffset> _historyRetryAt = [];
    private readonly Dictionary<Guid, UserActivityCoverage> _coverage = [];
    private readonly HashSet<Guid> _dirty = [];
    private readonly Lock _gate = new();
    private bool _initialized;
    private DateTimeOffset _lastTick;
    private DateTimeOffset _lastFlush;
    private DateTimeOffset _nextRefresh;
    private DateTimeOffset _retryRefresh;
    private DateTimeOffset _nextCleanup;

    public UserActivityHistoryService(
        IPluginConfigurationService configuration,
        ISessionManager sessions,
        IUserManager users,
        ILibraryManager library,
        VirtualItemDetector virtualItems,
        IUserActivityInsightsService insights,
        IUserActivityCacheService cache,
        IApplicationPaths paths,
        TimeProvider clock,
        IEnumerable<IUserActivityHistoryConsumer> consumers,
        ILogger<UserActivityHistoryService> logger)
    {
        _configuration = configuration;
        _sessions = sessions;
        _users = users;
        _library = library;
        _virtualItems = virtualItems;
        _insights = insights;
        _cache = cache;
        _clock = clock;
        _consumers = consumers.ToArray();
        _logger = logger;
        _store = new UserActivityHistoryStore(paths.DataPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStop;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    lock (_gate)
                    {
                        Tick(_clock.GetUtcNow());
                    }
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    _logger.LogWarning(ex, "Shared activity history update failed.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _sessions.PlaybackStart -= OnStart;
            _sessions.PlaybackProgress -= OnProgress;
            _sessions.PlaybackStopped -= OnStop;
            lock (_gate)
            {
                _tracker.Clear();
                Flush();
            }
        }
    }

    internal void Tick(DateTimeOffset now, bool allowRefresh = true)
    {
        if (!_configuration.IsInitialized)
        {
            return;
        }

        var users = _users.GetUsers().ToArray();
        if (_lastTick == default || now - _lastTick > TimeSpan.FromSeconds(35))
        {
            _tracker.Clear();
            _coverage.Clear();
        }

        _lastTick = now;
        foreach (var period in _coverage.Values)
        {
            period.EndUtc = now;
        }

        var availableUsers = users.Where(user => !user.HasPermission(PermissionKind.IsDisabled) && TryHistory(user.Id, now, out _)).ToArray();
        var needsCurrent = _consumers.Any(consumer => consumer.NeedsCurrentState(now, availableUsers));
        var refreshed = false;
        if (allowRefresh && (!_initialized || now >= _nextRefresh || needsCurrent) && now >= _retryRefresh)
        {
            refreshed = Refresh(now);
        }

        if (refreshed)
        {
            Flush();
        }

        if (_initialized)
        {
            foreach (var consumer in _consumers)
            {
                consumer.Tick(now, availableUsers, History, refreshed);
            }
        }

        ExtendCoverage(availableUsers.Select(user => user.Id), now);
        CleanupDeletedUsers(users, now);

        if (now - _lastFlush >= TimeSpan.FromSeconds(30))
        {
            Flush();
            _lastFlush = now;
        }
    }

    private void ExtendCoverage(IEnumerable<Guid> userIds, DateTimeOffset now)
    {
        foreach (var id in userIds)
        {
            var history = History(id);
            if (!_coverage.ContainsKey(id))
            {
                var period = new UserActivityCoverage { StartUtc = now, EndUtc = now };
                history.Coverage.Add(period);
                _coverage[id] = period;
            }

            _dirty.Add(id);
        }
    }

    private void CleanupDeletedUsers(IEnumerable<User> users, DateTimeOffset now)
    {
        if (now >= _nextCleanup)
        {
            var ids = users.Select(user => user.Id).ToHashSet();
            foreach (var id in _store.KnownUsers().Concat(_histories.Keys).Concat(_consumers.SelectMany(consumer => consumer.KnownUsers())).Distinct().Where(id => !ids.Contains(id)).ToArray())
            {
                _store.DeleteUser(id);
                _histories.Remove(id);
                _historyRetryAt.Remove(id);
                _coverage.Remove(id);
                _dirty.Remove(id);
                foreach (var consumer in _consumers)
                {
                    consumer.DeleteUser(id);
                }
            }

            // Keep the journal: retention and storage tuning are deliberately separate from this change.
            _nextCleanup = now.AddDays(1);
        }
    }

    private bool Refresh(DateTimeOffset now)
    {
        var refreshed = false;
        try
        {
            var cached = _cache.LoadResult();
            if (cached is not null)
            {
                Apply(cached, now);
                _initialized = true;
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "Could not import the latest activity cache; trying current Jellyfin data.");
        }

        try
        {
            var snapshot = _insights.BuildActivityReport();
            Apply(snapshot, now);
            _initialized = true;
            refreshed = true;
            _nextRefresh = now.AddHours(1);
            _retryRefresh = now;
            try
            {
                _cache.SaveResult(snapshot);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _logger.LogWarning(ex, "Could not update the latest activity cache; the shared journal is retained.");
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _retryRefresh = now.AddMinutes(1);
            _logger.LogWarning(ex, "Could not refresh the Jellyfin activity baseline.");
        }

        return refreshed;
    }

    public void Record(UserActivityResult snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!_configuration.IsInitialized)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            Tick(now, false); // A due cutoff waits for this task snapshot before its first publication.
            Apply(snapshot, now);
            _initialized = true;
            foreach (var consumer in _consumers)
            {
                consumer.Tick(now, _users.GetUsers().Where(user => !user.HasPermission(PermissionKind.IsDisabled) && TryHistory(user.Id, now, out _)).ToArray(), History, true);
            }

            Flush();
        }
    }

    private void Apply(UserActivityResult snapshot, DateTimeOffset now)
    {
        var source = new DateTimeOffset(snapshot.GeneratedAt).ToUniversalTime();
        if (source > now)
        {
            source = now;
        }

        foreach (var row in snapshot.Items)
        {
            ApplyItem(row, now, source);
        }
    }

    private void ApplyItem(UserActivitySummary row, DateTimeOffset now, DateTimeOffset source)
    {
        if (row.ItemId == Guid.Empty || string.IsNullOrWhiteSpace(row.ItemType))
        {
            return;
        }

        var existing = _library.GetItemById(row.ItemId);
        if (existing is not null && _virtualItems.IsVirtual(existing))
        {
            return;
        }

        var seriesId = row.SeriesId ?? (existing as Episode)?.SeriesId;
        var metadata = new UserPlaybackItem
        {
            ItemId = row.ItemId,
            Name = row.ItemName,
            ItemType = row.ItemType,
            RuntimeTicks = Math.Max(0, row.RuntimeTicks),
            SeriesId = seriesId == Guid.Empty ? null : seriesId,
            SeriesName = row.SeriesName,
            Genres = new Collection<string>(row.Genres ?? [])
        };
        foreach (var state in row.UserActivities)
        {
            RecordState(metadata, state, existing, now, source);
        }
    }

    private void RecordState(UserPlaybackItem metadata, UserItemActivity state, BaseItem? existing, DateTimeOffset now, DateTimeOffset source)
    {
        var user = _users.GetUserById(state.UserId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled) || (existing is not null && !existing.IsVisible(user)))
        {
            return;
        }

        if (TryHistory(user.Id, now, out var history) && history.Record(metadata, state, now, source))
        {
            _dirty.Add(user.Id);
        }
    }

    private bool TryHistory(Guid userId, DateTimeOffset now, out UserActivityHistory history)
    {
        history = null!;
        if (_historyRetryAt.TryGetValue(userId, out var retryAt) && now < retryAt)
        {
            return false;
        }

        try
        {
            history = History(userId);
            _historyRetryAt.Remove(userId);
            return true;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _coverage.Remove(userId);
            _historyRetryAt[userId] = now.AddMinutes(1);
            _logger.LogWarning(ex, "Could not load activity history for user {UserId}; other users continue to be recorded.", userId);
            return false;
        }
    }

    private UserActivityHistory History(Guid userId)
    {
        if (!_histories.TryGetValue(userId, out var history))
        {
            history = _store.Load(userId);
            _histories[userId] = history;
        }

        return history;
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs args) => Observe(args, true, false);

    private void OnProgress(object? sender, PlaybackProgressEventArgs args) => Observe(args, false, false);

    private void OnStop(object? sender, PlaybackStopEventArgs args) => Observe(args, false, true);

    private void Observe(PlaybackProgressEventArgs args, bool start, bool stop)
    {
        try
        {
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (!_configuration.IsInitialized)
                {
                    return;
                }

                Tick(now, false);
                if (args.Item is not Video video || _virtualItems.IsVirtual(args.Item) || args.IsAutomated)
                {
                    return;
                }

                var episode = args.Item as Episode;
                var genres = args.Item.Genres;
                if (genres.Length == 0 && episode?.Series is { } parent)
                {
                    genres = parent.Genres;
                }

                var metadata = new UserPlaybackItem
                {
                    ItemId = args.Item.Id,
                    Name = args.Item.Name ?? string.Empty,
                    ItemType = args.Item.GetType().Name,
                    SeriesId = episode?.SeriesId == Guid.Empty ? null : episode?.SeriesId,
                    SeriesName = episode?.SeriesName,
                    RuntimeTicks = Math.Max(0, args.Item.RunTimeTicks ?? 0),
                    Genres = new Collection<string>(genres)
                };
                var session = string.IsNullOrEmpty(args.PlaySessionId) ? args.Session?.Id : args.PlaySessionId;
                if (string.IsNullOrEmpty(session))
                {
                    return;
                }

                var progress = new UserPlaybackTracker.Progress(args.PlaybackPositionTicks, args.IsPaused, args.IsAutomated, start, stop, now);
                foreach (var user in args.Users.DistinctBy(user => user.Id))
                {
                    RecordPlayback(user, video, metadata, session, progress);
                }

                if (stop)
                {
                    Flush();
                }
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            lock (_gate)
            {
                foreach (var user in args.Users)
                {
                    _coverage.Remove(user.Id);
                }
            }

            _logger.LogWarning(ex, "Could not record shared playback activity.");
        }
    }

    private void RecordPlayback(User user, Video item, UserPlaybackItem metadata, string session, UserPlaybackTracker.Progress progress)
    {
        if (user.HasPermission(PermissionKind.IsDisabled) || !item.IsVisible(user) || !TryHistory(user.Id, progress.At, out var history))
        {
            return;
        }

        _tracker.Observe(user.Id, session, metadata, item.RunTimeTicks, progress, history);
        _dirty.Add(user.Id);
    }

    private void Flush()
    {
        foreach (var id in _dirty.ToArray())
        {
            try
            {
                _store.Save(id, _histories[id]);
                _dirty.Remove(id);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _coverage.Remove(id);
                _logger.LogWarning(ex, "Could not persist shared activity history for user {UserId}.", id);
            }
        }
    }
}
