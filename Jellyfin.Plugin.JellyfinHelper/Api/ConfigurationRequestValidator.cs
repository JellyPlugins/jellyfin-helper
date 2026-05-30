using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Validates <see cref="ConfigurationUpdateRequest" /> fields before they are applied.
///     Extracted from <see cref="ConfigurationController" /> to keep the controller focused on HTTP concerns.
/// </summary>
public static class ConfigurationRequestValidator
{
    /// <summary>Maximum allowed value for day-range fields (OrphanMinAgeDays, TrashRetentionDays).</summary>
    private const int MaxDays = 3650;

    /// <summary>Maximum number of Arr instances per type (Radarr / Sonarr).</summary>
    private const int MaxArrInstances = 3;

    /// <summary>
    ///     Validates the given <paramref name="request" /> and returns the first error found, or <c>null</c> if valid.
    /// </summary>
    /// <param name="request">The configuration update request to validate.</param>
    /// <returns>An error message string, or <c>null</c> when the request is valid.</returns>
    public static string? Validate(ConfigurationUpdateRequest request)
    {
        // Numeric range checks
        if (request.OrphanMinAgeDays is < 0 or > MaxDays)
        {
            return "OrphanMinAgeDays must be 0–3650.";
        }

        if (request.TrashRetentionDays is < 0 or > MaxDays)
        {
            return "TrashRetentionDays must be 0–3650.";
        }

        // Arr instance count limits
        if (request.RadarrInstances is { Count: > MaxArrInstances })
        {
            return $"Maximum {MaxArrInstances} Radarr instances allowed.";
        }

        if (request.SonarrInstances is { Count: > MaxArrInstances })
        {
            return $"Maximum {MaxArrInstances} Sonarr instances allowed.";
        }

        // Seerr settings validation - only enforce range when Seerr is actually configured
        if (!string.IsNullOrWhiteSpace(request.SeerrUrl) &&
            request.SeerrCleanupAgeDays is (< 1 or > MaxDays))
        {
            return "SeerrCleanupAgeDays must be 1–3650.";
        }

        // Validate Seerr URL if provided
        if (!string.IsNullOrWhiteSpace(request.SeerrUrl) &&
            (!Uri.TryCreate(request.SeerrUrl, UriKind.Absolute, out var seerrUri) ||
             (seerrUri.Scheme != "http" && seerrUri.Scheme != "https")))
        {
            return "Seerr URL must be a valid http:// or https:// URL.";
        }

        // If Seerr URL is set, API key must also be set
        if (!string.IsNullOrWhiteSpace(request.SeerrUrl) && string.IsNullOrWhiteSpace(request.SeerrApiKey))
        {
            return "Seerr API key is required when a Seerr URL is configured.";
        }

        // Trash folder path validation (block obviously invalid paths from being persisted)
        var trashPathError = ValidateTrashPathStrict(request.TrashFolderPath, request.UseTrash);
        if (trashPathError != null)
        {
            return trashPathError;
        }

        // Arr instance format validation (multi-instance lists)
        var error = ValidateArrInstances(request.RadarrInstances, "Radarr");

        error ??= ValidateArrInstances(request.SonarrInstances, "Sonarr");

        return error;
    }

    /// <summary>
    ///     Performs strict validation of the trash folder path and returns an error message for obviously
    ///     invalid paths (invalid characters, traversal patterns, only-slashes, etc.).
    ///     This validation BLOCKS the save — the configuration will NOT be persisted.
    /// </summary>
    /// <param name="trashFolderPath">The path value from the configuration update request.</param>
    /// <param name="useTrash">Whether the trash feature is enabled.</param>
    /// <returns>An error message string, or <c>null</c> when the path is valid.</returns>
    public static string? ValidateTrashPathStrict(string? trashFolderPath, bool useTrash)
    {
        // When trash is disabled, path is irrelevant — allow save
        if (!useTrash)
        {
            return null;
        }

        // When trash is enabled, path must not be empty
        if (string.IsNullOrWhiteSpace(trashFolderPath))
        {
            return "Trash folder path is required when trash is enabled.";
        }

        // Reject paths that consist only of slashes/backslashes (e.g. "/*", "/\", "\/", "/", "\")
        var trimmed = trashFolderPath.Trim();
        if (trimmed.All(c => c == '/' || c == '\\' || c == '*' || c == '?' || c == '<' || c == '>' || c == '|' || c == '"'))
        {
            return $"Trash folder path '{trashFolderPath}' contains only invalid characters.";
        }

        // Reject control characters (U+0000 to U+001F) — these are never valid in folder names
        // on any platform. This keeps the server-side filter in sync with the UI-side validation
        // which also blocks the full \x00-\x1F range.
        var firstControlChar = trashFolderPath.FirstOrDefault(static c => c < '\x20');
        if (firstControlChar != default || trashFolderPath.Contains('\0', StringComparison.Ordinal))
        {
            return "Trash folder path contains invalid control characters.";
        }

        // Reject individual invalid characters that are never valid in folder names.
        // Note: Cast to char? is required because the array contains characters that could
        // match default(char), making a plain FirstOrDefault unable to distinguish "not found".
        char[] invalidChars = ['*', '?', '<', '>', '|', '"'];
        var firstInvalidChar = invalidChars.Cast<char?>().FirstOrDefault(c => trashFolderPath.Contains(c!.Value, StringComparison.Ordinal));
        if (firstInvalidChar != null)
        {
            return $"Trash folder path contains invalid character '{firstInvalidChar}'.";
        }

        // Reject path traversal patterns (segment-aware to avoid false positives on names like "my..folder")
        var segments = trashFolderPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            return "Trash folder path must not contain '..' sequences.";
        }

