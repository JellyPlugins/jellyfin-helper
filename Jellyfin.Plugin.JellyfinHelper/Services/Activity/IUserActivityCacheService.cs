namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Persists user activity results to disk for fast retrieval across restarts.
///     Cache is refreshed each time the scheduled task runs.
/// </summary>
public interface IUserActivityCacheService
{
    /// <summary>
    ///     Saves the activity result to disk.
    /// </summary>
    /// <param name="result">The activity result to persist.</param>
    void SaveResult(UserActivityResult result);

    /// <summary>
    ///     Loads the cached activity result from disk.
    /// </summary>
    /// <returns>The cached result, or null if no cache exists.</returns>
    UserActivityResult? LoadResult();
}