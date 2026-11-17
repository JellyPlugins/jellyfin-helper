namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>Retains observed Jellyfin state changes in the common activity history when recording is enabled.</summary>
public interface IUserActivityHistoryRecorder
{
    /// <summary>Records a completed activity-task snapshot without inventing playback intervals.</summary>
    /// <param name="snapshot">Existing per-user and per-item Jellyfin activity.</param>
    void Record(UserActivityResult snapshot);
}