        // Reject paths that resolve to the root itself
        if (trimmed is "." or "./" or ".\\")
        {
            return "Trash folder path must not resolve to the library root itself.";
        }

        // Attempt Path.GetFullPath to catch OS-level invalid path issues
        try
        {
            var dummyRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            Path.GetFullPath(trashFolderPath, dummyRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Trash folder path '{trashFolderPath}' is invalid: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    ///     Checks whether <paramref name="trashFolderPath" /> is a relative path that escapes upward via
    ///     <c>..</c> segments. Returns a warning message when suspicious, or <c>null</c> when the path is
    ///     fine. Absolute paths and empty/whitespace values are always considered valid here.
    /// </summary>
    /// <remarks>
    ///     The check is intentionally a warning, not a hard error: <c>ICleanupConfigHelper.GetTrashPath</c>
    ///     already falls back to <c>.jellyfin-trash</c> at runtime, so the system stays safe. The warning
    ///     surfaces the problem to the admin without blocking the save.
    /// </remarks>
    /// <param name="trashFolderPath">The path value from the configuration update request.</param>
    /// <returns>A warning message string, or <c>null</c> when no issue is detected.</returns>
    public static string? ValidateTrashPath(string? trashFolderPath)
    {
        if (string.IsNullOrWhiteSpace(trashFolderPath) || Path.IsPathRooted(trashFolderPath))
        {
            return null;
        }

        // Resolve against a dummy root to detect whether ".." sequences escape upward.
        // We cannot use a real library root here — it is runtime state unknown at config-save time.
        // Use Path.GetTempPath() as a guaranteed-absolute, platform-correct anchor.
        // Path.GetFullPath(path, basePath) is used instead of Path.GetFullPath(Path.Combine(...))
        // to avoid the silent dropped-prefix pitfall when path is rooted (CA2249 / S4347).
        var dummyRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        string resolved;
        try
        {
            resolved = Path.GetFullPath(trashFolderPath, dummyRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"TrashFolderPath '{trashFolderPath}' contains invalid characters or is too long. " +
                   "At runtime it will fall back to '.jellyfin-trash'.";
        }

        var rootPrefix = dummyRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(resolved, dummyRoot, pathComparison))
        {
            return $"TrashFolderPath '{trashFolderPath}' resolves to the library root itself. " +
                   "At runtime it will fall back to '.jellyfin-trash'.";
        }

        if (!resolved.StartsWith(rootPrefix, pathComparison))
        {
            return $"TrashFolderPath '{trashFolderPath}' is a relative path that escapes the library root " +
                   "via '..' sequences. At runtime it will fall back to '.jellyfin-trash'. " +
                   "Use an absolute path if you intend a location outside the library folder.";
        }

        return null;
    }

    /// <summary>
    ///     Validates a list of Arr instances for URL format and non-empty API keys.
    /// </summary>
    /// <param name="instances">The instances to validate.</param>
    /// <param name="typeName">The type name (Radarr/Sonarr) for error messages.</param>
    /// <returns>An error message string, or <c>null</c> if all instances are valid.</returns>
    internal static string? ValidateArrInstances(IReadOnlyList<ArrInstanceConfig>? instances, string typeName)
    {
        if (instances is null)
        {
            return null;
        }

        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];

            // Skip completely empty instances (user may have added a blank row)
            if (string.IsNullOrWhiteSpace(instance.Url) && string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            // If URL is provided, validate format
            if (!string.IsNullOrWhiteSpace(instance.Url) &&
                (!Uri.TryCreate(instance.Url, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https")))
            {
                var instanceName = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
                return
                    $"{typeName} instance '{instanceName}' has an invalid URL. Only http:// and https:// URLs are allowed.";
            }

            // If URL is set, API key must also be set
            if (string.IsNullOrWhiteSpace(instance.Url) || !string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
            return $"{typeName} instance '{label}' has a URL but no API key.";
        }

        return null;
    }
}