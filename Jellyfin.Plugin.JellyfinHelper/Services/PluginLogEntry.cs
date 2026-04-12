using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Represents a single plugin log entry.
/// </summary>
public sealed class PluginLogEntry
{
    /// <summary>
    /// Gets the UTC timestamp of the log entry.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets the log level (DEBUG, INFO, WARN, ERROR).
    /// </summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>
    /// Gets the source component that generated the log entry.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the log message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional exception details.
    /// </summary>
    public string? Exception { get; init; }
}