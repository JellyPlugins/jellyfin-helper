using System;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;

/// <summary>
/// Interface for the plugin log service that manages an in-memory ring buffer
/// for plugin-specific log entries with dual-logging support.
/// </summary>
public interface IPluginLogService
{
    /// <summary>
    /// Logs a debug-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    void LogDebug(string source, string message, ILogger? logger = null);

    /// <summary>
    /// Logs an info-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    void LogInfo(string source, string message, ILogger? logger = null);

    /// <summary>
    /// Logs a warning-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    void LogWarning(string source, string message, Exception? exception = null, ILogger? logger = null);

    /// <summary>
    /// Logs an error-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    void LogError(string source, string message, Exception? exception = null, ILogger? logger = null);

    /// <summary>
    /// Gets all log entries, optionally filtered by minimum level and/or source.
    /// Entries are returned newest-first.
    /// </summary>
    /// <param name="minLevel">Optional minimum level filter (DEBUG, INFO, WARN, ERROR).</param>
    /// <param name="source">Optional source filter (partial match).</param>
    /// <param name="limit">Maximum number of entries to return (default 500).</param>
    /// <returns>A read-only collection of matching log entries, newest first.</returns>
    ReadOnlyCollection<PluginLogEntry> GetEntries(string? minLevel = null, string? source = null, int limit = 500);

    /// <summary>
    /// Gets the total number of entries currently stored.
    /// </summary>
    /// <returns>The entry count.</returns>
    int GetCount();

    /// <summary>
    /// Clears all log entries.
    /// </summary>
    void Clear();

    /// <summary>
    /// Exports all entries (or filtered entries) as a plain-text log string for download.
    /// </summary>
    /// <param name="minLevel">Optional minimum level filter.</param>
    /// <param name="source">Optional source filter (partial match).</param>
    /// <returns>A formatted log string.</returns>
    string ExportAsText(string? minLevel = null, string? source = null);
}