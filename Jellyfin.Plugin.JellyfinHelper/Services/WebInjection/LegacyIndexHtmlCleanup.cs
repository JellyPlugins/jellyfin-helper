using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;

/// <summary>
///     Removes the index.html mutations older plugin versions wrote to disk.
/// </summary>
/// <remarks>
///     Injection now happens per request, so an on-disk tag would only keep a dead reference alive
///     after uninstall. Cleanup is best effort and tolerates read-only web directories.
/// </remarks>
internal static class LegacyIndexHtmlCleanup
{
    private const string LogSource = "Discovery";

    /// <summary>
    ///     Strips our script tag from the on-disk index.html when an older version left one behind.
    /// </summary>
    /// <param name="pluginLog">Plugin log for user-visible entries.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="webPath">Jellyfin's web root, or <c>null</c> when unavailable.</param>
    /// <returns><c>true</c> when the file was rewritten.</returns>
    public static bool TryRemoveScriptTag(IPluginLogService pluginLog, ILogger logger, string? webPath)
    {
        if (string.IsNullOrWhiteSpace(webPath))
        {
            return false;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        try
        {
            if (!File.Exists(indexPath))
            {
                return false;
            }

            var original = File.ReadAllText(indexPath);
            var cleaned = IndexHtmlTransformer.Remove(original);
            if (string.Equals(cleaned, original, StringComparison.Ordinal))
            {
                return false;
            }

            AtomicFile.WriteAllText(indexPath, cleaned);
            pluginLog.LogInfo(LogSource, "Removed a legacy on-disk script tag from index.html.", logger);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            pluginLog.LogWarning(LogSource, $"Could not remove the legacy script tag from index.html: {ex.Message}", ex, logger);
            return false;
        }
    }

    /// <summary>
    ///     Deletes atomic-write temp files older versions may have left in the web root.
    /// </summary>
    /// <param name="pluginLog">Plugin log for user-visible entries.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="webPath">Jellyfin's web root, or <c>null</c> when unavailable.</param>
    public static void TryDeleteStaleTempFiles(IPluginLogService pluginLog, ILogger logger, string? webPath)
    {
        if (string.IsNullOrWhiteSpace(webPath))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(webPath))
            {
                return;
            }

            var removed = 0;
            foreach (var file in Directory.GetFiles(webPath, "index.html.*.tmp"))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    pluginLog.LogWarning(LogSource, $"Could not delete stale temp file {Path.GetFileName(file)}: {ex.Message}", ex, logger);
                }
            }

            if (removed > 0)
            {
                pluginLog.LogInfo(LogSource, $"Removed {removed} stale index.html temp file(s) left by an older version.", logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            pluginLog.LogWarning(LogSource, $"Could not scan the web root for stale temp files: {ex.Message}", ex, logger);
        }
    }
}
