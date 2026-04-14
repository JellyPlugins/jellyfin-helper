using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
/// Result of validating a backup payload before import.
/// Contains a list of validation errors and warnings.
/// </summary>
public class BackupValidationResult
{
    /// <summary>
    /// Gets the list of critical validation errors that prevent import.
    /// </summary>
    public Collection<string> Errors { get; } = new();

    /// <summary>
    /// Gets the list of non-critical warnings (data was sanitized but import can proceed).
    /// </summary>
    public Collection<string> Warnings { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the backup is valid for import (no critical errors).
    /// </summary>
    public bool IsValid => Errors.Count == 0;
}
