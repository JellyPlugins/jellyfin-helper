using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>Reads enabled users and watch data for statistics, with batch-first, per-user failure isolation.</summary>
public sealed class MediaStatisticsUserDataSource
{
    private const string LogCategory = "MediaStatistics";
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<MediaStatisticsUserDataSource> _logger;
    private readonly IUserDataManager? _userDataManager;
    private readonly IUserManager? _userManager;

    /// <summary>Initializes a new instance of the <see cref="MediaStatisticsUserDataSource"/> class.</summary>
    /// <param name="pluginLog">Plugin diagnostics.</param>
    /// <param name="logger">Host diagnostics.</param>
    /// <param name="userDataManager">Optional local watch-data reader.</param>
    /// <param name="userManager">Optional local user enumeration.</param>
    public MediaStatisticsUserDataSource(
        IPluginLogService pluginLog,
        ILogger<MediaStatisticsUserDataSource> logger,
        IUserDataManager? userDataManager = null,
        IUserManager? userManager = null)
    {
        ArgumentNullException.ThrowIfNull(pluginLog);
        ArgumentNullException.ThrowIfNull(logger);
        _pluginLog = pluginLog;
        _logger = logger;
        _userDataManager = userDataManager;
        _userManager = userManager;
    }

    /// <summary>Gets a value indicating whether both local user services are available; missing services never invent watches.</summary>
    internal bool IsAvailable => _userDataManager is not null && _userManager is not null;

    /// <summary>Returns enabled users once per scan, tolerating user-manager outages.</summary>
    /// <returns>The current enabled users, or an empty list when unavailable.</returns>
    internal List<User> GetEnabledUsers()
    {
        try
        {
            return _userManager?.GetUsers()?.Where(static user => !user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled)).ToList() ?? [];
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogCategory, "User lookup failed; watched status will show Never watched.", ex, _logger);
            return [];
        }
    }

    /// <summary>Loads a user's scan-wide batch; null retains the per-item fallback on failures or unavailable batches.</summary>
    /// <param name="user">The enabled scan user.</param>
    /// <param name="items">The distinct video items of this scan.</param>
    /// <returns>The batch map, or null when per-item lookup is required.</returns>
    internal IReadOnlyDictionary<Guid, UserItemData>? TryLoadBatch(User user, IReadOnlyList<BaseItem> items)
    {
        if (_userDataManager is null || items.Count == 0)
        {
            return new Dictionary<Guid, UserItemData>();
        }

        return BatchFallbackHelper.TryRunBatch<IReadOnlyDictionary<Guid, UserItemData>?>(
            batchCall: () => _userDataManager.GetUserDataBatch(items, user),
            fallbackValue: null,
            onFailure: ex => _pluginLog.LogWarning(
                LogCategory,
                $"Batch watch-data load failed for user '{user.Username}'; falling back to per-item lookup.",
                ex,
                _logger));
    }

    /// <summary>Reads a batch entry or falls back for failed, stale or partial batches, without aborting another user's statistics.</summary>
    /// <param name="user">The enabled scan user.</param>
    /// <param name="item">The indexed library item.</param>
    /// <param name="batch">The caller's scan-scoped batch, or null when unavailable.</param>
    /// <param name="filePath">The file path, used only in diagnostics.</param>
    /// <returns>The watch data, or null when unavailable.</returns>
    internal UserItemData? Lookup(User user, BaseItem item, IReadOnlyDictionary<Guid, UserItemData>? batch, string filePath)
    {
        if (batch is not null && batch.TryGetValue(item.Id, out var found))
        {
            return found;
        }

        try
        {
            return _userDataManager?.GetUserData(user, item);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogCategory, $"Skipping watched lookup for user '{user.Username}' on '{filePath}'", ex, _logger);
            return null;
        }
    }
}
