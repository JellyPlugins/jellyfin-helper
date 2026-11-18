using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
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
    private readonly Dictionary<Guid, DateTimeOffset> _baselineCutoffs = [];
    private bool _enabled;

    /// <summary>Initializes a new instance of the <see cref="YearlyRewindService"/> class.</summary>
    /// <param name="configuration">Annual display settings.</param>
    /// <param name="paths">Server data directory beside statistics and activity data.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public YearlyRewindService(IPluginConfigurationService configuration, IApplicationPaths paths, ILogger<YearlyRewindService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
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
            if (config?.YearlyRewindEnabled != true)
            {
                return false;
            }

            var cutoff = YearlyRewindWindow.LatestSnapshotAt(now, config.YearlyRewindStartDate, TimeZoneInfo.Local);
            return !_enabled || users.Any(user => !_baselineCutoffs.TryGetValue(user.Id, out var baseline) || baseline < cutoff);
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

            var cutoff = YearlyRewindWindow.LatestSnapshotAt(now, config.YearlyRewindStartDate, TimeZoneInfo.Local);
            if (hasNewBaseline)
            {
                foreach (var userId in users.Select(user => user.Id))
                {
                    _baselineCutoffs[userId] = cutoff;
                }
            }

            _enabled = true;
            foreach (var userId in users.Where(user => !user.HasPermission(PermissionKind.IsDisabled)).Select(user => user.Id))
            {
                PublishForUser(userId, now, cutoff, config, history);
            }
        }
    }

    private void PublishForUser(Guid userId, DateTimeOffset now, DateTimeOffset cutoff, PluginConfiguration config, Func<Guid, UserActivityHistory> history)
    {
        if (!_baselineCutoffs.TryGetValue(userId, out var baseline) || baseline < cutoff
            || (_checkedCutoffs.TryGetValue(userId, out var checkedAt) && checkedAt >= cutoff))
        {
            return;
        }

        try
        {
            var current = RecoverPending(userId, cutoff);

            if (NeedsPublication(current, cutoff))
            {
                var facts = history(userId);
                if (current is not null || facts.Coverage.Any(period => period.StartUtc < cutoff)
                    || facts.Observations.Any(row => row.LastPlayedAtUtc < cutoff)
                    || YearlyRewindWindow.IsTabVisible(now, config.YearlyRewindStartDate, config.YearlyRewindEndDate, TimeZoneInfo.Local))
                {
                    current = CreateAndPublish(userId, facts, cutoff, now, config.YearlyRewindStartDate, current);
                }
            }

            if (current is not null)
            {
                _checkedCutoffs[userId] = cutoff;
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "YearlyRewind publication failed for user {UserId}; the previous report is retained.", userId);
        }
    }

    private YearlyRewindSnapshot CreateAndPublish(Guid userId, UserActivityHistory facts, DateTimeOffset cutoff, DateTimeOffset now, string startDate, YearlyRewindSnapshot? current)
    {
        if (!_sealed.TryGetValue(userId, out var report) || report.SnapshotAtUtc != cutoff)
        {
            var start = YearlyRewindWindow.PeriodStart(cutoff, startDate, TimeZoneInfo.Local);
            report = YearlyRewindAggregator.Build(userId, facts, start, cutoff, now, TimeZoneInfo.Local, current);
            _sealed[userId] = report;
        }

        _pending.Seal(report);
        _reports.Publish(report);
        _pending.Complete(userId);
        _sealed.Remove(userId);
        return report;
    }

    private YearlyRewindSnapshot? RecoverPending(Guid userId, DateTimeOffset cutoff)
    {
        var current = _reports.Load(userId);
        var pending = _pending.LoadPending(userId);
        if (pending is null)
        {
            return current;
        }

        if (pending.SnapshotAtUtc >= cutoff)
        {
            _reports.Publish(pending);
        }

        _pending.Complete(userId);
        return _reports.Load(userId);
    }

    private static bool NeedsPublication(YearlyRewindSnapshot? current, DateTimeOffset cutoff) =>
        (current is null || current.SnapshotAtUtc < cutoff)
        && (current is null || TimeZoneInfo.ConvertTime(current.SnapshotAtUtc, TimeZoneInfo.FindSystemTimeZoneById(current.TimeZoneId)).Year
            < TimeZoneInfo.ConvertTime(cutoff, TimeZoneInfo.Local).Year);

    IEnumerable<Guid> IUserActivityHistoryConsumer.KnownUsers() => _reports.KnownUsers().Concat(_pending.KnownUsers()).Distinct();

    void IUserActivityHistoryConsumer.DeleteUser(Guid userId)
    {
        lock (_gate)
        {
            _reports.DeleteUser(userId);
            _pending.DeleteUser(userId);
            _checkedCutoffs.Remove(userId);
            _baselineCutoffs.Remove(userId);
            _sealed.Remove(userId);
        }
    }
}
