using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;

/// <summary>
///     In-memory ring buffer for plugin-specific log entries with dual-logging support.
/// </summary>
public class PluginLogService : IPluginLogService
{
    /// <summary>
    ///     Maximum number of entries stored in the ring buffer.
    /// </summary>
    internal const int MaxEntries = 2000;

    private const string LogTemplate = "[{Source}] {Message}";

    /// <summary>
    ///     Ordered log levels for comparison.
    /// </summary>
    private static readonly string[] LevelOrder = ["DEBUG", "INFO", "WARN", "ERROR"];

    private readonly LinkedList<PluginLogEntry> _buffer = [];
    private readonly IPluginConfigurationService _configService;
    private readonly Lock _lock = new();
    private volatile string? _testMinLevelOverride;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PluginLogService" /> class.
    /// </summary>
    /// <param name="configService">The plugin configuration service.</param>
    public PluginLogService(IPluginConfigurationService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    /// <summary>
    ///     Gets or sets an optional override for the minimum log level. Used by unit tests.
    /// </summary>
    internal string? TestMinLevelOverride
    {
        get => _testMinLevelOverride;
        set => _testMinLevelOverride = value;
    }

    /// <summary>
    ///     Logs a debug-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public void LogDebug(string source, string message, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        var safeSource = SanitizeForLog(source);
        var safeMessage = SanitizeForLog(message);
        if (logger is not null && logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(LogTemplate, safeSource, safeMessage);
        }

        AddEntry("DEBUG", safeSource, safeMessage, null);
    }

    /// <summary>
    ///     Logs an info-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public void LogInfo(string source, string message, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        var safeSource = SanitizeForLog(source);
        var safeMessage = SanitizeForLog(message);
        if (logger is not null && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(LogTemplate, safeSource, safeMessage);
        }

