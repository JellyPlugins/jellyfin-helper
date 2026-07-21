namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
/// Summary of what was restored during a backup import.
/// </summary>
public class BackupRestoreSummary
{
    /// <summary>
    /// Gets or sets a value indicating whether configuration settings were restored.
    /// </summary>
    public bool ConfigurationRestored { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the growth timeline was restored.
    /// </summary>
    public bool TimelineRestored { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the growth baseline was restored.
    /// </summary>
    public bool BaselineRestored { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether at least one API credential (Seerr or an
    /// Arr instance) was overwritten with a different value from the backup.
    /// When <c>true</c> the caller can surface an appropriate audit notification.
    /// </summary>
    public bool CredentialsChanged { get; set; }
}
