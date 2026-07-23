namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
/// Interface for the service that creates and restores plugin backups.
/// </summary>
/// <remarks>
/// Static utility methods (<c>SerializeBackup</c>, <c>DeserializeBackup</c>,
/// <c>Validate</c>, <c>Sanitize</c>) and constants remain on the concrete
/// <see cref="BackupService"/> class because they are pure functions
/// with no instance state.
/// </remarks>
public interface IBackupService
{
    /// <summary>
    /// Creates a backup of all exportable plugin data.
    /// </summary>
    /// <param name="includeSecrets">
    ///     When <c>true</c>, API key values are included in the backup.
    ///     When <c>false</c> (the default), all API key fields are replaced with an empty
    ///     string so that the exported file does not contain plaintext credentials.
    /// </param>
    /// <returns>The backup data object ready for serialization.</returns>
    BackupData CreateBackup(bool includeSecrets = false);

    /// <summary>
    /// Restores backup data into the plugin configuration and data files.
    /// Must be called only after validation returns a valid result.
    /// </summary>
    /// <param name="backup">The validated backup data.</param>
    /// <returns>A summary of what was restored.</returns>
    BackupRestoreSummary RestoreBackup(BackupData backup);
}