        AddEntry("INFO", safeSource, safeMessage, null);
    }

    /// <summary>
    ///     Logs a warning-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public void LogWarning(string source, string message, Exception? exception = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        var safeSource = SanitizeForLog(source);
        var safeMessage = SanitizeForLog(message);
        // Guard the forwarding call for parity with LogDebug/LogInfo above (CA1873).
        // The null-check on logger is preserved because it is an optional dependency.
        if (logger is not null && logger.IsEnabled(LogLevel.Warning))
        {
            if (exception != null)
            {
                logger.LogWarning(exception, LogTemplate, safeSource, safeMessage);
            }
            else
            {
                logger.LogWarning(LogTemplate, safeSource, safeMessage);
            }
        }

        AddEntry("WARN", safeSource, safeMessage, exception);
    }

    /// <summary>
    ///     Logs an error-level message to the plugin buffer and optionally to Jellyfin's logger.
    /// </summary>
    /// <param name="source">The source component.</param>
    /// <param name="message">The log message.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="logger">Optional Jellyfin ILogger for dual-logging.</param>
    public void LogError(string source, string message, Exception? exception = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        var safeSource = SanitizeForLog(source);
        var safeMessage = SanitizeForLog(message);
        // Guard the forwarding call for parity with LogDebug/LogInfo above (CA1873).
        // The null-check on logger is preserved because it is an optional dependency.
        if (logger is not null && logger.IsEnabled(LogLevel.Error))
        {
            if (exception != null)
            {
                logger.LogError(exception, LogTemplate, safeSource, safeMessage);
            }
            else
            {
                logger.LogError(LogTemplate, safeSource, safeMessage);
            }
        }

        AddEntry("ERROR", safeSource, safeMessage, exception);
    }

    /// <summary>
    ///     Gets all log entries, optionally filtered by minimum level and/or source.
    ///     Entries are returned newest-first.
    /// </summary>
    /// <param name="minLevel">Optional minimum level filter (DEBUG, INFO, WARN, ERROR).</param>
    /// <param name="source">Optional source filter (partial match).</param>
    /// <param name="limit">Maximum number of entries to return (default 500).</param>
    /// <returns>A read-only collection of matching log entries, newest first.</returns>
    public ReadOnlyCollection<PluginLogEntry> GetEntries(
        string? minLevel = null,
        string? source = null,
        int limit = 500)
    {
        lock (_lock)
        {
            IEnumerable<PluginLogEntry> query = _buffer;

            if (!string.IsNullOrEmpty(minLevel))
            {
                var minIndex = GetLevelIndex(minLevel);
                query = query.Where(e => GetLevelIndex(e.Level) >= minIndex);
            }

            if (!string.IsNullOrEmpty(source))
            {
                query = query.Where(e => e.Source.Contains(source, StringComparison.OrdinalIgnoreCase));
            }

            return query.Take(limit).ToList().AsReadOnly();
        }
    }

    /// <summary>
    ///     Gets the total number of entries currently stored.
    /// </summary>
    /// <returns>The entry count.</returns>
    public int GetCount()
    {
        lock (_lock)
        {
            return _buffer.Count;
        }
    }

    /// <summary>
    ///     Clears all log entries.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    /// <summary>
    ///     Exports all entries (or filtered entries) as a plain-text log string for download.
    /// </summary>
    /// <param name="minLevel">Optional minimum level filter.</param>
    /// <param name="source">Optional source filter (partial match).</param>
    /// <returns>A formatted log string.</returns>
    public string ExportAsText(string? minLevel = null, string? source = null)
    {
        var entries = new List<PluginLogEntry>(GetEntries(minLevel, source, MaxEntries));
        var sb = new StringBuilder();
        sb.AppendLine("=== Jellyfin Helper Plugin Logs ===");
        sb.AppendLine(
            string.Create(CultureInfo.InvariantCulture, $"Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"));
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
    ///     Gets the configured minimum log level from plugin configuration.
    ///     Returns "INFO" if no configuration is available.
    /// </summary>
    /// <returns>The minimum log level string.</returns>
    internal string GetConfiguredMinLevel()
    {
        if (TestMinLevelOverride != null)
        {
            return TestMinLevelOverride;
        }

        try
        {
            return _configService.GetConfiguration().PluginLogLevel;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Plugin not initialized yet - use default
        }

        return "INFO";
    }

    /// <summary>
    ///     Gets the numeric index of a log level for comparison.
    /// </summary>
    /// <param name="level">The level string.</param>
    /// <returns>The index (0=DEBUG, 1=INFO, 2=WARN, 3=ERROR).</returns>
    internal static int GetLevelIndex(string level)
    {
        for (var i = 0; i < LevelOrder.Length; i++)
        {
            if (string.Equals(LevelOrder[i], level, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 1; // Default to INFO
    }

    /// <summary>
    ///     Strips CR, LF, and NUL from a string to prevent log-forging via injected newlines.
    /// </summary>
    private static string SanitizeForLog(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Replace('\t', ' ');

    private void AddEntry(string level, string source, string message, Exception? exception)
    {
        // Check against configured minimum level
        var minLevel = GetConfiguredMinLevel();
        if (GetLevelIndex(level) < GetLevelIndex(minLevel))
        {
            return;
        }

        // source and message are already sanitized by the public Log* methods. Cap the exception string at 8192 chars to prevent ExportAsText memory bloat when an exception carries a very large stack trace or inner-exception chain.
        const int MaxExceptionLength = 8192;
        var rawException = exception?.ToString().Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        var sanitizedException = rawException is { Length: > MaxExceptionLength }
            ? rawException[..MaxExceptionLength] + " [truncated]"
            : rawException;

        var entry = new PluginLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Source = source,
            Message = message,
            Exception = sanitizedException
        };

        lock (_lock)
        {
            _buffer.AddFirst(entry); // Newest first

            while (_buffer.Count > MaxEntries)
            {
                _buffer.RemoveLast();
            }
        }
    }
}