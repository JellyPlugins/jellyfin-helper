using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
/// Defines the execution mode for a scheduled sub-task.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskMode
{
    /// <summary>
    /// The task is deactivated and will be skipped entirely.
    /// </summary>
    Deactivate = 0,

    /// <summary>
    /// The task runs in dry-run mode — it logs what would happen but makes no changes.
    /// This is the default for all tasks (safe mode).
    /// </summary>
    DryRun = 1,

    /// <summary>
    /// The task is fully active and will perform real operations (delete, move, repair).
    /// </summary>
    Activate = 2,
}
