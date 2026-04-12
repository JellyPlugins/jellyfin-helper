using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// In-memory ring buffer for plugin-specific log entries with dual-logging support.
/// Thread-safe singleton that captures structured log messages from all plugin components.
/// When an <see cref="ILogger"/> is provided, messages are forwarded to both the plugin's
/// in-memory buffer AND Jellyfin's standard logging pipeline.
/// Respects the configured minimum log level from <see cref="Configuration.PluginConfiguration"/>.
/// </summary>
public static class PluginLogService
{
    private static readonly object Lock = new();
    private static readonly LinkedList<PluginLogEntry> Buffer = new();

    /// <summary>
    /// Maximum number of entries stored in the ring buffer.
    /// </summary>
    internal const int MaxEntries = 2000;

    /// <summary>
    /// Ordered log levels for comparison.
    /// </summary>
    private static readonly string[] LevelOrder = { "DEBUG", "INFO", "WARN", "ERROR" };

    /// <summary>
    /// Gets or sets an optional override for the minimum log level. Used by unit tests.
    /// When set to a non-null value, this overrides the plugin configuration.
    /// </summary>
    internal static string? TestMinLevelOverride { get; set; }

    /// <summary>
    /// Logs a debug-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public static void LogDebug(string source, string message, ILogger? logger = null)
    {
        logger?.LogDebug("[{Source}] {Message}", source, message);
        AddEntry("DEBUG", source, message, null);
    }

    /// <summary>
    /// Logs an info-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public static void LogInfo(string source, string message, ILogger? logger = null)
    {
        logger?.LogInformation("[{Source}] {Message}", source, message);
        AddEntry("INFO", source, message, null);
    }

    /// <summary>
    /// Logs a warning-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public static void LogWarning(string source, string message, Exception? exception = null, ILogger? logger = null)
    {
        if (exception != null)
        {
            logger?.LogWarning(exception, "[{Source}] {Message}", source, message);
        }
        else
        {
            logger?.LogWarning("[{Source}] {Message}", source, message);
        }

        AddEntry("WARN", source, message, exception);
    }

    /// <summary>
    /// Logs an error-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public static void LogError(string source, string message, Exception? exception = null, ILogger? logger = null)
    {
        if (exception != null)
        {
            logger?.LogError(exception, "[{Source}] {Message}", source, message);
        }
        else
        {
            logger?.LogError("[{Source}] {Message}", source, message);
        }

        AddEntry("ERROR", source, message, exception);
    }

    /// <summary>
    /// Gets all log entries, optionally filtered by minimum level and/or source.
    /// Entries are returned newest-first.
    /// </summary>
    /// <param name="minLevel">Optional minimum level filter (DEBUG, INFO, WARN, ERROR).</param>
    /// <param name="source">Optional source filter (partial match).</param>
    /// <param name="limit">Maximum number of entries to return (default 500).</param>
    /// <returns>A read-only collection of matching log entries, newest first.</returns>
    public static ReadOnlyCollection<PluginLogEntry> GetEntries(string? minLevel = null, string? source = null, int limit = 500)
    {
        lock (Lock)
        {
            IEnumerable<PluginLogEntry> query = Buffer;

            if (!string.IsNullOrEmpty(minLevel))
            {
                int minIndex = GetLevelIndex(minLevel);
                query = query.Where(e => GetLevelIndex(e.Level) >= minIndex);
            }

            if (!string.IsNullOrEmpty(source))
            {
                query = query.Where(e => e.Source?.Contains(source, StringComparison.OrdinalIgnoreCase) == true);
            }

            return query.Take(limit).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets the total number of entries currently stored.
    /// </summary>
    /// <returns>The entry count.</returns>
    public static int GetCount()
    {
        lock (Lock)
        {
            return Buffer.Count;
        }
    }

    /// <summary>
    /// Clears all log entries.
    /// </summary>
    public static void Clear()
    {
        lock (Lock)
        {
            Buffer.Clear();
        }
    }

    /// <summary>
    /// Exports all entries (or filtered entries) as a plain-text log string for download.
    /// </summary>
    /// <param name="minLevel">Optional minimum level filter.</param>
    /// <param name="source">Optional source filter (partial match).</param>
    /// <returns>A formatted log string.</returns>
    public static string ExportAsText(string? minLevel = null, string? source = null)
    {
        var entries = new List<PluginLogEntry>(GetEntries(minLevel, source, MaxEntries));
        var sb = new StringBuilder();
        sb.AppendLine("=== Jellyfin Helper Plugin Logs ===");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Entries: {entries.Count}"));
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();

        // Reverse so oldest is first in exported file
        entries.Reverse();

        foreach (var entry in entries)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}]"));
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" [{entry.Level,-5}]"));
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" [{entry.Source}]"));
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" {entry.Message}"));
            sb.AppendLine();

            if (!string.IsNullOrEmpty(entry.Exception))
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Exception: {entry.Exception}"));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the configured minimum log level from plugin configuration.
    /// Returns "INFO" if no configuration is available.
    /// </summary>
    /// <returns>The minimum log level string.</returns>
    internal static string GetConfiguredMinLevel()
    {
        if (TestMinLevelOverride != null)
        {
            return TestMinLevelOverride;
        }

        try
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                return plugin.Configuration.PluginLogLevel;
            }
        }
        catch
        {
            // Plugin not initialized yet — use default
        }

        return "INFO";
    }

    /// <summary>
    /// Gets the numeric index of a log level for comparison.
    /// </summary>
    /// <param name="level">The level string.</param>
    /// <returns>The index (0=DEBUG, 1=INFO, 2=WARN, 3=ERROR).</returns>
    internal static int GetLevelIndex(string level)
    {
        for (int i = 0; i < LevelOrder.Length; i++)
        {
            if (string.Equals(LevelOrder[i], level, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 1; // Default to INFO
    }

    private static void AddEntry(string level, string source, string message, Exception? exception)
    {
        // Check against configured minimum level
        string minLevel = GetConfiguredMinLevel();
        if (GetLevelIndex(level) < GetLevelIndex(minLevel))
        {
            return;
        }

        var entry = new PluginLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Source = source,
            Message = message,
            Exception = exception?.ToString(),
        };

        lock (Lock)
        {
            Buffer.AddFirst(entry); // Newest first

            while (Buffer.Count > MaxEntries)
            {
                Buffer.RemoveLast();
            }
        }
    }
}