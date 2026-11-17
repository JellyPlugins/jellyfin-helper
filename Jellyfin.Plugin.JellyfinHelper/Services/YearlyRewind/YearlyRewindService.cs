using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Publishes annual snapshots from the common activity history without owning a second collector.</summary>
public sealed class YearlyRewindService : IUserActivityHistoryConsumer
{
    private readonly IPluginConfigurationService _configuration;
    private readonly ILogger<YearlyRewindService> _logger;
    private readonly YearlyRewindStore _reports;
    private readonly YearlyRewindPendingStore _pending;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _checkedCutoffs = [];
    private readonly Dictionary<Guid, YearlyRewindSnapshot> _sealed = [];
    private readonly HashSet<Guid> _baselineUsers = [];
    private bool _enabled;
    private DateTimeOffset? _lastCutoff;

    /// <summary>Initializes a new instance of the <see cref="YearlyRewindService"/> class.</summary>
    /// <param name="configuration">Annual display settings.</param>
    /// <param name="paths">Server data directory beside statistics and activity data.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public YearlyRewindService(IPluginConfigurationService configuration, IApplicationPaths paths, ILogger<YearlyRewindService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _reports = new YearlyRewindStore(paths.DataPath);
        _pending = new YearlyRewindPendingStore(paths.DataPath);
    }

    internal YearlyRewindSnapshot? Load(Guid userId)
    {
        lock (_gate)
        {
            return _reports.Load(userId);
        }
    }

    bool IUserActivityHistoryConsumer.NeedsCurrentState(DateTimeOffset now, IReadOnlyCollection<User> users)
    {
        lock (_gate)
        {
            var config = _configuration.IsInitialized ? _configuration.GetConfiguration() : null;
            return config?.YearlyRewindEnabled == true && (!_enabled || !_lastCutoff.HasValue
                || YearlyRewindWindow.LatestSnapshotAt(now, config.YearlyRewindStartDate, TimeZoneInfo.Local) > _lastCutoff
                || users.Any(user => !_baselineUsers.Contains(user.Id)));
        }
    }

    void IUserActivityHistoryConsumer.Tick(DateTimeOffset now, IReadOnlyCollection<User> users, Func<Guid, UserActivityHistory> history, bool hasNewBaseline)
    {
        lock (_gate)
        {
            var config = _configuration.IsInitialized ? _configuration.GetConfiguration() : null;
            if (config?.YearlyRewindEnabled != true)
            {
                _enabled = false;
                return;
            }

            if (!_enabled && !hasNewBaseline)
            {
                return;
            }

            if (hasNewBaseline)
            {
                _baselineUsers.UnionWith(users.Select(user => user.Id));
            }

            _enabled = true;
            var cutoff = YearlyRewindWindow.LatestSnapshotAt(now, config.YearlyRewindStartDate, TimeZoneInfo.Local);
            _lastCutoff = cutoff;
            foreach (var user in users.Where(user => !user.HasPermission(PermissionKind.IsDisabled)))
            {
                if (!_baselineUsers.Contains(user.Id))
                {
                    continue;
                }

                if (_checkedCutoffs.TryGetValue(user.Id, out var checkedAt) && checkedAt >= cutoff)
                {
                    continue;
                }

                try
                {
                    var current = _reports.Load(user.Id);
                    var pending = _pending.LoadPending(user.Id);
                    if (pending is not null)
                    {
                        if (pending.SnapshotAtUtc >= cutoff)
                        {
                            _reports.Publish(pending);
                        }

                        _pending.Complete(user.Id);
                        current = _reports.Load(user.Id);
                    }

                    if ((current is null || current.SnapshotAtUtc < cutoff)
                        && (current is null || TimeZoneInfo.ConvertTime(current.SnapshotAtUtc, TimeZoneInfo.FindSystemTimeZoneById(current.TimeZoneId)).Year
                            < TimeZoneInfo.ConvertTime(cutoff, TimeZoneInfo.Local).Year))
                    {
                        var facts = history(user.Id);
                        if (current is not null || facts.Coverage.Any(period => period.StartUtc < cutoff)
                            || facts.Observations.Any(row => row.LastPlayedAtUtc < cutoff)
                            || YearlyRewindWindow.IsTabVisible(now, config.YearlyRewindStartDate, config.YearlyRewindEndDate, TimeZoneInfo.Local))
                        {
                            if (!_sealed.TryGetValue(user.Id, out var report) || report.SnapshotAtUtc != cutoff)
                            {
                                var start = YearlyRewindWindow.PeriodStart(cutoff, config.YearlyRewindStartDate, TimeZoneInfo.Local);
                                report = YearlyRewindAggregator.Build(user.Id, facts, start, cutoff, now, TimeZoneInfo.Local, current);
                                _sealed[user.Id] = report;
                            }

                            _pending.Seal(report);
                            _reports.Publish(report);
                            _pending.Complete(user.Id);
                            _sealed.Remove(user.Id);
                            current = report;
                        }
                    }

                    if (current is not null)
                    {
                        _checkedCutoffs[user.Id] = cutoff;
                    }
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    _logger.LogWarning(ex, "YearlyRewind publication failed for user {UserId}; the previous report is retained.", user.Id);
                }
            }
        }
    }

    IEnumerable<Guid> IUserActivityHistoryConsumer.KnownUsers() => _reports.KnownUsers().Concat(_pending.KnownUsers()).Distinct();

    void IUserActivityHistoryConsumer.DeleteUser(Guid userId)
    {
        lock (_gate)
        {
            _reports.DeleteUser(userId);
            _pending.DeleteUser(userId);
            _checkedCutoffs.Remove(userId);
            _baselineUsers.Remove(userId);
            _sealed.Remove(userId);
        }
    }
}
