namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Analyzes user watch activity across all library items and users.
///     Provides per-item summaries with per-user breakdowns including
///     play counts, last watched dates, completion percentages, and more.
/// </summary>
public interface IUserActivityInsightsService
{
    /// <summary>
    ///     Scans all library items and all users to build a complete activity report.
    ///     This is called by the scheduled task and the result is persisted to cache.
    /// </summary>
    /// <returns>The full activity result with per-item/per-user breakdowns.</returns>
    UserActivityResult BuildActivityReport();
}